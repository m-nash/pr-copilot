// Licensed under the MIT License.

using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

/// <summary>
/// Tests for <see cref="MonitorFlowTools.BuildFreeformInterpretAction"/> and the
/// structured <see cref="FreeformInterpretContext"/> payload it produces. These
/// guard the cross-context-boundary fix: when MCP elicitation captures a freeform
/// reply, the agent's chat history has no record of the prompt or reply, so the
/// payload must carry every piece of context the agent needs to act intelligently
/// (comment, recommendation, question shown, user reply).
/// </summary>
public class FreeformInterpretContextTests
{
    private static ElicitChoiceResult MakeFreeformResult(string text, string question, params string[] choices) =>
        new()
        {
            Value = text,
            IsFreeform = true,
            OriginalQuestion = question,
            OriginalChoices = choices.ToList()
        };

    [Fact]
    public void BuildFreeformInterpretAction_PopulatesContextField()
    {
        var state = new MonitorState();
        var result = MakeFreeformResult("just merge it", "What now?", "Merge the PR", "I'll handle it myself");

        var action = MonitorFlowTools.BuildFreeformInterpretAction(result, state);

        Assert.Equal("execute", action.Action);
        Assert.Equal("interpret_freeform", action.Task);
        Assert.NotNull(action.Context);
        Assert.IsType<FreeformInterpretContext>(action.Context);
    }

    [Fact]
    public void BuildFreeformInterpretAction_InstructionsContainBoundaryNotice()
    {
        // The whole point of the fix: the agent must be told that elicitation happened
        // out of band from chat. Without this prefix, the agent dismisses the reply as
        // stale state when it doesn't match its chat memory.
        var state = new MonitorState();
        var result = MakeFreeformResult("write a test first", "What now?", "Fix it");

        var action = MonitorFlowTools.BuildFreeformInterpretAction(result, state);

        Assert.NotNull(action.Instructions);
        Assert.Contains("MCP elicitation", action.Instructions);
        Assert.Contains("authoritative", action.Instructions);
        Assert.Contains("chat history", action.Instructions);
    }

    [Fact]
    public void BuildFreeformInterpretContext_CapturesUserReplyVerbatim()
    {
        var state = new MonitorState();
        var result = MakeFreeformResult("Lets write a test that proves this", "What now?", "Fix it");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("Lets write a test that proves this", ctx.UserReply.Text);
        Assert.True(ctx.UserReply.IsFreeform);
        Assert.Equal("custom_instruction", ctx.UserReply.SamplingClassification);
    }

    [Fact]
    public void BuildFreeformInterpretContext_CapturesElicitationQuestionAndChoices()
    {
        var state = new MonitorState();
        var result = MakeFreeformResult("custom thing", "How should I handle this comment?",
            "Address this comment", "I'll handle it myself");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("How should I handle this comment?", ctx.Elicitation.Question);
        Assert.Equal(2, ctx.Elicitation.Choices.Count);
        Assert.Equal("Address this comment", ctx.Elicitation.Choices[0].Display);
        Assert.Equal("address", ctx.Elicitation.Choices[0].Value);
        Assert.Equal("I'll handle it myself", ctx.Elicitation.Choices[1].Display);
        Assert.Equal("handle_myself", ctx.Elicitation.Choices[1].Value);
    }

    [Fact]
    public void BuildFreeformInterpretContext_CommentFlow_AttachesActiveCommentAndRecommendation()
    {
        // This is the bug-trigger case from the bug report: the user replies "write a
        // test that proves this" — "this" refers to the recommendation about the cache
        // key. Without comment + recommendation in context, the agent can't resolve
        // what "this" means.
        var state = new MonitorState
        {
            CommentFlow = CommentFlowState.SingleCommentPrompt,
            CurrentCommentIndex = 0,
            UnresolvedComments =
            [
                new CommentInfo
                {
                    Id = "thread-1",
                    Author = "copilot-pull-request-reviewer[bot]",
                    FilePath = "src/CredentialResolverEngine.cs",
                    Line = 74,
                    Body = "CredentialCache is keyed only by the merged credential section content.",
                    Url = "https://github.com/owner/repo/pull/1#discussion_r1"
                }
            ],
            LastRecommendation = "Salt the cache key with a resolver-chain fingerprint."
        };
        var result = MakeFreeformResult("Lets write a test that proves this",
            "How would you like to proceed?", "Apply the recommendation", "I'll handle it myself");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("comment", ctx.FlowType);
        Assert.NotNull(ctx.Comment);
        Assert.Equal("copilot-pull-request-reviewer[bot]", ctx.Comment!.Author);
        Assert.Equal("src/CredentialResolverEngine.cs", ctx.Comment.FilePath);
        Assert.Equal(74, ctx.Comment.Line);
        Assert.Contains("CredentialCache", ctx.Comment.Body);
        Assert.Equal("https://github.com/owner/repo/pull/1#discussion_r1", ctx.Comment.Url);

        Assert.NotNull(ctx.ServerAnalysis);
        Assert.Equal("Salt the cache key with a resolver-chain fingerprint.", ctx.ServerAnalysis!.Recommendation);
        Assert.Null(ctx.CiFailure);
    }

