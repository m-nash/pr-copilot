// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace PrCopilot.Tests;

internal class FakeElicitationMcpServer(params string[] choices) : FakeSamplingMcpServer("{\"mapsToChoice\":null}")
{
    private readonly Queue<string> _choices = new(choices);

    public List<ElicitRequestParams> Elicitations { get; } = [];
    public Func<ElicitRequestParams, CancellationToken, Task>? BeforeResponse { get; set; }

    public override ClientCapabilities? ClientCapabilities => new()
    {
        Sampling = new(),
        Elicitation = new() { Form = new() }
    };

    public override async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken ct = default)
    {
        if (request.Method != "elicitation/create")
            return await base.SendRequestAsync(request, ct);

        var parameters = Assert.IsAssignableFrom<JsonNode>(request.Params).Deserialize<ElicitRequestParams>();
        Assert.NotNull(parameters);
        Elicitations.Add(parameters);
        if (BeforeResponse != null)
            await BeforeResponse(parameters, ct);
        Assert.NotEmpty(_choices);
        var result = JsonSerializer.SerializeToNode(new
        {
            action = "accept",
            content = new { choice = _choices.Dequeue() }
        });
        return new JsonRpcResponse { Id = request.Id, Result = result };
    }
}
