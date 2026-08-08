// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Regression tests for treat_as_replied_externally routing in ProcessCommentChoice
/// (PR #51 reviewer comment on MonitorTransitions.cs:680).
///
/// "Treat as comment replied" (-> "treat_as_replied_externally") was unconditionally
/// routed to SkipAndAdvanceComment, which always shows the AddressAllIterating-style
/// "Address this comment / Skip / Done" prompt for the next comment. That's wrong for
/// ExplainAllIterating: the user is in the explain-all loop and should see the
/// explain_comment task for the next thread, not an address prompt — i.e., treat_as_replied
/// should be flow-aware just like the normal comment_replied event (which routes through
/// AdvanceAfterComment -> AdvanceExplainAll for the explain-all flow).
/// </summary>
public class TreatAsRepliedExternallyRoutingTests
{
    private static MonitorState BaseState() => new()
    {
        Owner = "test-owner",
        Repo = "test-repo",
        PrNumber = 42,
        HeadSha = "abc123",
        HeadBranch = "feature/test",
        SessionFolder = Path.GetTempPath(),
        CurrentState = MonitorStateId.AwaitingUser
    };

    private static CommentInfo MakeComment(string id) => new()
    {
        Id = id,
        Author = "human-reviewer",
        FilePath = "src/File.cs",
        Line = 10,
        Body = $"Comment {id}",
        Url = $"https://example/{id}"
    };

    [Fact]
    public void TreatAsRepliedExternally_InExplainAllIterating_AdvancesViaExplainAll()
    {
        var state = BaseState();
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments.Add(MakeComment("c1"));
        state.UnresolvedComments.Add(MakeComment("c2"));
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_replied_externally", null);

        // Should re-emit explain task for next comment, not the "Address this comment" prompt
        Assert.Equal("execute", action.Action);
        Assert.Equal("explain_comment", action.Task);
        Assert.Equal(1, state.CurrentCommentIndex);
        Assert.Equal(CommentFlowState.ExplainAllIterating, state.CommentFlow);
    }

    [Fact]
    public void TreatAsRepliedExternally_InAddressAllIterating_AsksAddressOrSkip_Regression()
    {
        var state = BaseState();
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments.Add(MakeComment("c1"));
        state.UnresolvedComments.Add(MakeComment("c2"));
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_replied_externally", null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Address this comment", action.Choices ?? []);
        Assert.Equal(1, state.CurrentCommentIndex);
    }

    [Fact]
    public void TreatAsRepliedExternally_InSingleCommentPrompt_GoesToPickRemaining_Regression()
    {
        var state = BaseState();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(MakeComment("c1"));
        state.UnresolvedComments.Add(MakeComment("c2"));
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_replied_externally", null);

        // Single-comment branch: AdvanceAfterComment falls through to PickRemaining
        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.PickRemaining, state.CommentFlow);
        Assert.Contains("Address next comment", action.Choices ?? []);
    }

    [Fact]
    public void TreatAsRepliedExternally_InExplainAllIterating_AtLastComment_StopsFlow()
    {
        var state = BaseState();
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments.Add(MakeComment("c1"));
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_replied_externally", null);

        // No more comments → AdvanceExplainAll falls through to TransitionToPolling
        Assert.Equal("polling", action.Action);
    }
}
