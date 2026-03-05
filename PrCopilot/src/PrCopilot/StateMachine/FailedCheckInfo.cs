// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// A failed check run with details for investigation.
/// </summary>
public class FailedCheckInfo
{
    public string Name { get; set; } = "";
    public string Conclusion { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Url { get; set; } = "";
    public string? ExternalId { get; set; }
}
