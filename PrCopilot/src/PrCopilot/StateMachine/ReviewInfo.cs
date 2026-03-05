// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// A PR review (approval, changes requested, etc.)
/// </summary>
public class ReviewInfo
{
    public string Author { get; set; } = "";
    public string State { get; set; } = ""; // APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED
    public DateTime SubmittedAt { get; set; }
    public bool IsStale { get; set; }
}
