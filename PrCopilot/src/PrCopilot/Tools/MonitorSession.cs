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
    public volatile bool IsStopped;
    private readonly SemaphoreSlim _stepGate = new(1, 1);
    private readonly object _stepLock = new();
    private CancellationTokenSource? _stepCancellation;
    private bool _cancelStepOnStop = true;
    private readonly CancellationTokenSource _stopCancellation = new();
    public CancellationToken StopToken { get; }

    public MonitorSession() => StopToken = _stopCancellation.Token;

    public async Task<string> RunStepAsync(
        Func<CancellationToken, bool, Task<string>> execute, CancellationToken cancellationToken, bool cancelOnStop = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, cancelOnStop ? StopToken : CancellationToken.None);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, StopToken);
        CancellationTokenSource? previous;
        MonitorAction? expectedAction;
        MonitorStateId expectedState;
        bool overlapping;
        lock (_stepLock)
        {
            previous = _stepCancellation;
            overlapping = previous != null || _stepGate.CurrentCount == 0;
            _stepCancellation = cancellation;
            _cancelStepOnStop = cancelOnStop;
            expectedAction = State.PendingAction;
            expectedState = State.CurrentState;
        }
        try
        {
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            // Cancellation cannot undo an in-flight GitHub write; its owner must finish before a replacement proceeds.
            await _stepGate.WaitAsync(waitCancellation.Token);
            try
            {
                waitCancellation.Token.ThrowIfCancellationRequested();
                var reconnect = overlapping || !ReferenceEquals(expectedAction, State.PendingAction) || expectedState != State.CurrentState;
                return await execute(cancellation.Token, reconnect);
            }
            finally
            {
                _stepGate.Release();
            }
        }
        finally
        {
            lock (_stepLock)
            {
                if (ReferenceEquals(_stepCancellation, cancellation))
                    _stepCancellation = null;
            }
        }
    }

    private void CancelStep()
    {
        CancellationTokenSource? cancellation;
        lock (_stepLock)
            cancellation = _cancelStepOnStop ? _stepCancellation : null;
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void CancelStop()
    {
        try { _stopCancellation.Cancel(); } catch (ObjectDisposedException) { }
        lock (_triggerLock)
            _triggerTcs?.TrySetResult("STOP");
    }

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
            if (StopToken.IsCancellationRequested)
                return Task.FromResult("STOP");

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
        if (IsStopped) return;
        DebugLogger.Log("Session", "CancelPolling called");
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _pollCts, newCts);
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }
    }

    /// <summary>
    /// Permanently stop the session. Unlike <see cref="CancelPolling"/>, this does
    /// NOT create a new token — the session is done and cannot be resumed.
    /// Uses Interlocked.Exchange so concurrent calls to StopPermanently/Dispose are safe.
    /// </summary>
    public void StopPermanently()
    {
        DebugLogger.Log("Session", "StopPermanently called");
        IsStopped = true;
        CancelStop();
        CancelStep();
        var cts = Interlocked.Exchange(ref _pollCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    // --- Session-level heartbeat for MCP keepalive ---
    private readonly HeartbeatManager _heartbeat = new();

    public long StartHeartbeat(McpServer? server, ProgressToken? progressToken = null)
        => _heartbeat.StartForPr(server, State, progressToken);

    public void StopHeartbeat() => _heartbeat.Stop();

    /// <summary>
    /// Stop the heartbeat only if the given generation matches the current one.
    /// Prevents a stale tool call's finally block from killing a newer heartbeat.
    /// </summary>
    public void StopHeartbeat(long generation) => _heartbeat.StopGeneration(generation);

    public void Dispose()
    {
        CancelStop();
        CancelStep();
        _stopCancellation.Dispose();
        _heartbeat.Dispose();
        _triggerWatcher?.Dispose();
        var cts = Interlocked.Exchange(ref _pollCts, null);
        cts?.Dispose();
    }
}
