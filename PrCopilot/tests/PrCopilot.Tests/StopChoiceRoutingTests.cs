// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Regression tests for "Stop monitoring" choice routing in the (ExecutingTask, "ready")
/// recovery prompts (PR #51 reviewer comment on MonitorTransitions.cs:420).
///
/// BuildExecutingTaskRecoveryPrompt offers "Stop monitoring" in all three flow-preserving
/// branches (CommentFlow, CiFailureFlow, ActiveWaitingComment). The choice maps to value
/// "stop". But ProcessUserChoice routes to the per-flow handlers FIRST when those flow
/// states are set — and none of ProcessCommentChoice / ProcessCiFailureChoice /
/// ProcessWaitingCommentChoice handle "stop". They all fall through to TransitionToPolling
/// (or ClearWaitingAndResume), so picking "Stop monitoring" silently resumes polling
/// instead of stopping the monitor.
/// </summary>
public class StopChoiceRoutingTests
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

    private static CommentInfo MakeComment() => new()
    {
        Id = "c1",
        Author = "human-reviewer",
        FilePath = "src/File.cs",
        Line = 10,
        Body = "Fix this",
        Url = "https://example/c1"
    };

    [Fact]
    public void StopChoice_FromCommentFlowRecoveryPrompt_StopsMonitor()
    {
        var state = BaseState();
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments.Add(MakeComment());

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "stop", null);

        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
    }

    [Fact]
    public void StopChoice_FromCiFailureRecoveryPrompt_StopsMonitor()
    {
        var state = BaseState();
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "stop", null);

        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
    }

    [Fact]
    public void StopChoice_FromWaitingCommentRecoveryPrompt_StopsMonitor()
    {
        var state = BaseState();
        state.ActiveWaitingComment = MakeComment();

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "stop", null);

        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
    }

    [Fact]
    public void StopChoice_FromGenericRecoveryPrompt_StopsMonitor_Regression()
    {
        // The no-flow case already works (terminal-level switch handles "stop").
        // Ensure the fix doesn't regress this path.
        var state = BaseState();

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "stop", null);

        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
    }
}
