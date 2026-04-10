// Licensed under the MIT License.

namespace PrCopilot;

internal static class StringExtensions
{
    /// <summary>
    /// Truncates a string to the specified maximum length, appending "..." if truncated.
    /// Returns empty string for null input.
    /// </summary>
    internal static string Truncate(this string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? "";
        return text[..maxLength] + "...";
    }
}
