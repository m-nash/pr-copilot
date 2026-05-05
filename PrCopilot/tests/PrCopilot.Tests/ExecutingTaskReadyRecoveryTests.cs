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

    // ────────────────────────────────────────────────────────────────────
    // Comment-flow recovery must offer both completion paths: the existing
    // "Treat as comment addressed" (resolves thread) AND a new "Treat as
    // comment replied" path that advances without posting anything new
    // (user already handled the reply outside the loop). Reviewer comment
    // #4 on PR #51 — the original recovery only exposed the addressed path,
    // so a pushback/clarification flow that finished without push would force
    // the user to either resolve the thread (wrong) or reset the flow state.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_CommentFlow_OffersTreatAsCommentRepliedChoice()
    {
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment());
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.SingleCommentPrompt,
            priorCiFailureFlow: CiFailureFlowState.None);

        Assert.NotNull(action.Choices);
        Assert.Contains("Treat as comment addressed", action.Choices!);
        Assert.Contains("Treat as comment replied", action.Choices!);
    }

    [Fact]
    public void ChoiceValueMap_HasTreatAsRepliedExternallyEntry()
    {
        Assert.Equal(
            "treat_as_replied_externally",
            MonitorTransitions.ChoiceValueMap["Treat as comment replied"]);
    }

    [Fact]
    public void ProcessEvent_TreatAsRepliedExternally_AdvancesWithoutPostingOrResolving()
    {
        // Two unresolved comments. User picks "Treat as comment replied" on the first —
        // we should NOT post a reply (no compose_reply / post_reply / resolve_thread action),
        // we should NOT resolve the thread, and we SHOULD advance to the next comment.
        var state = CreateState();
        var c1 = MakeComment(id: "c1");
        var c2 = MakeComment(id: "c2");
        state.UnresolvedComments.Add(c1);
        state.UnresolvedComments.Add(c2);
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_replied_externally", null);

        // Did NOT post anything — task should not be a reply/resolve action.
        Assert.NotEqual("compose_reply", action.Task);
        Assert.NotEqual("post_reply", action.Task);
        Assert.NotEqual("resolve_thread", action.Task);

        // Did NOT mark the comment as addressed (no thread resolution).
        Assert.False(c1.IsAddressed);
        Assert.False(state.PendingResolveAfterAddress);

        // Advanced to the next comment (index incremented OR transitioned to polling).
        Assert.True(state.CurrentCommentIndex >= 1);
    }

    [Fact]
    public void ProcessEvent_TreatAsRepliedExternally_LastComment_TransitionsToPolling()
    {
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment(id: "only"));
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "treat_as_replied_externally", null);

        // After the last comment, we should drop back to polling.
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
        Assert.NotEqual("compose_reply", action.Task);
        Assert.NotEqual("post_reply", action.Task);
    }

    // ────────────────────────────────────────────────────────────────────
    // Push-detected recovery must not assume comment_addressed for tasks
    // whose completion event is ambiguous (apply_recommendation can push
    // either an implementation OR a proving test → comment_replied).
    // Reviewer comment #5 on PR #51.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecoverFromReadyResolution_PushDetected_AmbiguousTask_AsksUserInsteadOfDispatchingCommentAddressed()
    {
        // apply_recommendation (or any task that didn't set an explicit expected
        // completion event) is ambiguous: the agent might have pushed an implementation
        // (comment_addressed → resolve thread) OR a proving test (comment_replied →
        // keep thread open for reviewer). The recovery must NOT guess; it should
        // surface the ask_user prompt with both choices.
        var state = CreateState();
        var c = MakeComment();
        state.UnresolvedComments.Add(c);
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.EnterExecutingTask();
        state.ExecutingTaskExpectedCompletion = null;  // ambiguous

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(state, headAdvanced: true);

        // Should NOT have routed through ProcessCommentAddressed (which would resolve
        // the thread or emit a compose/resolve action).
        Assert.False(c.IsAddressed);
        Assert.False(state.PendingResolveAfterAddress);
        Assert.NotEqual("compose_reply", action.Task);
        Assert.NotEqual("resolve_thread", action.Task);
        // SHOULD be an ask_user prompt with both completion choices.
        Assert.Equal("ask_user", action.Action);
        Assert.NotNull(action.Choices);
        Assert.Contains("Treat as comment addressed", action.Choices!);
        Assert.Contains("Treat as comment replied", action.Choices!);
    }

    [Fact]
    public void RecoverFromReadyResolution_PushDetected_KnownAddressedTask_DispatchesCommentAddressed()
    {
        // address_comment (set by EmitAddressCommentAction) has only one completion path:
        // comment_addressed. The recovery should auto-dispatch in this unambiguous case
        // (this preserves the original auto-recovery convenience for the common path).
        var state = CreateState();
        var c = MakeComment();
        state.UnresolvedComments.Add(c);
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.EnterExecutingTask();
        state.ExecutingTaskExpectedCompletion = "comment_addressed";

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(state, headAdvanced: true);

        // Should have routed through ProcessCommentAddressed — either a compose_reply
        // request (if no pending reply text) or a resolve_thread action. Either way, it
        // is NOT the recovery ask_user prompt.
        Assert.NotEqual("ask_user", action.Action);
        // The processing should have engaged the comment_addressed pipeline (one of
        // these flags or actions will be set).
        var engagedAddressedPipeline =
            action.Task == "compose_reply" ||
            action.Task == "resolve_thread" ||
            state.PendingResolveAfterAddress ||
            c.IsAddressed;
        Assert.True(engagedAddressedPipeline,
            $"Expected ProcessCommentAddressed to engage; got Action={action.Action}, Task={action.Task}");
    }

    [Fact]
    public void RecoverFromReadyResolution_NoPush_StillBuildsRecoveryPrompt()
    {
        // Regression: the no-push branch must still produce the user-friendly recovery prompt
        // ("without a new commit") regardless of ExecutingTaskExpectedCompletion.
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment());
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.EnterExecutingTask();
        state.ExecutingTaskExpectedCompletion = "comment_addressed";

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(state, headAdvanced: false);

        Assert.Equal("ask_user", action.Action);
        Assert.NotNull(action.Question);
        Assert.Contains("without a new commit", action.Question);
    }

    [Fact]
    public void EmitAddressCommentAction_SetsExpectedCompletionToCommentAddressed()
    {
        // address_comment task has only one documented completion path: event=comment_addressed.
        // The state field is what BuildRecoverFromReadyResolution reads to decide whether the
        // push-detected auto-dispatch is safe.
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment());
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.AwaitingUser;

        // Trigger BeginAddressCurrentComment → EmitAddressCommentAction via the public surface.
        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "address", null);

        Assert.Equal("address_comment", action.Task);
        Assert.Equal("comment_addressed", state.ExecutingTaskExpectedCompletion);
    }

    [Fact]
    public void BeginApplyRecommendation_LeavesExpectedCompletionAmbiguous()
    {
        // apply_recommendation has TWO documented completion paths: comment_addressed (implement)
        // and comment_replied (proving-test pushback). Must NOT pre-commit to one.
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment());
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);

        Assert.Equal("apply_recommendation", action.Task);
        Assert.Null(state.ExecutingTaskExpectedCompletion);
    }

    // ────────────────────────────────────────────────────────────────────
    // Waiting-comment recovery: when an interpret_freeform / execute task
    // runs while ActiveWaitingComment is set (CommentFlow==None and
    // CiFailureFlow==None — the normal post-reply waiting state), the
    // recovery must preserve the waiting context and offer the original
    // Resolve / Go-back choices instead of clearing ActiveWaitingComment
    // and offering only generic Resume/Stop. Reviewer comment #6 on PR #51.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_WaitingComment_PreservesContextAndOffersWaitingChoices()
    {
        var state = CreateState();
        var c = MakeComment();
        state.ActiveWaitingComment = c;
        // CommentFlow == None, CiFailureFlow == None — the normal waiting-for-reply state
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.None,
            priorCiFailureFlow: CiFailureFlowState.None);

        Assert.Equal("ask_user", action.Action);
        Assert.NotNull(action.Choices);
        // Original waiting-comment choices must be offered:
        Assert.Contains("Resolve this thread", action.Choices!);
        Assert.Contains("Go back to monitoring", action.Choices!);
        // ActiveWaitingComment must NOT be wiped (otherwise ProcessWaitingCommentChoice
        // can't dispatch the user's selection).
        Assert.Equal(c, state.ActiveWaitingComment);
    }

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_WaitingComment_ResolveChoice_DispatchesToProcessWaitingCommentChoice()
    {
        // End-to-end: after the recovery prompt offers "Resolve this thread", the user's
        // selection must round-trip through ChoiceValueMap → ProcessUserChoice →
        // ProcessWaitingCommentChoice → BuildResolveThreadAction. If ActiveWaitingComment
        // is wiped or CommentFlow is set, the dispatcher routes wrong.
        var state = CreateState();
        var c = MakeComment();
        state.ActiveWaitingComment = c;
        state.EnterExecutingTask();

        var prompt = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.None,
            priorCiFailureFlow: CiFailureFlowState.None);

        Assert.Equal("ask_user", prompt.Action);
        // The helper only builds the action; the caller (RecoverFromUnexpectedState or
        // BuildRecoverFromReadyResolution) sets AwaitingUser. Simulate that here so the
        // follow-up user_chose dispatches correctly.
        state.CurrentState = MonitorStateId.AwaitingUser;

        // Simulate the user picking "Resolve this thread"
        var choiceValue = MonitorTransitions.ChoiceValueMap["Resolve this thread"];
        var followUp = MonitorTransitions.ProcessEvent(state, "user_chose", choiceValue, null);

        // Should have dispatched to BuildResolveThreadAction (auto_execute resolve_thread).
        Assert.Equal("auto_execute", followUp.Action);
        Assert.Equal("resolve_thread", followUp.Task);
    }

    [Fact]
    public void BuildExecutingTaskRecoveryPrompt_NoFlowAndNoWaitingComment_FallsBackToGenericChoices()
    {
        // Regression: the truly-no-context branch (no flow, no waiting comment) still
        // produces only Resume/Stop and clears ActiveWaitingComment (already null here).
        var state = CreateState();
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildExecutingTaskRecoveryPrompt(
            state,
            reasonText: "Looks like the task finished without a new commit",
            priorCommentFlow: CommentFlowState.None,
            priorCiFailureFlow: CiFailureFlowState.None);

        Assert.NotNull(action.Choices);
        Assert.DoesNotContain("Resolve this thread", action.Choices!);
        Assert.Contains("Resume monitoring", action.Choices!);
    }

    [Fact]
    public void RecoverFromReadyResolution_PushDetected_WaitingComment_PreservesContextAndAsksUser()
    {
        // If HEAD advanced while we were in a waiting-comment state (the agent pushed
        // something while elicit-freeform was running), we shouldn't auto-dispatch any
        // completion — we have no flow, just a waiting thread. Surface the waiting-comment
        // recovery prompt instead, so the user can resolve or go back.
        var state = CreateState();
        var c = MakeComment();
        state.ActiveWaitingComment = c;
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(state, headAdvanced: true);

        Assert.Equal("ask_user", action.Action);
        Assert.NotNull(action.Choices);
        Assert.Contains("Resolve this thread", action.Choices!);
        Assert.Equal(c, state.ActiveWaitingComment);
    }
}