    [Fact]
    public void BuildFreeformInterpretContext_CommentFlow_NoRecommendation_OmitsServerAnalysis()
    {
        var state = new MonitorState
        {
            CommentFlow = CommentFlowState.SingleCommentPrompt,
            UnresolvedComments = [new CommentInfo { Author = "alice", FilePath = "f.cs", Body = "..." }],
            LastRecommendation = null
        };
        var result = MakeFreeformResult("ok", "Q?", "A");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("comment", ctx.FlowType);
        Assert.NotNull(ctx.Comment);
        Assert.Null(ctx.ServerAnalysis);
    }

    [Fact]
    public void BuildFreeformInterpretContext_CommentFlow_OutOfRangeIndex_OmitsComment()
    {
        // Defensive: if CurrentCommentIndex is past the end (shouldn't happen but state
        // can drift), don't IndexOutOfRange — just omit the comment.
        var state = new MonitorState
        {
            CommentFlow = CommentFlowState.SingleCommentPrompt,
            CurrentCommentIndex = 5,
            UnresolvedComments = [new CommentInfo { Author = "alice", FilePath = "f.cs", Body = "..." }]
        };
        var result = MakeFreeformResult("ok", "Q?", "A");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("comment", ctx.FlowType);
        Assert.Null(ctx.Comment);
    }

    [Fact]
    public void BuildFreeformInterpretContext_CiFailureFlow_AttachesFailedChecksAndAnalysis()
    {
        var state = new MonitorState
        {
            CiFailureFlow = CiFailureFlowState.InvestigationResults,
            FailedChecks =
            [
                new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://example/build" },
                new FailedCheckInfo { Name = "test", Conclusion = "failure", Url = "https://example/test" }
            ],
            InvestigationFindings = "Null reference in ConfigParser.",
            SuggestedFix = "Add null check at line 42.",
            LastRecommendation = "Apply the suggested null check fix."
        };
        var result = MakeFreeformResult("explain more", "What now?", "Apply the recommendation");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("ci_failure", ctx.FlowType);
        Assert.NotNull(ctx.CiFailure);
        Assert.Equal(2, ctx.CiFailure!.FailedChecks.Count);
        Assert.Equal("build", ctx.CiFailure.FailedChecks[0].Name);
        Assert.Equal("failure", ctx.CiFailure.FailedChecks[0].Conclusion);

        Assert.NotNull(ctx.ServerAnalysis);
        Assert.Equal("Null reference in ConfigParser.", ctx.ServerAnalysis!.InvestigationFindings);
        Assert.Equal("Add null check at line 42.", ctx.ServerAnalysis.SuggestedFix);
        Assert.Equal("Apply the suggested null check fix.", ctx.ServerAnalysis.Recommendation);
        Assert.Null(ctx.Comment);
    }

    [Fact]
    public void BuildFreeformInterpretContext_NoActiveFlow_DefaultsToGeneric()
    {
        var state = new MonitorState();
        var result = MakeFreeformResult("do the thing", "Q?", "A");

        var ctx = MonitorFlowTools.BuildFreeformInterpretContext(result, state);

        Assert.Equal("generic", ctx.FlowType);
        Assert.Null(ctx.Comment);
        Assert.Null(ctx.CiFailure);
        Assert.Null(ctx.ServerAnalysis);
    }

    [Fact]
    public void BuildFreeformInterpretAction_CarriesMonitorIdForMultiPrMode()
    {
        var state = new MonitorState();
        var result = MakeFreeformResult("ok", "Q?", "A");

        var action = MonitorFlowTools.BuildFreeformInterpretAction(result, state, monitorId: "pr-owner-repo-42");

        Assert.Equal("pr-owner-repo-42", action.MonitorId);
    }
}
