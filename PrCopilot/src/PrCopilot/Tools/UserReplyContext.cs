// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// The user's reply to an MCP elicitation prompt. This is authoritative — it is
/// the user's most recent input even though it does not appear in the agent's
/// chat history (elicitation flows separately from chat).
/// </summary>
internal sealed class UserReplyContext
{
    /// <summary>Verbatim text the user typed into the elicitation prompt.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>Always true for interpret_freeform — kept for clarity in the payload.</summary>
    [JsonPropertyName("isFreeform")]
    public bool IsFreeform { get; set; } = true;

    /// <summary>
    /// How sampling classified the text. "custom_instruction" means sampling decided
    /// the text doesn't map to any choice; "unavailable" means sampling didn't run or failed.
    /// </summary>
    [JsonPropertyName("samplingClassification")]
    public string SamplingClassification { get; set; } = "custom_instruction";
}
