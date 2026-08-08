// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Tests for the (ApplyingFix, "ready") recovery path. The CI fix flow runs in
/// MonitorStateId.ApplyingFix and instructs the agent to push before reporting
/// push_completed. A post-push hook that fires event=ready instead must be
/// recognized as a successful push (HEAD advanced + CI flow → dispatch
/// push_completed) rather than falling into RecoverFromUnexpectedState which
/// would wipe CiFailureFlow and reset monitoring.
/// See PR #51 reviewer comment on MonitorTransitions.cs:194.
/// </summary>
public class ApplyingFixReadyRecoveryTests
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

    [Fact]
    public void ApplyingFix_Ready_DispatchesRecoverFromReadyAutoExecute()
    {
        // The transition table must route (ApplyingFix, "ready") to the same
        // recover_from_ready auto_execute as (ExecutingTask, "ready"). Without it,
        // the event falls through to RecoverFromUnexpectedState which wipes
        // CiFailureFlow.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ApplyingFix;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;

        var action = MonitorTransitions.ProcessEvent(state, "ready", null, null);

        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("recover_from_ready_in_executing_task", action.Task);
        // Recovery must NOT have wiped CI flow context — the resolution step needs it.
        Assert.Equal(CiFailureFlowState.InvestigationResults, state.CiFailureFlow);
    }

    [Fact]
    public void ApplyingFix_Ready_HeadAdvanced_DispatchesPushCompleted()
    {
        // End-to-end behavior: when ApplyingFix produces a push (HEAD advanced) and
        // then re-enters with event=ready, the resolution step should treat it as a
        // successful push_completed and resume polling — same as if the agent had
        // called event=push_completed directly.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ApplyingFix;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.HeadShaAtTaskStart = "abc123";
        state.HeadSha = "def456"; // simulate HEAD advanced

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(state, headAdvanced: true);

        // push_completed from ApplyingFix transitions to polling.
        Assert.Equal("polling", action.Action);
    }

    [Fact]
    public void BeginApplyFix_CapturesHeadShaSnapshot()
    {
        // The recovery path compares HeadSha against HeadShaAtTaskStart to decide
        // whether a push happened. BeginApplyFix must capture the snapshot just like
        // EnterExecutingTask does — otherwise headAdvanced is always wrong for
        // the apply_fix flow and the recovery makes the wrong decision.
        var state = CreateState();
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.SuggestedFix = "do the thing";
        state.HeadSha = "before-apply";
        state.HeadShaAtTaskStart = null;

        // Trigger BeginApplyFix via the public dispatch.
        state.CurrentState = MonitorStateId.AwaitingUser;
        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);

        Assert.Equal(MonitorStateId.ApplyingFix, state.CurrentState);
        Assert.Equal("before-apply", state.HeadShaAtTaskStart);
    }

    [Fact]
    public void ApplyingFix_Ready_HeadNotAdvanced_OffersFlowAwareRecoveryPrompt()
    {
        // No push detected (HEAD unchanged) — recovery should surface a flow-aware
        // ask_user prompt rather than wiping CI flow context.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ApplyingFix;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.HeadShaAtTaskStart = "abc123";
        state.HeadSha = "abc123";

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(state, headAdvanced: false);

        Assert.Equal("ask_user", action.Action);
        // Must not have wiped CI flow context — the prompt is flow-aware.
        Assert.Equal(CiFailureFlowState.InvestigationResults, state.CiFailureFlow);
    }
}
