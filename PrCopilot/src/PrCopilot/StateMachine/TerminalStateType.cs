// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Terminal state types detected by the polling loop.
/// Priority-ordered: earlier values take precedence.
/// </summary>
public enum TerminalStateType
{
    /// <summary>New unresolved review comment (highest priority).</summary>
    NewComment,

    /// <summary>A reviewer replied to a comment we previously replied to.</summary>
    ReviewerReplied,

    /// <summary>Merge conflict detected.</summary>
    MergeConflict,

    /// <summary>CI has failures (checked BEFORE approved — failures can never be masked).</summary>
    CiFailure,

    /// <summary>CI checks were cancelled.</summary>
    CiCancelled,

    /// <summary>PR is approved and CI is green (only if no failures/cancellations).</summary>
    ApprovedCiGreen
}
