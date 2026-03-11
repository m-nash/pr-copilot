// Licensed under the MIT License.

using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PrCopilot.Services;
using PrCopilot.StateMachine;

namespace PrCopilot.Tools;

/// <summary>
/// Converts MonitorAction ask_user payloads to MCP elicitation requests
/// and processes the results back into state machine choice values.
/// Bypasses the LLM entirely for user interaction — choices go directly
/// from the server to the client.
/// The Copilot CLI natively adds a freeform text input alongside enum choices.
/// </summary>
internal static class ElicitationHelper
{
    /// <summary>
    /// Build an enum-only ElicitRequestParams from a MonitorAction.
    /// The CLI automatically adds a freeform text input alongside the enum.
    /// </summary>
    internal static ElicitRequestParams BuildElicitRequest(MonitorAction action)
    {
        var options = new List<ElicitRequestParams.EnumSchemaOption>();

        foreach (var choice in action.Choices ?? [])
        {
            // Static choices get their mapped value as const; dynamic choices use full text
            var constValue = MonitorTransitions.ChoiceValueMap.TryGetValue(choice, out var mapped)
                ? mapped
                : choice;

            options.Add(new ElicitRequestParams.EnumSchemaOption
            {
                Const = constValue,
                Title = choice
            });
        }

        return new ElicitRequestParams
        {
            Message = action.Question ?? "What would you like to do?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["choice"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Title = "Choose an action",
                        OneOf = options
                    }
                },
                Required = ["choice"]
            }
        };
    }

    /// <summary>
    /// Extract the selected choice from an ElicitResult.
    /// If the value doesn't match any known enum const, it's freeform text
    /// from the CLI's built-in text input.
    /// Returns null if the user declined or cancelled.
    /// </summary>
    internal static ElicitChoiceResult? ExtractElicitResult(ElicitResult result, HashSet<string>? knownConsts = null)
    {
        if (!result.IsAccepted || result.Content is null)
        {
            DebugLogger.Log("Elicitation", $"User {result.Action} the elicitation");
            return null;
        }

        if (result.Content.TryGetValue("choice", out var choiceElement))
        {
            var choice = choiceElement.GetString();
            if (!string.IsNullOrWhiteSpace(choice))
            {
                bool isFreeform = knownConsts != null && !knownConsts.Contains(choice);
                return new ElicitChoiceResult { Value = choice, IsFreeform = isFreeform };
            }
        }

        // Check for freeform response field (CLI may use this for typed text)
        if (result.Content.TryGetValue("response", out var responseElement))
        {
            var response = responseElement.GetString();
            if (!string.IsNullOrWhiteSpace(response))
            {
                return new ElicitChoiceResult { Value = response, IsFreeform = true };
            }
        }

        DebugLogger.Log("Elicitation", "ElicitResult accepted but no choice or response found");
        return null;
    }

    /// <summary>
    /// Elicit a choice from the user via MCP elicitation.
    /// The CLI natively provides a freeform text input alongside enum choices.
    /// Returns a result with Value="handle_myself" if the user declines or cancels.
    /// </summary>
    internal static async Task<ElicitChoiceResult> ElicitChoiceAsync(
        McpServer server,
        MonitorAction action,
        CancellationToken cancellationToken)
    {
        var request = BuildElicitRequest(action);

        // Build the set of known enum const values for freeform detection
        var knownConsts = new HashSet<string>();
        foreach (var choice in action.Choices ?? [])
        {
            knownConsts.Add(MonitorTransitions.ChoiceValueMap.TryGetValue(choice, out var mapped) ? mapped : choice);
        }

        DebugLogger.Log("Elicitation", $"Eliciting: {action.Question} [{string.Join(", ", action.Choices ?? [])}]");

        var result = await server.ElicitAsync(request, cancellationToken);
        var elicitResult = ExtractElicitResult(result, knownConsts);

        if (elicitResult is null)
        {
            DebugLogger.Log("Elicitation", "Falling back to handle_myself (user declined/cancelled)");
            return new ElicitChoiceResult { Value = "handle_myself", IsFreeform = false };
        }

        // Attach original context for freeform interpretation
        elicitResult.OriginalQuestion = action.Question;
        elicitResult.OriginalChoices = action.Choices;

        DebugLogger.Log("Elicitation", $"Result: {(elicitResult.IsFreeform ? "freeform" : "choice")}={elicitResult.Value}");
        return elicitResult;
    }
}
