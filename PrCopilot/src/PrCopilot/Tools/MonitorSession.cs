// Licensed under the MIT License.

using PrCopilot.Services;
using PrCopilot.StateMachine;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PrCopilot.Tools;

/// <summary>
/// Internal session state for a monitored PR.
/// </summary>
internal class MonitorSession : IDisposable
{
    public MonitorState State { get; set; } = new();
    /// <summary>Pending viewer action (e.g., "ACTION|threadId" from trigger file).</summary>
    public string? PendingTriggerContent { get; set; }
    private CancellationTokenSource? _pollCts = new();
    public CancellationToken PollToken => _pollCts?.Token ?? CancellationToken.None;

    // Dedicated trigger file watcher — captures ACTION clicks even when not polling
    private FileSystemWatcher? _triggerWatcher;
    private TaskCompletionSource<string>? _triggerTcs;
    private readonly object _triggerLock = new();

    public void StartTriggerWatcher(string triggerFilePath)
    {
        var dir = Path.GetDirectoryName(triggerFilePath);
        var fileName = Path.GetFileName(triggerFilePath);
        if (dir == null || fileName == null) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        _triggerWatcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _triggerWatcher.Created += OnTriggerFileDetected;
        _triggerWatcher.Changed += OnTriggerFileDetected;

        // Check if trigger file already exists (race condition on startup)
        if (File.Exists(triggerFilePath))
        {
            DebugLogger.Log("Trigger", $"Stale trigger file found on startup: {triggerFilePath}");
            OnTriggerFileDetected(this, new FileSystemEventArgs(WatcherChangeTypes.Created, dir, fileName));
        }
    }

    private void OnTriggerFileDetected(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Small delay to let the writer finish
            Thread.Sleep(50);
            if (!File.Exists(e.FullPath)) return;
            var content = File.ReadAllText(e.FullPath).Trim();
            File.Delete(e.FullPath);
            DebugLogger.Log("Trigger", $"File detected: changeType={e.ChangeType}, content='{content}'");

            if (!string.IsNullOrEmpty(content))
            {
                lock (_triggerLock)
                {
                    PendingTriggerContent = content;
                    _triggerTcs?.TrySetResult(content);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log("Trigger", $"File handler error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns a task that completes when the trigger file is written.
    /// Used by SleepWithInterruptsAsync as an alternative to polling.
    /// </summary>
    public Task<string> WaitForTriggerAsync(CancellationToken ct)
    {
        lock (_triggerLock)
        {
            // If there's already pending content, return immediately
            if (PendingTriggerContent != null)
            {
                DebugLogger.Log("Trigger", $"WaitForTriggerAsync returning immediately: pending='{PendingTriggerContent}'");
                return Task.FromResult(PendingTriggerContent);
            }

            _triggerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcs = _triggerTcs; // capture local ref for the callback
            ct.Register(() => tcs?.TrySetCanceled());
            return _triggerTcs.Task;
        }
    }

    /// <summary>Reset the TCS for the next wait cycle.</summary>
    public void ResetTriggerWait()
    {
        lock (_triggerLock)
        {
            _triggerTcs = null;
        }
    }

    public void CancelPolling()
    {
        DebugLogger.Log("Session", "CancelPolling called");
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
    }

    // --- Session-level heartbeat for MCP keepalive ---
    private readonly HeartbeatManager _heartbeat = new();

    public void StartHeartbeat(McpServer? server, ProgressToken? progressToken = null)
        => _heartbeat.StartForPr(server, State, progressToken);

    public void StopHeartbeat() => _heartbeat.Stop();

    public void Dispose()
    {
        _heartbeat.Dispose();
        _triggerWatcher?.Dispose();
        _pollCts?.Dispose();
    }
}
