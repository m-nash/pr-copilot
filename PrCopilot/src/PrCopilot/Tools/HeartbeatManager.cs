// Licensed under the MIT License.

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PrCopilot.Services;
using PrCopilot.StateMachine;

namespace PrCopilot.Tools;

/// <summary>
/// Manages heartbeat/progress notifications sent to the MCP client
/// to keep the connection alive during long-running polling operations.
/// </summary>
internal sealed class HeartbeatManager : IDisposable
{
    private static int s_heartbeatCounter;

    internal static readonly string[] SpinnerFrames =
        ["🕐", "🕑", "🕒", "🕓", "🕔", "🕕", "🕖", "🕗", "🕘", "🕙", "🕚", "🕛"];

    /// <summary>Counter exposed for testing only.</summary>
    internal static int HeartbeatCounter { get => s_heartbeatCounter; set => s_heartbeatCounter = value; }

    private CancellationTokenSource? _cts;
    private long _generation;
    private const int IntervalSeconds = 5;

    /// <summary>Current generation, exposed for testing.</summary>
    internal long Generation => _generation;

    /// <summary>Whether the heartbeat is currently running.</summary>
    internal bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    /// <summary>
    /// Start a heartbeat that sends single-PR status messages every 5 seconds.
    /// Automatically sends the first message immediately.
    /// Stops any existing heartbeat first to prevent duplicates.
    /// Returns a generation token for use with <see cref="StopGeneration"/>.
    /// </summary>
    public long StartForPr(McpServer? server, MonitorState state, ProgressToken? progressToken = null)
    {
        Stop();
        var gen = ++_generation;
        if (server == null) { DebugLogger.Log("Heartbeat", "No McpServer — heartbeat disabled"); return gen; }

        DebugLogger.Log("Heartbeat", $"Starting heartbeat for PR #{state.PrNumber} (gen={gen})");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = SendAsync(server, BuildHeartbeatMessage(state), progressToken);
        _ = RunLoopAsync(server, () => BuildHeartbeatMessage(state), progressToken, ct);
        return gen;
    }

    /// <summary>
    /// Start a heartbeat that sends multi-PR status messages every 5 seconds.
    /// Stops any existing heartbeat first to prevent duplicates.
    /// Returns a generation token for use with <see cref="StopGeneration"/>.
    /// </summary>
    public long StartForMultiPr(McpServer? server, Func<int> sessionCountProvider, ProgressToken? progressToken = null)
    {
        Stop();
        var gen = ++_generation;
        if (server == null) { DebugLogger.Log("Heartbeat", "No McpServer — heartbeat disabled"); return gen; }

        DebugLogger.Log("Heartbeat", $"Starting multi-PR heartbeat (gen={gen})");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = SendAsync(server, BuildMultiPrHeartbeatMessage(sessionCountProvider()), progressToken);
        _ = RunLoopAsync(server, () => BuildMultiPrHeartbeatMessage(sessionCountProvider()), progressToken, ct);
        return gen;
    }

    /// <summary>Stop the heartbeat unconditionally.</summary>
    public void Stop()
    {
        if (_cts != null)
            DebugLogger.Log("Heartbeat", "Stopping heartbeat");
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Stop the heartbeat only if the given generation matches the current one.
    /// This prevents a stale caller (e.g., a replaced tool call's finally block)
    /// from killing a heartbeat started by a newer call.
    /// </summary>
    public void StopGeneration(long generation)
    {
        if (_generation == generation)
        {
            Stop();
        }
        else
        {
            DebugLogger.Log("Heartbeat", $"StopGeneration skipped: requested gen={generation}, current gen={_generation}");
        }
    }

    public void Dispose() => Stop();

    // --- Message building (internal for testing) ---

    /// <summary>
    /// Build a concise heartbeat message with CI status and timestamp for a single PR.
    /// </summary>
    internal static string BuildHeartbeatMessage(MonitorState state)
    {
        var spinner = SpinnerFrames[s_heartbeatCounter % SpinnerFrames.Length];
        var c = state.Checks;
        var ciParts = new List<string>();
        if (c.Passed > 0) ciParts.Add($"{c.Passed}✅");
        if (c.Failed > 0) ciParts.Add($"{c.Failed}❌");
        if (c.InProgress > 0) ciParts.Add($"{c.InProgress}⏳");

        var ciSummary = ciParts.Count > 0
            ? $" · CI: {string.Join(" ", ciParts)}"
            : c.Total > 0 ? " · CI: pending" : "";

        var time = DateTime.Now.ToString("h:mm:ss tt");
        return $"{spinner} {time} · Background Monitoring PR #{state.PrNumber}{ciSummary} · Will prompt when your attention is needed";
    }

    /// <summary>
    /// Build a heartbeat message for multi-PR monitoring mode.
    /// </summary>
    internal static string BuildMultiPrHeartbeatMessage(int sessionCount)
    {
        var spinner = SpinnerFrames[s_heartbeatCounter % SpinnerFrames.Length];
        var time = DateTime.Now.ToString("h:mm:ss tt");
        return $"{spinner} {time} · Background Monitoring {sessionCount} PR(s) · Will prompt when your attention is needed";
    }

    // --- Internal plumbing ---

    private static async Task RunLoopAsync(McpServer server, Func<string> messageBuilder, ProgressToken? progressToken, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct);
                await SendAsync(server, messageBuilder(), progressToken);
            }
            catch (OperationCanceledException) { break; }
            catch { /* swallow unexpected errors in heartbeat */ }
        }
    }

    /// <summary>
    /// Send a heartbeat notification via MCP.
    /// Tries notifications/progress and notifications/message for compatibility.
    /// </summary>
    internal static async Task SendAsync(McpServer server, string message, ProgressToken? progressToken = null)
    {
        s_heartbeatCounter++;

        // Approach 1: If client sent a progressToken, use notifications/progress (standard MCP)
        if (progressToken.HasValue)
        {
            try
            {
                await server.SendNotificationAsync(
                    "notifications/progress",
                    new ProgressNotificationParams
                    {
                        ProgressToken = progressToken.Value,
                        Progress = new ProgressNotificationValue
                        {
                            Progress = s_heartbeatCounter,
                            Message = message,
                        },
                    });
                DebugLogger.Log("Heartbeat", $"Sent notifications/progress (token={progressToken.Value}): {message}");
                return;
            }
            catch (Exception ex)
            {
                DebugLogger.Log("Heartbeat", $"notifications/progress failed: {ex.Message}");
            }
        }

        // Approach 2: Try notifications/progress with a self-generated token
        try
        {
            await server.SendNotificationAsync(
                "notifications/progress",
                new ProgressNotificationParams
                {
                    ProgressToken = new ProgressToken("pr-copilot-heartbeat"),
                    Progress = new ProgressNotificationValue
                    {
                        Progress = s_heartbeatCounter,
                        Message = message,
                    },
                });
            DebugLogger.Log("Heartbeat", $"Sent notifications/progress (self-token): {message}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log("Heartbeat", $"notifications/progress (self-token) failed: {ex.Message}");
        }

        // Approach 3: Also send notifications/message (logging)
        try
        {
            await server.SendNotificationAsync(
                "notifications/message",
                new LoggingMessageNotificationParams
                {
                    Level = LoggingLevel.Info,
                    Logger = "pr-copilot",
                    Data = JsonSerializer.SerializeToElement(message)
                });
            DebugLogger.Log("Heartbeat", $"Sent notifications/message: {message}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log("Heartbeat", $"notifications/message failed: {ex.Message}");
        }
    }
}
