// Licensed under the MIT License.

namespace PrCopilot.Tools;

/// <summary>
/// Result from an elicitation — either a standard choice or freeform text.
/// </summary>
internal sealed class ElicitChoiceResult
{
    /// <summary>The choice value (mapped internal value) or freeform text.</summary>
    internal string Value { get; init; } = "handle_myself";

    /// <summary>True if the user typed freeform text instead of picking a choice.</summary>
    internal bool IsFreeform { get; init; }

    /// <summary>The original question shown to the user (for freeform context).</summary>
    internal string? OriginalQuestion { get; set; }

    /// <summary>The original choices shown (for freeform context).</summary>
    internal List<string>? OriginalChoices { get; set; }
}
