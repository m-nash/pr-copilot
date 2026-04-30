// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// Server-side analysis the user is reacting to in their freeform reply. Pronouns
/// in the reply ("this", "it", "that fix") commonly refer to the recommendation
/// or suggested fix here; without this context the agent cannot resolve them.
/// </summary>
internal sealed class ServerAnalysisContext
{
    /// <summary>Most recent recommendation text (from explain_comment or CI investigation).</summary>
    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recommendation { get; set; }

    /// <summary>Investigation findings (CI failure flow).</summary>
    [JsonPropertyName("investigationFindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvestigationFindings { get; set; }

    /// <summary>Suggested fix (CI failure flow).</summary>
    [JsonPropertyName("suggestedFix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuggestedFix { get; set; }
}
