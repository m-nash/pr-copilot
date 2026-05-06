// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Regression tests for EmitComposeReplyAction (PR #51 reviewer comment on
/// MonitorTransitions.cs:1109).
///
/// EmitComposeReplyAction is called from ProcessCommentAddressed/ProcessCommentReplied
/// when the agent fires comment_addressed/comment_replied without including reply_text.
/// Both callers are dispatched from CurrentState == ExecutingTask, so EmitComposeReplyAction
/// is a re-entry into ExecutingTask. compose_reply itself never touches git, so it must
/// preserve the prior task's recovery snapshot (HeadShaAtTaskStart and
/// ExecutingTaskExpectedCompletion) — overwriting them causes the (ExecutingTask, "ready")
/// recovery to misattribute the original task's push to compose_reply and surface a bogus
/// recovery prompt (or auto-dispatch the wrong completion event).
/// </summary>
public class ComposeReplySnapshotTests
{
    private static MonitorState CreateExecutingTaskState() => new()
    {
        Owner = "test-owner",
        Repo = "test-repo",
        PrNumber = 42,
        HeadSha = "stale-pre-push",
        HeadBranch = "feature/test",
        SessionFolder = Path.GetTempPath(),
        CurrentState = MonitorStateId.ExecutingTask,
        CommentFlow = CommentFlowState.SingleCommentPrompt,
        CurrentCommentIndex = 0,
        // Snapshot from the original push-capable task (e.g., address_comment)
        HeadShaAtTaskStart = "original-snapshot",
        ExecutingTaskExpectedCompletion = "comment_addressed"
    };

    private static CommentInfo MakeComment(string author = "human-reviewer") => new()
    {
        Id = "c1",
        Author = author,
        FilePath = "src/File.cs",
        Line = 10,
        Body = "Fix this",
        Url = "https://example/c1"
    };

    [Fact]
    public void CommentAddressed_NoReplyText_PreservesHeadShaSnapshot()
    {
        // Before fix: EmitComposeReplyAction calls EnterExecutingTask which overwrites
        // HeadShaAtTaskStart with state.HeadSha (still pre-push, since the server hasn't
        // refreshed since the agent pushed). The original task's snapshot is lost.
        var state = CreateExecutingTaskState();
        state.UnresolvedComments.Add(MakeComment());
        // PendingReplyText is empty → ProcessCommentAddressed will route to EmitComposeReplyAction.

        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);

        Assert.Equal("compose_reply", action.Task);
        Assert.Equal("original-snapshot", state.HeadShaAtTaskStart);
    }

    [Fact]
    public void CommentAddressed_NoReplyText_PreservesExpectedCompletion()
    {
        // Before fix: EnterExecutingTask resets ExecutingTaskExpectedCompletion to null,
        // turning an unambiguous task ("comment_addressed") into the ambiguous branch
        // → bogus ask_user recovery prompt instead of auto-dispatch.
        var state = CreateExecutingTaskState();
        state.UnresolvedComments.Add(MakeComment());

        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);

        Assert.Equal("compose_reply", action.Task);
        Assert.Equal("comment_addressed", state.ExecutingTaskExpectedCompletion);
    }

    [Fact]
    public void CommentReplied_NoReplyText_PreservesHeadShaSnapshot()
    {
        var state = CreateExecutingTaskState();
        state.UnresolvedComments.Add(MakeComment());
        state.ExecutingTaskExpectedCompletion = null; // apply_recommendation is ambiguous

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        Assert.Equal("compose_reply", action.Task);
        Assert.Equal("original-snapshot", state.HeadShaAtTaskStart);
    }
}
