// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

public class ReviewResult
{
    public List<ReviewInfo> Approvals { get; set; } = [];
    public List<ReviewInfo> StaleApprovals { get; set; } = [];

    /// <summary>
    /// All users who have ever submitted a review on this PR (any state: approved, commented, etc.).
    /// Used to determine whether copilot has already reviewed, so we don't re-request.
    /// </summary>
    public HashSet<string> AllReviewAuthors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
