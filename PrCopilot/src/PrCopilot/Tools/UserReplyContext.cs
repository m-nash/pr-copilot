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
    /// How sampling classified the text. "custom_instruction" means sampling ran and decided
    /// the text doesn't map to any of the available choices (so it must be a custom instruction);
    /// "unavailable" means sampling didn't run or failed (e.g., the host didn't grant the sampling
    /// capability, the model returned invalid JSON, or an exception was raised). The agent should
    /// distinguish these — "custom_instruction" is a deliberate decision the agent can trust;
    /// "unavailable" means the agent must do all the work of interpreting the text itself.
    /// </summary>
    [JsonPropertyName("samplingClassification")]
    public string SamplingClassification { get; set; } = "custom_instruction";

    /// <summary>
    /// When sampling ran, the brief reasoning it provided for its classification decision
    /// (e.g., "user wrote a custom test request, doesn't match any of the choices"). Null
    /// when sampling was unavailable. Useful context for the agent — it can echo the
    /// reasoning to the user or use it to make a finer-grained decision.
    /// </summary>
    [JsonPropertyName("samplingReasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SamplingReasoning { get; set; }
}
