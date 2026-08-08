// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.Tools;

/// <summary>
/// The active comment thread context, attached to <see cref="FreeformInterpretContext"/>
/// when the user is replying about a code review comment.
/// </summary>
internal sealed class CommentContext
{
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}
