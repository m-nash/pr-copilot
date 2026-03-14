// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

public class ReviewResult
{
    public List<ReviewInfo> Approvals { get; set; } = [];
    public List<ReviewInfo> StaleApprovals { get; set; } = [];

    /// <summary>
    /// All users who have ever submitted a review on this PR (any state).
    /// Used to check whether a reviewer (e.g. copilot) has already been requested.
    /// </summary>
    public HashSet<string> AllReviewAuthors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
