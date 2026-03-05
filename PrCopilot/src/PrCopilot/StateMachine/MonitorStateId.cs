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
