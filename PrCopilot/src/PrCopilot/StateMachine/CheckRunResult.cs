// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

public class CheckRunResult
{
    public CheckRunCounts Counts { get; set; } = new();
    public List<FailedCheckInfo> Failures { get; set; } = [];
}
