// Licensed under the MIT License.

using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

/// <summary>
/// Tests for freeform Path B handling when the user is on a waiting-for-reply
/// comment thread (state.ActiveWaitingComment != null, CommentFlow == None,
/// CiFailureFlow == None). A freeform reply that doesn't map to "Resolve" or
/// "Go back" must still route through the comment-reply/comment-addressed flow
/// rather than collapsing into the generic task_complete branch — otherwise
/// ProcessTaskComplete clears ActiveWaitingComment and abandons the thread.
/// See PR #51 reviewer comment on MonitorFlowTools.cs:183.
/// </summary>
public class WaitingCommentFreeformTests
{
    private static MonitorState CreateState() => new()
    {
        Owner = "test-owner",
        Repo = "test-repo",
        PrNumber = 42,
        HeadSha = "abc123",
        HeadBranch = "feature/test",
        SessionFolder = Path.GetTempPath()
    };

    private static CommentInfo MakeComment(string id = "wait1", string author = "human-reviewer") => new()
    {
        Id = id,
        Author = author,
        FilePath = "src/File.cs",
        Line = 10,
        Body = "Are you sure this handles null?",
        Url = "https://github.com/test/pr/42#comment"
    };

    private static ElicitChoiceResult MakeFreeformResult(string text) => new()
    {
        Value = text,
        IsFreeform = true,
        OriginalQuestion = "What do you want to do?",
        OriginalChoices = ["Resolve this thread", "Go back to monitoring"]
    };

    // ────────────────────────────────────────────────────────────────────
    // Path B instructions in waiting-comment context
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PathBInstructions_WaitingComment_MentionsCommentAddressedAndReplied()
    {
        // Regression: previously fell into the generic branch which only mentions
        // task_complete. The agent then fired task_complete which made
        // ProcessTaskComplete clear ActiveWaitingComment and resume polling,
        // dropping the thread context.
        var state = CreateState();
        state.ActiveWaitingComment = MakeComment();
        var result = MakeFreeformResult("draft another reply explaining the null guard");

        var action = MonitorFlowTools.BuildFreeformInterpretAction(result, state);

        Assert.Contains("comment_addressed", action.Instructions);
        Assert.Contains("comment_replied", action.Instructions);
    }

    [Fact]
    public void PathBInstructions_WaitingComment_IncludesActiveCommentContext()
    {
        var state = CreateState();
        state.ActiveWaitingComment = MakeComment(id: "thread-99");
        var result = MakeFreeformResult("resolve it now");

        var action = MonitorFlowTools.BuildFreeformInterpretAction(result, state);

        // Must surface the active waiting thread so the agent acts on the right comment.
        Assert.Contains("Are you sure this handles null?", action.Instructions);
    }

    [Fact]
    public void PathBInstructions_NoFlowAndNoWaitingComment_StillUsesGenericBranch()
    {
        // Regression: don't accidentally apply waiting-comment branch when there's
        // no waiting comment.
        var state = CreateState();
        var result = MakeFreeformResult("just print hello");

        var action = MonitorFlowTools.BuildFreeformInterpretAction(result, state);

        Assert.Contains("task_complete", action.Instructions);
        Assert.DoesNotContain("comment_addressed", action.Instructions);
        Assert.DoesNotContain("comment_replied", action.Instructions);
    }

    // ────────────────────────────────────────────────────────────────────
    // ProcessCommentAddressed / ProcessCommentReplied dispatch in waiting-comment context
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommentAddressed_WaitingCommentNoFlow_ResolvesActiveWaitingThread()
    {
        // Agent finished the freeform task with code changes and fires comment_addressed.
        // Without a comment flow there's no UnresolvedComments[index] to operate on —
        // the dispatcher must use ActiveWaitingComment.
        var state = CreateState();
        var comment = MakeComment(id: "thread-7");
        state.ActiveWaitingComment = comment;
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.PendingReplyText = "Fixed the null guard in commit abc.";

        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);

        // Should resolve the waiting thread, not fall through to AdvanceAfterCommentAddressed
        // (which would just transition to polling and lose the resolve).
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);
        Assert.Same(comment, action.Context);
        Assert.True(state.PendingResolveAfterAddress);
    }

    [Fact]
    public void CommentReplied_WaitingCommentNoFlow_HumanReviewer_PostsReplyOnActiveWaitingThread()
    {
        var state = CreateState();
        var comment = MakeComment(id: "thread-8", author: "human-reviewer");
        state.ActiveWaitingComment = comment;
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.PendingReplyText = "Yes, the guard handles null — see the test added in commit xyz.";

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Human reviewer + reply-only → post the reply on the existing waiting thread.
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("post_thread_reply", action.Task);
        Assert.Same(comment, action.Context);
    }

    [Fact]
    public void CommentReplied_WaitingCommentNoFlow_BotReviewer_AutoResolves()
    {
        var state = CreateState();
        var comment = MakeComment(id: "thread-9", author: "copilot-pull-request-reviewer[bot]");
        state.ActiveWaitingComment = comment;
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.PendingReplyText = "Acknowledged — see analysis above.";

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Bot reviewer won't follow up → resolve the thread immediately.
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);
        Assert.Same(comment, action.Context);
    }

    [Fact]
    public void CommentAddressed_NoFlowNoWaitingComment_StillSafelyAdvances()
    {
        // Regression: when there's neither a flow nor a waiting comment, the dispatcher
        // must not crash — it should fall through to the existing AdvanceAfterCommentAddressed
        // path (which transitions back to polling).
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;

        // Should not throw.
        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.NotNull(action);
    }
}
