// Licensed under the MIT License.

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PrCopilot.Tests;

/// <summary>
/// Minimal fake McpServer for unit testing heartbeat and notification flows.
/// SendNotificationAsync (inherited from McpSession) calls SendMessageAsync,
/// which this fake overrides to silently succeed.
/// </summary>
#pragma warning disable MCPEXP002 // Experimental MCP API — needed for test fake
internal class FakeMcpServer : McpServer
#pragma warning restore MCPEXP002
{
    private readonly List<JsonRpcMessage> _sentMessages = [];

    /// <summary>All messages sent via SendMessageAsync (notifications, etc.)</summary>
    public IReadOnlyList<JsonRpcMessage> SentMessages => _sentMessages;

    public override string SessionId => "fake-session";
    public override string NegotiatedProtocolVersion => "2024-11-05";
    public override Implementation? ClientInfo => null;
    public override ClientCapabilities? ClientCapabilities => null;
    public override LoggingLevel? LoggingLevel => null;
    public override McpServerOptions ServerOptions => new();
    public override IServiceProvider? Services => null;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken ct = default)
        => Task.FromResult(new JsonRpcResponse { Id = request.Id, Result = default });

    public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
    {
        _sentMessages.Add(message);
        return Task.CompletedTask;
    }

    public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
        => new FakeDisposable();

    private class FakeDisposable : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
