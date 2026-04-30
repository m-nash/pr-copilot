// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// The elicitation prompt that was shown to the user, captured for the
/// <see cref="FreeformInterpretContext"/> payload so the agent knows what
/// question the user was responding to.
/// </summary>
internal sealed class ElicitationContext
{
    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<ElicitationChoiceContext> Choices { get; set; } = [];
}
