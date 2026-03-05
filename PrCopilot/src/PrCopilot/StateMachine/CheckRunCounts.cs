// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Aggregated check run counts matching the GitHub PR UI.
/// </summary>
public class CheckRunCounts
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    /// <summary>Check runs with status "in_progress" (actively running CI jobs).</summary>
    public int InProgress { get; set; }
    /// <summary>Legacy commit statuses with state "pending" (e.g. policy enforcement checks).</summary>
    public int Pending { get; set; }
    public int Queued { get; set; }
    public int Cancelled { get; set; }
    public int Total { get; set; }
}
