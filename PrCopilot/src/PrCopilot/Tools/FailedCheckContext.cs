// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// A single failed CI check entry in <see cref="CiFailureContext.FailedChecks"/>.
/// </summary>
internal sealed class FailedCheckContext
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}
