// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace PrCopilot.Tests;

/// <summary>
/// FakeMcpServer variant that supports sampling — returns a configurable text response.
/// </summary>
#pragma warning disable MCPEXP002
internal class FakeSamplingMcpServer : FakeMcpServer
#pragma warning restore MCPEXP002
{
    private readonly string[] _responseBlocks;

    public CreateMessageRequestParams? LastRequest { get; private set; }

    public FakeSamplingMcpServer(string responseText = "sampling response")
    {
        _responseBlocks = [responseText];
    }

    /// <summary>
    /// Simulate a client that returns the answer across multiple content blocks
    /// (e.g. a truncated false-start block followed by the complete answer).
    /// </summary>
    public FakeSamplingMcpServer(params string[] responseBlocks)
    {
        _responseBlocks = responseBlocks.Length > 0 ? responseBlocks : ["sampling response"];
    }

    public override ClientCapabilities? ClientCapabilities => new()
    {
        Sampling = new SamplingCapability()
    };

    public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken ct = default)
    {
        // Intercept sampling/createMessage requests
        if (request.Method == "sampling/createMessage")
        {
            // Capture the request params for test assertions
            if (request.Params is JsonNode paramsNode)
            {
                LastRequest = paramsNode.Deserialize<CreateMessageRequestParams>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            var result = new CreateMessageResult
            {
                Model = "test-model",
                Role = Role.Assistant,
                Content = [.. _responseBlocks.Select(b => new TextContentBlock { Text = b })],
                StopReason = "endTurn"
            };

            var resultNode = JsonSerializer.SerializeToNode(result);

            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Result = resultNode
            });
        }

        return base.SendRequestAsync(request, ct);
    }
}
