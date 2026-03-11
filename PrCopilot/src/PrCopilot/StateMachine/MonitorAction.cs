// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PrCopilot.StateMachine;

/// <summary>
/// Action payload returned by the state machine to the MCP tools.
/// The MCP tool serializes this as the tool response. The agent interprets it.
/// </summary>
public class MonitorAction
{
    /// <summary>
    /// What the agent should do: "ask_user", "execute", "stop"
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>For multi-PR monitoring: the specific monitor ID this action relates to.</summary>
    [JsonPropertyName("monitorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MonitorId { get; set; }

    /// <summary>For ask_user: the question text.</summary>
    [JsonPropertyName("question")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Question { get; set; }

    /// <summary>For ask_user: the choices.</summary>
    [JsonPropertyName("choices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Choices { get; set; }


    /// <summary>For execute: the task type (address_comment, investigate_ci_failure, etc.)</summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Task { get; set; }

    /// <summary>For execute: instructions for the LLM.</summary>
    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    /// <summary>For stop/info: a message.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>Context data (comment details, failure details, etc.)</summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Context { get; set; }
}
