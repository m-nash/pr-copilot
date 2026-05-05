// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Tests for the (ExecutingTask, "ready") recovery path and the flow-aware
/// non-destructive recovery from ExecutingTask. See MonitorTransitions.cs:
/// BuildRecoverFromReadyAction and BuildExecutingTaskRecoveryPrompt.
/// </summary>
public class ExecutingTaskReadyRecoveryTests
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

    private static CommentInfo MakeComment(string id = "c1") => new()
    {
        Id = id,
        Author = "reviewer1",
        FilePath = "src/File.cs",
        Line = 10,
        Body = "Please fix this",
        Url = "https://github.com/test/pr/42#comment"
    };

    // ────────────────────────────────────────────────────────────────────
    // Fix A: HEAD-SHA snapshot captured on every ExecutingTask entry
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnterExecutingTask_CapturesCurrentHeadShaAsSnapshot()
    {
        var state = CreateState();
        state.HeadSha = "abc123";

        state.EnterExecutingTask();

        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
        Assert.Equal("abc123", state.HeadShaAtTaskStart);
    }

    [Fact]
    public void EnterExecutingTask_RefreshesSnapshotOnSubsequentEntries()
    {
        // After AwaitingUser → ExecutingTask cycles, snapshot must reflect the latest entry,
        // not the original task-start. Otherwise back-and-forth in a long flow would always
        // detect "push happened" against a stale baseline.
        var state = CreateState();
        state.HeadSha = "abc123";
        state.EnterExecutingTask();
        Assert.Equal("abc123", state.HeadShaAtTaskStart);

        state.CurrentState = MonitorStateId.AwaitingUser;
        state.HeadSha = "def456"; // a push happened during the previous sub-task
        state.EnterExecutingTask();

        Assert.Equal("def456", state.HeadShaAtTaskStart);
    }

    // ────────────────────────────────────────────────────────────────────
    // Fix A: (ExecutingTask, "ready") returns the auto_execute recovery hook
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessEvent_ExecutingTaskReady_ReturnsAutoExecuteRecoverTask()
    {
        // The state machine itself can't perform an HTTP HEAD-fetch — it returns an
        // auto_execute action that the wrapper executes (see ExecuteAutoAction case
        // "recover_from_ready_in_executing_task" in MonitorFlowTools).
        var state = CreateState();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(MakeComment());
        state.EnterExecutingTask();

        var action = MonitorTransitions.ProcessEvent(state, "ready", null, null);

        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("recover_from_ready_in_executing_task", action.Task);
        // Must NOT have wiped flow state — we still need to know the active comment when
        // the wrapper finishes its HEAD check and routes to the right completion event.
        Assert.Equal(CommentFlowState.SingleCommentPrompt, state.CommentFlow);
        Assert.Single(state.UnresolvedComments);
    }

    // ────────────────────────────────────────────────────────────────────
    // Fix B: RecoverFromUnexpectedState is now flow-aware when prior=ExecutingTask
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecoverFromUnexpectedState_FromExecutingTaskWithCommentFlow_PreservesFlowAndOffersTreatAsAddressed()
    {
        // Regression: previously we wiped CommentFlow + cleared ActiveWaitingComment, so
        // "Resume monitoring" was the ONLY useful option and the user lost the in-progress
        // comment loop. Now the flow is preserved so a follow-up "Treat as comment addressed"
        // can dispatch into ProcessCommentAddressed and finish the comment cleanly.
        var state = CreateState();
        var comment = MakeComment();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(comment);
        state.ActiveWaitingComment = comment;
        state.EnterExecutingTask();

        // Send an event with no transition from ExecutingTask — falls to default recovery.
        var action = MonitorTransitions.ProcessEvent(state, "completely_made_up_event", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Treat as comment addressed", action.Choices!);
        Assert.Contains("Skip this comment", action.Choices!);
        // Flow state preserved so the next user_chose can resume in-flow:
        Assert.Equal(CommentFlowState.SingleCommentPrompt, state.CommentFlow);
        Assert.Same(comment, state.ActiveWaitingComment);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
    }

    [Fact]
    public void RecoverFromUnexpectedState_FromExecutingTaskWithCiFailureFlow_PreservesFlowAndOffersTreatAsPushed()
    {
        var state = CreateState();
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.EnterExecutingTask();

        var action = MonitorTransitions.ProcessEvent(state, "completely_made_up_event", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Treat as push completed", action.Choices!);
        Assert.Equal(CiFailureFlowState.InvestigationResults, state.CiFailureFlow);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
    }

    [Fact]
    public void RecoverFromUnexpectedState_FromExecutingTaskWithNoFlow_OffersGenericChoices()
    {
        var state = CreateState();
        state.EnterExecutingTask();

        var action = MonitorTransitions.ProcessEvent(state, "completely_made_up_event", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(["Resume monitoring", "Stop monitoring"], action.Choices);
    }

    [Fact]
    public void RecoverFromUnexpectedState_FromNonExecutingTaskState_StillWipesFlowStateAsBefore()
    {
        // The destructive recovery is intentional for non-ExecutingTask prior states —
        // those represent legitimately confusing situations where the safest action is
        // to reset to a known clean baseline. Don't regress that behavior.
        var state = CreateState();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.ActiveWaitingComment = MakeComment();
        state.CurrentState = MonitorStateId.Polling;

        var action = MonitorTransitions.ProcessEvent(state, "completely_made_up_event", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.None, state.CommentFlow);
        Assert.Null(state.ActiveWaitingComment);
    }

    // ────────────────────────────────────────────────────────────────────
    // Fix B: "treat_as_addressed" / "treat_as_pushed" choices route correctly
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void UserChose_TreatAsAddressed_DispatchesIntoProcessCommentAddressed()
    {
        var state = CreateState();
        var comment = MakeComment();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(comment);
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.AwaitingUser;
        // No PendingReplyText → ProcessCommentAddressed should ask the agent to compose one
        // (rather than blowing up). This is the path used after the wrapper detects that
        // HEAD changed but the agent hasn't supplied reply text yet.
        state.PendingReplyText = null;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_addressed", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("compose_reply", action.Task);
    }

    [Fact]
    public void UserChose_TreatAsPushed_TransitionsToPolling()
    {
        var state = CreateState();
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_pushed", null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
        Assert.Equal(CiFailureFlowState.None, state.CiFailureFlow);
    }

    [Fact]
    public void ChoiceValueMap_HasTreatAsAddressedAndTreatAsPushedEntries()
    {
        // Display strings used in BuildExecutingTaskRecoveryPrompt must round-trip through
        // ChoiceValueMap so the wrapper can normalize the user's selection to the expected
        // internal value before invoking ProcessEvent.
        Assert.Equal("treat_as_addressed", MonitorTransitions.ChoiceValueMap["Treat as comment addressed"]);
        Assert.Equal("treat_as_pushed", MonitorTransitions.ChoiceValueMap["Treat as push completed"]);
    }

    // ────────────────────────────────────────────────────────────────────
    // No-push fallback prompt — must NOT leak the synthetic "ready_unresolved"
    // event name into the user-facing question text. Reviewer comment #3 on PR #51.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_CommentFlow_NoPushReason_DoesNotLeakSyntheticEventName()
    {
        // Before the fix, the auto_execute no-push fallback called
        // ProcessEvent(state, "ready_unresolved", ...) which routed through the generic
        // recovery and interpolated the literal string "ready_unresolved" into the prompt.
        // The user saw "Unexpected event 'ready_unresolved' while addressing a comment ..."
        // which exposes an internal name. The fix takes a user-friendly reasonText instead.
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment());
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.SingleCommentPrompt,
            priorCiFailureFlow: CiFailureFlowState.None);

        Assert.Equal("ask_user", action.Action);
        Assert.NotNull(action.Question);
        Assert.DoesNotContain("ready_unresolved", action.Question);
        Assert.DoesNotContain("Unexpected event", action.Question);
        Assert.Contains("without a new commit", action.Question);
        Assert.Contains("Treat as comment addressed", action.Choices!);
    }

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_CiFlow_NoPushReason_DoesNotLeakSyntheticEventName()
    {
        var state = CreateState();
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.None,
            priorCiFailureFlow: CiFailureFlowState.InvestigationResults);

        Assert.NotNull(action.Question);
        Assert.DoesNotContain("ready_unresolved", action.Question);
        Assert.DoesNotContain("Unexpected event", action.Question);
        Assert.Contains("without a new commit", action.Question);
        Assert.Contains("Treat as push completed", action.Choices!);
    }

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_NoActiveFlow_NoPushReason_DoesNotLeakSyntheticEventName()
    {
        var state = CreateState();
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.None,
            priorCiFailureFlow: CiFailureFlowState.None);

        Assert.NotNull(action.Question);
        Assert.DoesNotContain("ready_unresolved", action.Question);
        Assert.DoesNotContain("ExecutingTask/", action.Question);
        Assert.Contains("without a new commit", action.Question);
    }

    [Fact]
    public void RecoverFromUnexpectedState_FromExecutingTaskWithGenuineUnknownEvent_StillSurfacesEventNameForDebugging()
    {
        // The truly-unknown event path (an actual bug — agent sent something we don't know
        // about) keeps surfacing the event name because it's useful debug info. Only the
        // designed no-push fallback should hide the synthetic name.
        var state = CreateState();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(MakeComment());
        state.EnterExecutingTask();

        var action = MonitorTransitions.ProcessEvent(state, "totally_made_up_event_xyz", null, null);

        Assert.NotNull(action.Question);
        // Genuine unknown events still get the event name interpolated for debug visibility:
        Assert.Contains("totally_made_up_event_xyz", action.Question);
    }
}
