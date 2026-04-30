// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// Structured context attached to <c>interpret_freeform</c> execute actions.
/// The agent's chat history has no record of MCP elicitation — it happens out of band.
/// This payload tells the agent everything it needs to act on a freeform reply: what
/// the user was asked, what reply they gave, what comment/CI failure was being
/// discussed, and what server-side analysis or recommendation the user is reacting to.
/// Without this, the agent can mistake a legitimate elicitation reply for "stale state"
/// because the text doesn't match its chat memory of the user's last message.
/// </summary>
internal sealed class FreeformInterpretContext
{
    /// <summary>"comment" | "ci_failure" | "generic"</summary>
    [JsonPropertyName("flowType")]
    public string FlowType { get; set; } = "generic";

    /// <summary>
    /// Why this fallback was chosen. "sampling_classified_as_custom_instruction"
    /// when sampling decided the text was a custom instruction;
    /// "sampling_unavailable" when sampling threw or returned null unexpectedly.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "sampling_classified_as_custom_instruction";

    /// <summary>The elicitation prompt that was shown to the user.</summary>
    [JsonPropertyName("elicitation")]
    public ElicitationContext Elicitation { get; set; } = new();

    /// <summary>The user's authoritative reply (delivered via MCP elicitation, NOT chat).</summary>
    [JsonPropertyName("userReply")]
    public UserReplyContext UserReply { get; set; } = new();

    /// <summary>The comment thread under discussion (when <c>FlowType</c> is "comment").</summary>
    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommentContext? Comment { get; set; }

    /// <summary>Failed CI checks under discussion (when <c>FlowType</c> is "ci_failure").</summary>
    [JsonPropertyName("ciFailure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CiFailureContext? CiFailure { get; set; }

    /// <summary>
    /// Server-side analysis the user is reacting to. The user's reply often references
    /// this implicitly ("write a test that proves this", "fix it that way", "no, do X instead").
    /// Without this, pronouns in the reply are unresolvable.
    /// </summary>
    [JsonPropertyName("serverAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerAnalysisContext? ServerAnalysis { get; set; }
}
