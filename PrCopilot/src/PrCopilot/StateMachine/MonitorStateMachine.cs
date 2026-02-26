// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Top-level states for the PR monitor state machine.
/// Transitions are deterministic — the C# code decides, not the LLM.
/// </summary>
public enum MonitorStateId
{
    /// <summary>No active monitoring session.</summary>
    Idle,

    /// <summary>Background polling loop is running, checking for terminal states.</summary>
    Polling,

    /// <summary>A terminal state was detected. Preparing the ask_user payload.</summary>
    TerminalDetected,

    /// <summary>Waiting for the user to make a choice (via ask_user).</summary>
    AwaitingUser,

    /// <summary>LLM is executing a task (address comment, apply fix, write reply).</summary>
    ExecutingTask,

    /// <summary>LLM is investigating CI failures (analyzing logs).</summary>
    Investigating,

    /// <summary>Investigation complete, presenting findings to user.</summary>
    InvestigationResults,

    /// <summary>LLM is applying a fix (code change + commit/push).</summary>
    ApplyingFix,

    /// <summary>Monitoring stopped (user chose "handle myself" or explicit stop).</summary>
    Stopped
}

/// <summary>
/// Sub-states for the comment addressing flow.
/// </summary>
public enum CommentFlowState
{
    None,
    SingleCommentPrompt,
    MultiCommentPrompt,
    AddressAllIterating,
    PickComment,
    PickRemaining
}

/// <summary>
/// Sub-states for the CI failure investigation flow.
/// </summary>
public enum CiFailureFlowState
{
    None,
    CiFailurePrompt,
    Investigating,
    InvestigationResults
}

/// <summary>
/// Terminal state types detected by the polling loop.
/// Priority-ordered: earlier values take precedence.
/// </summary>
public enum TerminalStateType
{
    /// <summary>New unresolved review comment (highest priority).</summary>
    NewComment,

    /// <summary>Merge conflict detected.</summary>
    MergeConflict,

    /// <summary>CI has failures (checked BEFORE approved — failures can never be masked).</summary>
    CiFailure,

    /// <summary>CI checks were cancelled.</summary>
    CiCancelled,

    /// <summary>PR is approved and CI is green (only if no failures/cancellations).</summary>
    ApprovedCiGreen,

    /// <summary>CI passed but all comments were previously ignored.</summary>
    CiPassedCommentsIgnored
}
