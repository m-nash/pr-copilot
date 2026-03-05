// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

public class ReviewResult
{
    public List<ReviewInfo> Approvals { get; set; } = [];
    public List<ReviewInfo> StaleApprovals { get; set; } = [];
}
