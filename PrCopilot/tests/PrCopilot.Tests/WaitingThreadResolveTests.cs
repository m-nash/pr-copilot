// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Regression tests for ProcessTaskComplete handling of waiting-thread resolutions
/// (PR #51 reviewer comment on MonitorTransitions.cs:921).
///
/// The waiting-thread branches in ProcessCommentAddressed/ProcessCommentReplied set
/// PendingResolveAfterAddress / PendingAdvanceAfterReply but do NOT enter a comment flow
/// (CommentFlow stays None). When ProcessTaskComplete then runs, it indexes
/// UnresolvedComments[CurrentCommentIndex] — which can point to a stale, unrelated
/// comment from a prior flow — and may trigger a bogus rerequest_review on that
/// reviewer or surface PickRemaining prompts for unrelated comments instead of just
/// returning to monitoring.
/// </summary>
public class WaitingThreadResolveTests
{
    private static MonitorState BaseState() => new()
    {
        Owner = "test-owner",
        Repo = "test-repo",
        PrAuthor = "pr-author",
        CurrentUser = "current-user",
        PrNumber = 42,
        HeadSha = "abc123",
        HeadBranch = "feature/test",
        SessionFolder = Path.GetTempPath(),
        CurrentState = MonitorStateId.ExecutingTask
    };

    private static CommentInfo MakeComment(string id, string author) => new()
    {
        Id = id,
        Author = author,
        FilePath = "src/File.cs",
        Line = 10,
        Body = $"Comment {id}",
        Url = $"https://example/{id}"
    };

    [Fact]
    public void TaskComplete_AfterWaitingThreadResolve_TransitionsToPolling()
    {
        // Simulates: waiting branch of ProcessCommentReplied (bot author) just resolved
        // the waiting thread. ProcessTaskComplete is now firing. UnresolvedComments
        // contains a stale entry from a prior flow with a different reviewer who has NOT
        // been re-requested. The bug: ProcessTaskComplete indexes UnresolvedComments[0]
        // and calls ShouldReRequestReview on "other-reviewer" → triggers bogus rerequest.
        var state = BaseState();
        state.CommentFlow = CommentFlowState.None;
        state.ActiveWaitingComment = MakeComment("waiting", "some-bot[bot]");
        state.UnresolvedComments.Add(MakeComment("stale", "other-reviewer"));
        state.CurrentCommentIndex = 0;
        state.PendingResolveAfterAddress = true;
        state.PendingResolveSummary = "Comment addressed";

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Null(state.PendingReRequestReviewer);
        Assert.DoesNotContain("other-reviewer", state.ReviewsReRequested);
        Assert.Null(state.ActiveWaitingComment);
        Assert.False(state.PendingResolveAfterAddress);
    }

    [Fact]
    public void TaskComplete_AfterWaitingThreadReplyPosted_TransitionsToPolling()
    {
        // Simulates: waiting branch of ProcessCommentReplied (human author) just posted
        // a reply on the waiting thread. ProcessTaskComplete is now firing via
        // PendingAdvanceAfterReply. Same indexing bug as above.
        var state = BaseState();
        state.CommentFlow = CommentFlowState.None;
        state.ActiveWaitingComment = MakeComment("waiting", "human-reviewer");
        state.WaitingForReplyComments.Add(state.ActiveWaitingComment);
        state.UnresolvedComments.Add(MakeComment("stale", "other-reviewer"));
        state.CurrentCommentIndex = 0;
        state.PendingAdvanceAfterReply = "Replied to comment";

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Null(state.PendingReRequestReviewer);
        Assert.DoesNotContain("other-reviewer", state.ReviewsReRequested);
        Assert.Null(state.PendingAdvanceAfterReply);
    }

    [Fact]
    public void TaskComplete_InActiveCommentFlow_StillTriggersRerequestReview_Regression()
    {
        // Regression: the in-flow path (CommentFlow != None) must STILL re-request review
        // when appropriate. The fix only short-circuits the waiting-thread case
        // (CommentFlow == None).
        var state = BaseState();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(MakeComment("c1", "in-flow-reviewer"));
        state.CurrentCommentIndex = 0;
        state.PendingResolveAfterAddress = true;
        state.PendingResolveSummary = "Comment addressed";

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        // Only comment for in-flow-reviewer is the one we just resolved → ShouldReRequestReview
        // returns true → BuildReRequestReviewAction fires.
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("request_review", action.Task);
        Assert.Equal("in-flow-reviewer", state.PendingReRequestReviewer);
    }
}
