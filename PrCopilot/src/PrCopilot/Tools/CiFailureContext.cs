// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// CI failure context, attached to <see cref="FreeformInterpretContext"/> when the
/// user is replying about a failed CI check.
/// </summary>
internal sealed class CiFailureContext
{
    [JsonPropertyName("failedChecks")]
    public List<FailedCheckContext> FailedChecks { get; set; } = [];
}
