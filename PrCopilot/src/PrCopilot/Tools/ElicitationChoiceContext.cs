// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// A single choice from an elicitation prompt. <see cref="Display"/> is what the
/// user saw; <see cref="Value"/> is the internal mapped value the agent should
/// pass back as <c>choice</c> for a Path A clean choice match.
/// </summary>
internal sealed class ElicitationChoiceContext
{
    /// <summary>Human-readable choice label as shown to the user.</summary>
    [JsonPropertyName("display")]
    public string Display { get; set; } = "";

    /// <summary>Internal mapped value (what would be passed back as <c>choice</c>).</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
