// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PrCopilot.Services;
using PrCopilot.StateMachine;

namespace PrCopilot.Tools;

/// <summary>
/// MCP tools for the PR monitor state machine.
/// 3 tools: pr_monitor_start, pr_monitor_next_step, pr_monitor_stop.
/// The agent calls pr_monitor_next_step in a loop — it blocks during polling
/// and returns instantly during active flows.
/// </summary>
[McpServerToolType]
public class MonitorFlowTools
{
    private static readonly ConcurrentDictionary<string, MonitorSession> _sessions = new();
    private static readonly HashSet<string> _multiMonitorIds = new();
    private static readonly object _multiMonitorLock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serialize a MonitorAction, automatically attaching choice_map for ask_user actions.
    /// Use this instead of calling JsonSerializer.Serialize directly.
    /// </summary>
    private static string SerializeAction(MonitorAction action)
    {
        if (action.Action == "ask_user")
            MonitorTransitions.AttachChoiceMap(action);
        return JsonSerializer.Serialize(action, _jsonOptions);
    }

    /// <summary>
    /// Write STOPPED lines to all active session logs so viewers close gracefully.
    /// Called from IHostApplicationLifetime.ApplicationStopping.
    /// </summary>
    public static void NotifyShutdown()
    {
        foreach (var kvp in _sessions)
        {
            try
            {
                var line = $"STOPPED|{DateTime.Now:hh:mm tt}|Server shutting down";
                File.AppendAllText(kvp.Value.State.LogFile, line + Environment.NewLine);
                kvp.Value.CancelPolling();
                kvp.Value.Dispose();
            }
            catch { }
        }

        _sessions.Clear();
    }

    [McpServerTool(Name = "pr_monitor_start"), Description("Initialize PR monitoring. Fetches PR data, sets up log file, returns monitor_id. Call pr_monitor_next_step next.")]
    public async Task<string> PrMonitorStart(
        [Description("GitHub repository owner")] string owner,
        [Description("GitHub repository name")] string repo,
        [Description("Pull request number")] int prNumber,
        [Description("Session folder path for log/trigger/debug files")] string sessionFolder,
        CancellationToken cancellationToken = default)
    {
        var monitorId = $"pr-{owner}-{repo}-{prNumber}";
        DebugLogger.Log("PrMonitorStart", $"Called: owner={owner}, repo={repo}, pr={prNumber}");

        // If there's already an active session for this PR, reuse it.
        // This handles the Esc → resume case: the old poll loop is still alive,
        // so we just return the existing state without disrupting the viewer or log.
        if (_sessions.TryGetValue(monitorId, out var existing))
        {
            DebugLogger.Log("PrMonitorStart", $"Reusing existing session for {monitorId}");
            var s = existing.State;
            return JsonSerializer.Serialize(new
            {
                monitor_id = monitorId,
                pr_title = s.PrTitle,
                pr_url = s.PrUrl,
                checks = s.Checks,
                approvals = s.Approvals.Select(a => a.Author).ToList(),
                stale_approvals = s.StaleApprovals.Select(a => a.Author).ToList(),
                unresolved_comments = s.UnresolvedComments.Count,
                waiting_for_reply_comments = s.WaitingForReplyComments.Count,
                merge_conflict = s.HasMergeConflict,
                message = $"Resuming existing monitor for PR #{prNumber}. Call pr_monitor_next_step with event='ready' to continue."
            }, _jsonOptions);
        }

        // Fetch all PR data
        var prInfo = await PrStatusFetcher.FetchPrInfoAsync(owner, repo, prNumber);
        var checkResult = await PrStatusFetcher.FetchCheckRunsAsync(owner, repo, prInfo.HeadSha);
        var reviewResult = await PrStatusFetcher.FetchReviewsAsync(owner, repo, prNumber, prInfo.HeadSha);
        var allComments = await PrStatusFetcher.FetchAndCleanUnresolvedCommentsAsync(owner, repo, prNumber, prInfo.Author);

        // Get the current authenticated GitHub user
        var currentUser = "";
        try
        {
            currentUser = await PrStatusFetcher.FetchCurrentUserAsync();
        }
        catch
        {
            currentUser = prInfo.Author; // fallback to PR author if gh fails
        }

        DebugLogger.Log("PrMonitorStart", $"Fetched: {checkResult.Counts.Total} checks ({checkResult.Counts.Failed} failed), {reviewResult.Approvals.Count} approvals, {allComments.Count} comments");

        // Build state
        var state = new MonitorState
        {
            Owner = owner,
            Repo = repo,
            PrNumber = prNumber,
            PrTitle = prInfo.Title,
            PrUrl = prInfo.Url,
            PrAuthor = prInfo.Author,
            CurrentUser = currentUser,
            HeadSha = prInfo.HeadSha,
            HeadBranch = prInfo.HeadBranch,
            SessionFolder = sessionFolder,
            CurrentState = MonitorStateId.Idle,
            Checks = checkResult.Counts,
            FailedChecks = checkResult.Failures,
            Approvals = reviewResult.Approvals,
            StaleApprovals = reviewResult.StaleApprovals,
            HasMergeConflict = !prInfo.Mergeable && prInfo.MergeableState == "dirty",
            UnresolvedComments = allComments.Where(c => !c.IsWaitingForReply).ToList(),
            WaitingForReplyComments = allComments.Where(c => c.IsWaitingForReply).ToList(),
            LastPollTime = DateTime.UtcNow
        };

        // Load ignore file if it exists
        if (File.Exists(state.IgnoreFile))
        {
            state.IgnoredCommentIds = (await File.ReadAllLinesAsync(state.IgnoreFile, cancellationToken))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
        }

        // Initialize log file with enriched first line
        var logDir = Path.GetDirectoryName(state.LogFile);
        if (logDir != null && !Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        // Initialize debug logger with PR-scoped path
        DebugLogger.Init(state.DebugLogFile);

        var headerLine = $"#{prNumber} - {prInfo.Title} | {prInfo.Url}";
        await File.WriteAllTextAsync(state.LogFile, headerLine + Environment.NewLine, cancellationToken);

        // Write initial STATUS line
        var statusLine = BuildStatusLine(state);
        await File.AppendAllTextAsync(state.LogFile, statusLine + Environment.NewLine, cancellationToken);

        // Store session
        var session = new MonitorSession
        {
            State = state
        };
        session.StartTriggerWatcher(state.TriggerFile);
        _sessions[monitorId] = session;

        // Launch viewer if not already running
        LaunchViewerIfNeeded(state);
        DebugLogger.Log("PrMonitorStart", $"Session stored, viewer checked, returning monitor_id={monitorId}");

        return JsonSerializer.Serialize(new
        {
            monitor_id = monitorId,
            pr_title = prInfo.Title,
            pr_url = prInfo.Url,
            checks = checkResult.Counts,
            approvals = reviewResult.Approvals.Select(a => a.Author).ToList(),
            stale_approvals = reviewResult.StaleApprovals.Select(a => a.Author).ToList(),
            unresolved_comments = state.UnresolvedComments.Count,
            waiting_for_reply_comments = state.WaitingForReplyComments.Count,
            merge_conflict = state.HasMergeConflict,
            message = $"Monitoring initialized for PR #{prNumber}. Call pr_monitor_next_step with event='ready' to begin."
        }, _jsonOptions);
    }

    [McpServerTool(Name = "pr_monitor_start_all"), Description("Initialize monitoring for all open PRs authored by or assigned to the current user. Fetches user's PRs, sets up sessions, returns monitor IDs. Call pr_monitor_next_step with monitorId='all' next.")]
    public async Task<string> PrMonitorStartAll(
        [Description("Session folder path for log/trigger/debug files")] string sessionFolder,
        [Description("GitHub username. If not provided, auto-detected from gh CLI auth.")] string? githubUser = null,
        CancellationToken cancellationToken = default)
    {
        DebugLogger.Log("PrMonitorStartAll", $"Called: user={githubUser ?? "(auto-detect)"}, sessionFolder={sessionFolder}");

        // Auto-detect user if not provided
        if (string.IsNullOrEmpty(githubUser))
        {
            try
            {
                githubUser = await PrStatusFetcher.FetchCurrentUserAsync();
                DebugLogger.Log("PrMonitorStartAll", $"Auto-detected user: {githubUser}");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("PrMonitorStartAll", ex);
                return JsonSerializer.Serialize(new
                {
                    action = "error",
                    message = "Could not detect your GitHub username. Please ask the user for their GitHub username and pass it as the githubUser parameter."
                }, _jsonOptions);
            }
        }

        // Fetch all open PRs
        var prs = await PrStatusFetcher.FetchUserPrsAsync(githubUser);
        if (prs.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                action = "stop",
                message = $"No open PRs found for user '{githubUser}'."
            }, _jsonOptions);
        }

        DebugLogger.Log("PrMonitorStartAll", $"Found {prs.Count} open PRs for {githubUser}");

        // Initialize each PR by calling PrMonitorStart
        var results = new List<object>();
        var monitorIds = new List<string>();
        var failures = new List<object>();
        foreach (var pr in prs)
        {
            try
            {
                await PrMonitorStart(pr.Owner, pr.Repo, pr.Number, sessionFolder, cancellationToken);
                var monitorId = $"pr-{pr.Owner}-{pr.Repo}-{pr.Number}";
                monitorIds.Add(monitorId);
                results.Add(new
                {
                    monitor_id = monitorId,
                    repo = $"{pr.Owner}/{pr.Repo}",
                    pr_number = pr.Number,
                    title = pr.Title,
                    url = pr.Url
                });
                DebugLogger.Log("PrMonitorStartAll", $"Initialized {monitorId}: {pr.Title}");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("PrMonitorStartAll", ex);
                failures.Add(new { repo = $"{pr.Owner}/{pr.Repo}", pr_number = pr.Number, error = ex.Message });
            }
        }

        if (results.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                action = "stop",
                message = "Failed to initialize monitoring for any PRs.",
                failures
            }, _jsonOptions);
        }

        lock (_multiMonitorLock)
        {
            foreach (var id in monitorIds)
                _multiMonitorIds.Add(id);
        }

        return JsonSerializer.Serialize(new
        {
            monitor_ids = monitorIds,
            prs = results,
            failures = failures.Count > 0 ? failures : null,
            message = $"Monitoring initialized for {results.Count} PR(s). Call pr_monitor_next_step with monitorId='all' and event='ready' to begin."
        }, _jsonOptions);
    }

    [McpServerTool(Name = "pr_monitor_next_step"), Description("The one tool the agent calls in a loop. Reports the result of the last action and gets the next instruction. May block for minutes/hours when in polling mode (zero tokens burned), or return instantly during active flows.")]
    public async Task<string> PrMonitorNextStep(
        [Description("Monitor ID from pr_monitor_start")] string monitorId,
        [Description("Event: ready, user_chose, comment_addressed, fix_applied, investigation_complete, push_completed, task_complete")] string @event,
        [Description("User's choice (for user_chose events)")] string? choice = null,
        [Description("Event data as JSON string (findings, suggested_fix, etc.)")] string? data = null,
        McpServer server = null!,
        RequestContext<CallToolRequestParams> context = null!,
        CancellationToken cancellationToken = default)
    {
        // Debug: log what the client actually sent us
        var progressToken = context?.Params?.ProgressToken;
        DebugLogger.Log("NextStep", $"ProgressToken from client: {(progressToken.HasValue ? progressToken.Value.ToString() : "NONE")}");
        if (context?.Params?.Meta != null)
        {
            DebugLogger.Log("NextStep", $"Meta keys: {string.Join(", ", context.Params.Meta.Select(kv => kv.Key))}");
        }
        else
        {
            DebugLogger.Log("NextStep", "Meta: null (client sent no _meta)");
        }

        // Handle multi-PR monitoring: monitorId="all" enters combined poll loop
        if (monitorId == "all")
        {
            if (@event != "ready")
            {
                return SerializeAction(new MonitorAction
                {
                    Action = "stop",
                    Message = "When using monitorId='all', only event='ready' is supported. Use the specific PR monitorId for other events."
                });
            }

            if (_sessions.IsEmpty)
            {
                lock (_multiMonitorLock) { _multiMonitorIds.Clear(); }
                return SerializeAction(new MonitorAction
                {
                    Action = "stop",
                    Message = "No active sessions to monitor. Call pr_monitor_start_all first."
                });
            }

            DebugLogger.Log("NextStep", $"Entering multi-PR poll loop ({_sessions.Count} sessions)");

            // Start heartbeat for multi-PR mode
            var heartbeatSession = _sessions.Values.FirstOrDefault();
            if (heartbeatSession == null)
            {
                lock (_multiMonitorLock) { _multiMonitorIds.Clear(); }
                return SerializeAction(new MonitorAction
                {
                    Action = "stop",
                    Message = "No active sessions to monitor."
                });
            }
            // Start a standalone heartbeat for multi-PR mode that doesn't depend on any particular session.
            using var multiPrHeartbeat = new HeartbeatManager();
            if (server != null)
                multiPrHeartbeat.StartForMultiPr(server, () => _sessions.Count, progressToken);

            try
            {
                // Write RESUMING line to all session logs
                foreach (var (_, s) in _sessions)
                {
                    var resumingAction = new MonitorAction { Action = "polling", Message = "Resuming monitoring..." };
                    await WriteLogEntryAsync(s.State, resumingAction);
                }

                var action = await PollAllUntilTerminalStateAsync(cancellationToken);

                if (action.Action == "ask_user")
                {
                    action.Instructions = "MANDATORY: Call the ask_user tool with the EXACT question and choices above. Do NOT rewrite, rephrase, or add your own choices. Do NOT act on behalf of the user. Wait for the user's selection, then call pr_monitor_next_step with the monitorId from this response, event='user_chose' and choice set to the EXACT value from the choice_map for the selected option. After handling, call pr_monitor_next_step with monitorId='all' and event='ready' to resume monitoring all PRs.";
                }

                await WriteLogEntryAsync(
                    action.MonitorId != null && _sessions.TryGetValue(action.MonitorId, out var actionSession) ? actionSession.State : heartbeatSession.State,
                    action);

                return SerializeAction(action);
            }
            catch (Exception ex)
            {
                DebugLogger.Error("NextStep", ex);
                return SerializeAction(new MonitorAction
                {
                    Action = "ask_user",
                    Question = $"Internal error during multi-PR monitoring: {ex.Message}. What would you like to do?",
                    Choices = ["Resume monitoring", "Stop monitoring"]
                });
            }
            finally
            {
                multiPrHeartbeat.Stop();
            }
        }

        if (!_sessions.TryGetValue(monitorId, out var session))
        {
            DebugLogger.Log("NextStep", $"No session for {monitorId}");
            return SerializeAction(new MonitorAction
            {
                Action = "stop",
                Message = $"No active session for {monitorId}. Call pr_monitor_start first."
            });
        }

        try
        {
            var state = session.State;
            DebugLogger.Log("NextStep", $"Called: event={@event}, choice={choice ?? "null"}, state={state.CurrentState}");

            // Start session-level heartbeat to keep MCP client alive
            session.StartHeartbeat(server, progressToken);
            // Check for pending viewer action (captured by FileSystemWatcher even when not polling)
            if (session.PendingTriggerContent != null && session.PendingTriggerContent.StartsWith("ACTION|") &&
                state.ActiveWaitingComment == null && @event != "user_chose")
            {
                var threadId = session.PendingTriggerContent["ACTION|".Length..];
                session.PendingTriggerContent = null;
                DebugLogger.Log("NextStep", $"Trigger intercept: ACTION|{threadId}");
                var comment = state.WaitingForReplyComments.FirstOrDefault(c => c.Id == threadId);
                if (comment != null)
                {
                    var triggerAction = MonitorTransitions.BuildWaitingCommentAction(state, comment);
                    if (triggerAction.Action == "ask_user")
                    {
                        triggerAction.Instructions = "MANDATORY: Call the ask_user tool with the EXACT question and choices above. Do NOT rewrite, rephrase, or add your own choices. Do NOT act on behalf of the user. Wait for the user's selection, then call pr_monitor_next_step with event='user_chose' and choice set to the EXACT value from the choice_map for the selected option.";
                    }
                    await WriteLogEntryAsync(state, triggerAction);
                    DebugLogger.Log("NextStep", $"Returning trigger action: {triggerAction.Action}");
                    return SerializeAction(triggerAction);
                }
                session.PendingTriggerContent = null;
            }

            // Parse data if provided
            if (!string.IsNullOrEmpty(data))
            {
                try
                {
                    using var dataDoc = JsonDocument.Parse(data);
                    var root = dataDoc.RootElement;
                    if (root.TryGetProperty("findings", out var findings))
                        state.InvestigationFindings = findings.GetString();
                    if (root.TryGetProperty("suggested_fix", out var fix))
                        state.SuggestedFix = fix.GetString();
                    if (root.TryGetProperty("issue_type", out var issueType))
                        state.IssueType = issueType.GetString();
                }
                catch { /* ignore parse errors in data */ }
            }

            // Feed event to state machine
            var action = MonitorTransitions.ProcessEvent(state, @event, choice, data);
            DebugLogger.Log("NextStep", $"State machine returned: action={action.Action}, task={action.Task ?? "null"}");

            // Handle auto_execute: run the command in C# and loop back
            while (action.Action == "auto_execute")
            {
                action = await ExecuteAutoAction(state, action);
                DebugLogger.Log("NextStep", $"Auto-execute result: action={action.Action}, task={action.Task ?? "null"}");
            }

            // If the state machine says "polling", we block here until a terminal state
            if (action.Action == "polling")
            {
                // In multi-PR mode, don't block on individual session — redirect to "all"
                bool isMultiMonitored;
                lock (_multiMonitorLock) { isMultiMonitored = _multiMonitorIds.Contains(monitorId); }
                if (isMultiMonitored && monitorId != "all")
                {
                    await WriteLogEntryAsync(state, action);
                    await PersistIgnoreFileAsync(state);
                    DebugLogger.Log("NextStep", "Multi-monitor active — redirecting to 'all' polling");
                    return SerializeAction(new MonitorAction
                    {
                        Action = "polling",
                        MonitorId = "all",
                        Message = "PR handled. Call pr_monitor_next_step with monitorId='all' and event='ready' to resume monitoring all PRs."
                    });
                }

                // Cancel any existing poll loop from a previous tool call (e.g., user hit Esc then resumed)
                session.CancelPolling();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.PollToken);
                // Write RESUMING line so the viewer clears any terminal state and shows polling UI
                await WriteLogEntryAsync(state, action);
                // Persist ignore file before blocking (may have changed during comment flow)
                await PersistIgnoreFileAsync(state);
                DebugLogger.Log("NextStep", "Entering poll loop...");
                action = await PollUntilTerminalStateAsync(session, linkedCts.Token);
                DebugLogger.Log("NextStep", $"Poll loop exited: action={action.Action}");

                // If we were cancelled because a new next_step call replaced our poll loop,
                // exit silently — the new call has taken over this session's log file.
                if (action.Action == "stop" && !cancellationToken.IsCancellationRequested)
                {
                    DebugLogger.Log("NextStep", "Poll replaced by new call — exiting silently");
                    return SerializeAction(action);
                }
            }

            // Persist ignore file after any state change
            await PersistIgnoreFileAsync(state);

            // Write to log if transitioning
            await WriteLogEntryAsync(state, action);

            // For ask_user actions, inject a mandatory instruction into the response
            // so the LLM cannot skip the ask_user step or rewrite the choices
            if (action.Action == "ask_user")
            {
                action.Instructions = "MANDATORY: Call the ask_user tool with the EXACT question and choices above. Do NOT rewrite, rephrase, or add your own choices. Do NOT act on behalf of the user. Wait for the user's selection, then call pr_monitor_next_step with event='user_chose' and choice set to the EXACT value from the choice_map for the selected option.";
            }

            return SerializeAction(action);
        }
        catch (Exception ex)
        {
            DebugLogger.Error("NextStep", ex);
            return SerializeAction(new MonitorAction
            {
                Action = "ask_user",
                Question = $"Internal error: {ex.Message}. What would you like to do?",
                Choices = ["Resume monitoring", "Stop monitoring"]
            });
        }
        finally
        {
            // Stop heartbeat when tool call returns (agent will re-invoke and restart it)
            session.StopHeartbeat();
        }
    }

    [McpServerTool(Name = "pr_monitor_stop"), Description("Stop monitoring a PR. Cleans up the polling loop.")]
    public string PrMonitorStop(
        [Description("Monitor ID from pr_monitor_start")] string monitorId)
    {
        // Stop all sessions when monitorId="all"
        if (monitorId == "all")
        {
            var count = _sessions.Count;
            foreach (var kvp in _sessions)
            {
                try
                {
                    var line = $"STOPPED|{DateTime.Now:hh:mm tt}|Monitoring stopped by user";
                    File.AppendAllText(kvp.Value.State.LogFile, line + Environment.NewLine);
                    kvp.Value.CancelPolling();
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _sessions.Clear();
            lock (_multiMonitorLock) { _multiMonitorIds.Clear(); }
            return SerializeAction(new MonitorAction
            {
                Action = "stop",
                Message = $"Monitoring stopped for all {count} PR(s)."
            });
        }

        if (_sessions.TryRemove(monitorId, out var session))
        {
            lock (_multiMonitorLock) { _multiMonitorIds.Remove(monitorId); }
            session.CancelPolling();
            session.Dispose();
            return SerializeAction(new MonitorAction
            {
                Action = "stop",
                Message = $"Monitoring stopped for {monitorId}."
            });
        }

        return SerializeAction(new MonitorAction
        {
            Action = "stop",
            Message = $"No active session for {monitorId}."
        });
    }

    /// <summary>
    /// Blocks until the polling loop detects a terminal state.
    /// This is the zero-token-burn blocking mechanism.
    /// </summary>
    private async Task<MonitorAction> PollUntilTerminalStateAsync(MonitorSession session, CancellationToken cancellationToken)
    {
        var state = session.State;

        while (!cancellationToken.IsCancellationRequested)
        {
            state.PollCount++;
            state.LastPollTime = DateTime.UtcNow;
            DebugLogger.Log("PollLoop", $"Poll #{state.PollCount} starting");

            // Re-fetch all PR data
            try
            {
                var previousHeadSha = state.HeadSha;
                var prInfo = await PrStatusFetcher.FetchPrInfoAsync(state.Owner, state.Repo, state.PrNumber);

                // Check if PR has been merged (by us or someone else)
                if (prInfo.IsMerged)
                {
                    DebugLogger.Log("PollLoop", $"PR #{state.PrNumber} is merged — stopping");
                    var mergedLine = $"STOPPED|{DateTime.Now:hh:mm tt}|🟣 PR #{state.PrNumber} merged successfully";
                    try { await File.AppendAllTextAsync(state.LogFile, mergedLine + Environment.NewLine, cancellationToken); }
                    catch { }
                    state.CurrentState = MonitorStateId.Stopped;
                    return new MonitorAction
                    {
                        Action = "merged",
                        Message = $"🟣 PR #{state.PrNumber} has been merged."
                    };
                }

                state.HeadSha = prInfo.HeadSha;
                state.HasMergeConflict = !prInfo.Mergeable && prInfo.MergeableState == "dirty";

                // New commit pushed — cancel any deferred rerun (CI restarts fresh)
                if (state.PendingRerunWhenChecksComplete && !string.Equals(previousHeadSha, state.HeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLogger.Log("PollLoop", $"New commit detected ({previousHeadSha[..7]} → {state.HeadSha[..7]}) — cancelling deferred rerun");
                    state.PendingRerunWhenChecksComplete = false;
                }

                var checkResult = await PrStatusFetcher.FetchCheckRunsAsync(state.Owner, state.Repo, state.HeadSha);
                state.Checks = checkResult.Counts;
                state.FailedChecks = checkResult.Failures;

                var reviewResult = await PrStatusFetcher.FetchReviewsAsync(state.Owner, state.Repo, state.PrNumber, state.HeadSha);
                state.Approvals = reviewResult.Approvals;
                state.StaleApprovals = reviewResult.StaleApprovals;

                var allComments = await PrStatusFetcher.FetchAndCleanUnresolvedCommentsAsync(state.Owner, state.Repo, state.PrNumber, state.PrAuthor);

                PruneResolvedIgnoredComments(state, allComments, "PollLoop");

                // Filter out ignored comments, then split by waiting-for-reply status
                var nonIgnored = allComments
                    .Where(c => !state.IgnoredCommentIds.Contains(c.Id))
                    .ToList();
                var needsAction = nonIgnored.Where(c => !c.IsWaitingForReply).ToList();
                var waitingForReply = nonIgnored.Where(c => c.IsWaitingForReply).ToList();
                state.UnresolvedComments = needsAction;
                state.WaitingForReplyComments = waitingForReply;

                DebugLogger.Log("PollLoop", $"Poll #{state.PollCount}: {state.Checks.Total} checks ({state.Checks.Failed} failed), {state.Approvals.Count} approvals, {needsAction.Count} needs-action, {waitingForReply.Count} waiting");

                // Write STATUS line
                var statusLine = BuildStatusLine(state);
                await File.AppendAllTextAsync(state.LogFile, statusLine + Environment.NewLine, cancellationToken);

                // Check for deferred rerun: user chose rerun while checks were still in progress
                // (Ignore legacy pending statuses — only actual CI jobs matter)
                if (state.PendingRerunWhenChecksComplete && state.Checks.InProgress == 0 && state.Checks.Queued == 0)
                {
                    // Safeguard: if failures resolved themselves (e.g., flaky test passed on re-poll), resume normal monitoring
                    if (state.Checks.Failed == 0)
                    {
                        DebugLogger.Log("PollLoop", "Deferred rerun cancelled — no failures remain");
                        state.PendingRerunWhenChecksComplete = false;
                    }
                    else
                    {
                        DebugLogger.Log("PollLoop", "All checks complete — triggering deferred rerun");
                        return MonitorTransitions.CompletePendingRerun(state);
                    }
                }

                // Check for terminal state — only needs-action comments trigger it
                var terminal = MonitorTransitions.DetectTerminalState(state, needsAction, state.HasMergeConflict);
                if (terminal.HasValue)
                {
                    DebugLogger.Log("PollLoop", $"Terminal state detected: {terminal.Value} (checks: {state.Checks.Passed}p/{state.Checks.Failed}f/{state.Checks.InProgress}ip/{state.Checks.Pending}pnd/{state.Checks.Queued}q, approvals: {state.Approvals.Count}, comments: {needsAction.Count}, conflict: {state.HasMergeConflict})");
                    return MonitorTransitions.BuildTerminalAction(state, terminal.Value);
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash — retry on next poll
                DebugLogger.Error("PollLoop", ex);
                var errorLine = $"ERROR|{DateTime.Now:hh:mm tt}|{ex.Message}";
                try { await File.AppendAllTextAsync(state.LogFile, errorLine + Environment.NewLine, cancellationToken); }
                catch { /* ignore log write failures */ }
            }

            // Adaptive sleep — use SessionEventService for OS events + trigger file + timeout
            var sleepSeconds = CalculateSleepSeconds(state);

            // Write after-hours pause to log if sleeping until morning
            if (IsAfterHours() && !(state.AfterHoursExtendedUntil.HasValue && state.AfterHoursExtendedUntil.Value > DateTime.Now))
            {
                var next9am = DateTime.Now.AddSeconds(sleepSeconds);
                var pauseLine = $"PAUSED|{DateTime.Now:hh:mm tt}|After hours — sleeping until {next9am:ddd MMM d, h:mm tt}";
                try { await File.AppendAllTextAsync(state.LogFile, pauseLine + Environment.NewLine, cancellationToken); }
                catch { }
            }

            // Clear any non-actionable trigger content before sleeping.
            // The viewer writes bare timestamps on countdown expiry ("Check Now" / auto-poll sync).
            // These are valid wake-up signals but must not persist into the next sleep cycle.
            if (session.PendingTriggerContent != null &&
                !session.PendingTriggerContent.StartsWith("ACTION|") &&
                !session.PendingTriggerContent.StartsWith("EXTEND|"))
            {
                DebugLogger.Log("PollLoop", $"Clearing non-actionable trigger: '{session.PendingTriggerContent}'");
                session.PendingTriggerContent = null;
            }

            DebugLogger.Log("PollLoop", $"Sleeping {sleepSeconds}s...");
            await SleepWithInterruptsAsync(session, sleepSeconds, cancellationToken);

            // Check for EXTEND trigger (after-hours override) — adds 2h from current extension or now
            if (session.PendingTriggerContent != null && session.PendingTriggerContent.StartsWith("EXTEND|"))
            {
                var baseTime = (state.AfterHoursExtendedUntil.HasValue && state.AfterHoursExtendedUntil.Value > DateTime.Now)
                    ? state.AfterHoursExtendedUntil.Value
                    : DateTime.Now;
                state.AfterHoursExtendedUntil = baseTime.AddHours(2);
                DebugLogger.Log("PollLoop", $"After-hours extended until {state.AfterHoursExtendedUntil.Value:hh:mm tt}");
                session.PendingTriggerContent = null;
                var extendLine = $"RESUMING|{DateTime.Now:hh:mm tt}|Monitoring until {state.AfterHoursExtendedUntil.Value:hh:mm tt}";
                try { await File.AppendAllTextAsync(state.LogFile, extendLine + Environment.NewLine, cancellationToken); }
                catch { }
                continue; // Immediately poll again
            }

            // Check if trigger file contained a viewer action request
            if (session.PendingTriggerContent != null && session.PendingTriggerContent.StartsWith("ACTION|"))
            {
                var threadId = session.PendingTriggerContent["ACTION|".Length..];
                session.PendingTriggerContent = null;
                DebugLogger.Log("PollLoop", $"Trigger detected: ACTION|{threadId}");

                // Find the comment in the waiting-for-reply list
                var comment = state.WaitingForReplyComments.FirstOrDefault(c => c.Id == threadId);
                if (comment != null)
                {
                    state.ActiveWaitingComment = comment;
                    return MonitorTransitions.BuildWaitingCommentAction(state, comment);
                }
                DebugLogger.Log("PollLoop", $"Comment {threadId} not found in waiting list");
            }
            session.PendingTriggerContent = null;
        }

        // Cancelled
        return new MonitorAction { Action = "stop", Message = "Monitoring cancelled." };
    }

    /// <summary>
    /// Fetches fresh PR data, updates session state, and checks for terminal states.
    /// Returns a MonitorAction if a terminal state (including merged) is detected, null otherwise.
    /// </summary>
    private static async Task<MonitorAction?> RefreshAndCheckTerminalAsync(MonitorState state, CancellationToken cancellationToken)
    {
        var prInfo = await PrStatusFetcher.FetchPrInfoAsync(state.Owner, state.Repo, state.PrNumber);

        if (prInfo.IsMerged)
        {
            DebugLogger.Log("PollRefresh", $"PR #{state.PrNumber} is merged");
            var mergedLine = $"STOPPED|{DateTime.Now:hh:mm tt}|🟣 PR #{state.PrNumber} merged successfully";
            try { await File.AppendAllTextAsync(state.LogFile, mergedLine + Environment.NewLine, cancellationToken); }
            catch { }
            state.CurrentState = MonitorStateId.Stopped;
            return new MonitorAction
            {
                Action = "merged",
                Message = $"🟣 PR #{state.PrNumber} has been merged."
            };
        }

        state.HeadSha = prInfo.HeadSha;
        state.HasMergeConflict = !prInfo.Mergeable && prInfo.MergeableState == "dirty";

        var checkResult = await PrStatusFetcher.FetchCheckRunsAsync(state.Owner, state.Repo, state.HeadSha);
        state.Checks = checkResult.Counts;
        state.FailedChecks = checkResult.Failures;

        var reviewResult = await PrStatusFetcher.FetchReviewsAsync(state.Owner, state.Repo, state.PrNumber, state.HeadSha);
        state.Approvals = reviewResult.Approvals;
        state.StaleApprovals = reviewResult.StaleApprovals;

        var allComments = await PrStatusFetcher.FetchAndCleanUnresolvedCommentsAsync(state.Owner, state.Repo, state.PrNumber, state.PrAuthor);

        PruneResolvedIgnoredComments(state, allComments, "PollRefresh");

        var nonIgnored = allComments
            .Where(c => !state.IgnoredCommentIds.Contains(c.Id))
            .ToList();
        var needsAction = nonIgnored.Where(c => !c.IsWaitingForReply).ToList();
        var waitingForReply = nonIgnored.Where(c => c.IsWaitingForReply).ToList();
        state.UnresolvedComments = needsAction;
        state.WaitingForReplyComments = waitingForReply;

        DebugLogger.Log("PollRefresh", $"PR #{state.PrNumber}: {state.Checks.Total} checks ({state.Checks.Failed} failed), {state.Approvals.Count} approvals, {needsAction.Count} needs-action, {waitingForReply.Count} waiting");

        var statusLine = BuildStatusLine(state);
        await File.AppendAllTextAsync(state.LogFile, statusLine + Environment.NewLine, cancellationToken);

        var terminal = MonitorTransitions.DetectTerminalState(state, needsAction, state.HasMergeConflict);
        if (terminal.HasValue)
        {
            DebugLogger.Log("PollRefresh", $"Terminal: {terminal.Value}");
            return MonitorTransitions.BuildTerminalAction(state, terminal.Value);
        }

        return null;
    }

    /// <summary>
    /// Combined poll loop for multi-PR monitoring. Iterates over all active sessions,
    /// checks each for terminal states, and returns the first one found.
    /// </summary>
    private async Task<MonitorAction> PollAllUntilTerminalStateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var activeSessions = _sessions
                .Where(kvp => kvp.Value.State.CurrentState != MonitorStateId.Stopped)
                .ToList();

            if (activeSessions.Count == 0)
            {
                lock (_multiMonitorLock) { _multiMonitorIds.Clear(); }
                return new MonitorAction { Action = "stop", Message = "All PRs have been merged or closed. Monitoring complete." };
            }

            var minSleepSeconds = int.MaxValue;

            foreach (var (mid, session) in activeSessions)
            {
                var st = session.State;
                st.PollCount++;
                st.LastPollTime = DateTime.UtcNow;
                DebugLogger.Log("PollAll", $"Polling {mid} (#{st.PollCount})");

                try
                {
                    var action = await RefreshAndCheckTerminalAsync(st, cancellationToken);
                    if (action != null)
                    {
                        action.MonitorId = mid;
                        // Remove merged/stopped sessions from the dictionary
                        if (action.Action == "merged" && _sessions.TryRemove(mid, out var removedSession))
                        {
                            removedSession.Dispose();
                            lock (_multiMonitorLock) { _multiMonitorIds.Remove(mid); }
                        }
                        return action;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("PollAll", ex);
                    var errorLine = $"ERROR|{DateTime.Now:hh:mm tt}|{ex.Message}";
                    try { await File.AppendAllTextAsync(st.LogFile, errorLine + Environment.NewLine, cancellationToken); }
                    catch { }
                }

                // Check for trigger actions from viewer
                if (session.PendingTriggerContent != null && session.PendingTriggerContent.StartsWith("ACTION|"))
                {
                    var threadId = session.PendingTriggerContent["ACTION|".Length..];
                    session.PendingTriggerContent = null;
                    var comment = st.WaitingForReplyComments.FirstOrDefault(c => c.Id == threadId);
                    if (comment != null)
                    {
                        st.ActiveWaitingComment = comment;
                        var triggerAction = MonitorTransitions.BuildWaitingCommentAction(st, comment);
                        triggerAction.MonitorId = mid;
                        return triggerAction;
                    }
                }

                var sleepSeconds = CalculateSleepSeconds(st);
                if (sleepSeconds < minSleepSeconds)
                    minSleepSeconds = sleepSeconds;
            }

            if (minSleepSeconds == int.MaxValue)
                minSleepSeconds = 60;

            // Write after-hours pause for all sessions if applicable
            if (IsAfterHours())
            {
                foreach (var (_, session) in activeSessions)
                {
                    var st = session.State;
                    if (!(st.AfterHoursExtendedUntil.HasValue && st.AfterHoursExtendedUntil.Value > DateTime.Now))
                    {
                        var next9am = DateTime.Now.AddSeconds(minSleepSeconds);
                        var pauseLine = $"PAUSED|{DateTime.Now:hh:mm tt}|After hours — sleeping until {next9am:ddd MMM d, h:mm tt}";
                        try { await File.AppendAllTextAsync(st.LogFile, pauseLine + Environment.NewLine, cancellationToken); }
                        catch { }
                    }
                }
            }

            // Clear non-actionable triggers before sleeping
            foreach (var (_, session) in activeSessions)
            {
                if (session.PendingTriggerContent != null &&
                    !session.PendingTriggerContent.StartsWith("ACTION|") &&
                    !session.PendingTriggerContent.StartsWith("EXTEND|"))
                {
                    session.PendingTriggerContent = null;
                }
            }

            DebugLogger.Log("PollAll", $"Sleeping {minSleepSeconds}s ({activeSessions.Count} sessions)...");

            // Sleep with interrupts from any session's trigger
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(minSleepSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var tasks = new List<Task> { Task.Delay(TimeSpan.FromSeconds(minSleepSeconds), linkedCts.Token) };
                tasks.AddRange(activeSessions.Select(kvp => kvp.Value.WaitForTriggerAsync(linkedCts.Token)));

                await Task.WhenAny(tasks);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Normal timeout
            }
            finally
            {
                foreach (var (_, session) in activeSessions)
                    session.ResetTriggerWait();
            }

            // Handle EXTEND triggers for any session
            foreach (var (_, session) in activeSessions)
            {
                var st = session.State;
                if (session.PendingTriggerContent != null && session.PendingTriggerContent.StartsWith("EXTEND|"))
                {
                    var baseTime = (st.AfterHoursExtendedUntil.HasValue && st.AfterHoursExtendedUntil.Value > DateTime.Now)
                        ? st.AfterHoursExtendedUntil.Value : DateTime.Now;
                    st.AfterHoursExtendedUntil = baseTime.AddHours(2);
                    session.PendingTriggerContent = null;
                    var extendLine = $"RESUMING|{DateTime.Now:hh:mm tt}|Monitoring until {st.AfterHoursExtendedUntil.Value:hh:mm tt}";
                    try { await File.AppendAllTextAsync(st.LogFile, extendLine + Environment.NewLine, cancellationToken); }
                    catch { }
                }

                // Clear non-actionable triggers after sleep
                if (session.PendingTriggerContent != null &&
                    !session.PendingTriggerContent.StartsWith("ACTION|") &&
                    !session.PendingTriggerContent.StartsWith("EXTEND|"))
                {
                    session.PendingTriggerContent = null;
                }
            }
        }

        return new MonitorAction { Action = "stop", Message = "Multi-PR monitoring cancelled." };
    }

    /// <summary>
    /// Execute a deterministic action in C# (no LLM needed) and return the next state machine action.
    /// </summary>
    private static async Task<MonitorAction> ExecuteAutoAction(MonitorState state, MonitorAction action)
    {
        switch (action.Task)
        {
            case "resolve_thread":
                {
                    var comment = state.ActiveWaitingComment;
                    if (comment == null)
                        return MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

                    var (success, output) = await GitHubCliExecutor.ResolveThreadAsync(comment.Id);
                    DebugLogger.Log("AutoExec", $"resolve_thread {comment.Id}: success={success}");

                    if (!success)
                    {
                        state.CurrentState = MonitorStateId.AwaitingUser;
                        return new MonitorAction
                        {
                            Action = "ask_user",
                            Question = $"Failed to resolve thread: {output}. What would you like to do?",
                            Choices = ["Resume monitoring", "I'll handle it myself"]
                        };
                    }

                    // Clear the waiting comment and let the state machine decide next
                    state.ActiveWaitingComment = null;
                    return MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
                }
            case "merge_pr":
                {
                    var (success, output) = await GitHubCliExecutor.MergePrAsync(state.Owner, state.Repo, state.PrNumber);
                    DebugLogger.Log("AutoExec", $"merge_pr #{state.PrNumber}: success={success}");

                    if (!success)
                    {
                        // Detect branch policy failure — offer richer choices
                        var isBranchPolicy = output.Contains("policy prohibits") || output.Contains("not mergeable");
                        state.CurrentState = MonitorStateId.AwaitingUser;

                        if (isBranchPolicy)
                        {
                            return new MonitorAction
                            {
                                Action = "ask_user",
                                Question = $"Failed to merge PR: {output}. What would you like to do?",
                                Choices = ["Force merge (--admin)", "Wait for another approver", "Resume monitoring", "I'll handle it myself"]
                            };
                        }

                        return new MonitorAction
                        {
                            Action = "ask_user",
                            Question = $"Failed to merge PR: {output}. What would you like to do?",
                            Choices = ["Resume monitoring", "I'll handle it myself"]
                        };
                    }

                    // Merge succeeded — write to log and return merged action
                    var mergedLine = $"STOPPED|{DateTime.Now:hh:mm tt}|🟣 PR #{state.PrNumber} merged successfully";
                    try { await File.AppendAllTextAsync(state.LogFile, mergedLine + Environment.NewLine); }
                    catch { }
                    state.CurrentState = MonitorStateId.Stopped;
                    return new MonitorAction
                    {
                        Action = "merged",
                        Message = $"🟣 PR #{state.PrNumber} merged successfully! {output}"
                    };
                }
            case "merge_pr_admin":
                {
                    var (success, output) = await GitHubCliExecutor.MergePrAdminAsync(state.Owner, state.Repo, state.PrNumber);
                    DebugLogger.Log("AutoExec", $"merge_pr_admin #{state.PrNumber}: success={success}");

                    if (!success)
                    {
                        state.CurrentState = MonitorStateId.AwaitingUser;
                        return new MonitorAction
                        {
                            Action = "ask_user",
                            Question = $"Failed to admin-merge PR: {output}. What would you like to do?",
                            Choices = ["Resume monitoring", "I'll handle it myself"]
                        };
                    }

                    var adminMergedLine = $"STOPPED|{DateTime.Now:hh:mm tt}|🟣 PR #{state.PrNumber} merged (admin) successfully";
                    try { await File.AppendAllTextAsync(state.LogFile, adminMergedLine + Environment.NewLine); }
                    catch { }
                    state.CurrentState = MonitorStateId.Stopped;
                    return new MonitorAction
                    {
                        Action = "merged",
                        Message = $"🟣 PR #{state.PrNumber} merged (admin) successfully! {output}"
                    };
                }
            case "run_new_build":
                {
                    var (success, output) = await GitHubCliExecutor.PushEmptyCommitAsync(
                        state.Owner, state.Repo, state.HeadBranch, state.HeadSha);
                    DebugLogger.Log("AutoExec", $"run_new_build: success={success}, output={output}");

                    if (!success)
                    {
                        state.CurrentState = MonitorStateId.AwaitingUser;
                        return new MonitorAction
                        {
                            Action = "ask_user",
                            Question = $"Failed to trigger new build: {output}. What would you like to do?",
                            Choices = ["Resume monitoring", "I'll handle it myself"]
                        };
                    }

                    return new MonitorAction { Action = "polling", Message = $"New CI run triggered — {output}. Resuming monitoring." };
                }
            default:
                DebugLogger.Error("AutoExec", $"Unknown auto_execute task: {action.Task}");
                return MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        }
    }

    private async Task SleepWithInterruptsAsync(MonitorSession session, int sleepSeconds, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugLogger.Log("Sleep", $"Entering: {sleepSeconds}s, pending={session.PendingTriggerContent ?? "null"}, ct.cancelled={ct.IsCancellationRequested}");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(sleepSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(sleepSeconds), linkedCts.Token);
        var triggerTask = session.WaitForTriggerAsync(linkedCts.Token);

        if (timeoutTask.IsCompleted || triggerTask.IsCompleted)
            DebugLogger.Log("Sleep", $"PRE-COMPLETED: timeout={timeoutTask.Status}, trigger={triggerTask.Status}");

        try
        {
            var completedTask = await Task.WhenAny(timeoutTask, triggerTask);
            sw.Stop();
            var winner = completedTask == timeoutTask ? "timeout" : "trigger";
            DebugLogger.Log("Sleep", $"WhenAny resolved: winner={winner} ({completedTask.Status}), elapsed={sw.ElapsedMilliseconds}ms, timeout={timeoutTask.Status}, trigger={triggerTask.Status}");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            DebugLogger.Log("Sleep", $"Timeout (normal), elapsed={sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            session.ResetTriggerWait();
        }
    }

    private static bool IsAfterHours()
    {
        var now = DateTime.Now;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        return now.Hour < 9 || now.Hour >= 18;
    }

    private static int SecondsUntilNextWorkday9AM()
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(9);
        if (now.Hour >= 9) next = next.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);
        return Math.Max(60, (int)(next - now).TotalSeconds);
    }

    private static int CalculateSleepSeconds(MonitorState state)
    {
        // After-hours check
        if (IsAfterHours())
        {
            // If extended, use normal polling
            if (state.AfterHoursExtendedUntil.HasValue && state.AfterHoursExtendedUntil.Value > DateTime.Now)
            {
                // Fall through to normal adaptive logic
            }
            else
            {
                // Clear any expired extension
                state.AfterHoursExtendedUntil = null;
                var secs = SecondsUntilNextWorkday9AM();
                DebugLogger.Log("Sleep", $"After-hours sleep: {secs}s until next 9AM");
                return secs;
            }
        }
        else
        {
            // Working hours — clear any stale extension
            if (state.AfterHoursExtendedUntil.HasValue)
                state.AfterHoursExtendedUntil = null;
        }

        // Adaptive polling: shorter when checks are in progress, longer when all complete
        var checks = state.Checks;
        if (checks.InProgress > 0 || checks.Queued > 0)
            return 60; // Checks still running — poll every minute
        if (checks.Total == 0)
            return 30; // No checks yet — poll more frequently
        return 120; // All checks complete — poll every 2 minutes
    }

    /// <summary>
    /// Remove ignored comment IDs that no longer appear in the current unresolved comments from GitHub.
    /// This prevents stale CiPassedCommentsIgnored prompts when comments have been resolved.
    /// </summary>
    private static void PruneResolvedIgnoredComments(MonitorState state, List<CommentInfo> allComments, string logContext)
    {
        var currentCommentIds = new HashSet<string>(allComments.Select(c => c.Id));
        var pruned = state.IgnoredCommentIds.RemoveAll(id => !currentCommentIds.Contains(id));
        if (pruned > 0)
            DebugLogger.Log(logContext, $"Pruned {pruned} stale ignored comment IDs (resolved on GitHub)");
    }

    private static string BuildStatusLine(MonitorState state)
    {
        var c = state.Checks;
        var timestamp = DateTime.Now.ToString("hh:mm tt");
        var approvalNames = state.Approvals.Select(a => new { name = a.Author }).ToList();
        var staleNames = state.StaleApprovals.Select(a => new { name = a.Author }).ToList();
        var unresolvedSummary = state.UnresolvedComments.Select(u => new
        {
            id = u.Id,
            author = u.Author,
            summary = u.Body.Length > 80 ? u.Body[..80] + "..." : u.Body,
            url = u.Url
        }).ToList();
        var waitingSummary = state.WaitingForReplyComments.Select(u => new
        {
            id = u.Id,
            author = u.Author,
            summary = u.Body.Length > 80 ? u.Body[..80] + "..." : u.Body,
            url = u.Url
        }).ToList();

        var sleepSeconds = CalculateSleepSeconds(state);
        var isAfterHours = IsAfterHours()
            && !(state.AfterHoursExtendedUntil.HasValue && state.AfterHoursExtendedUntil.Value > DateTime.Now);

        var statusObj = new
        {
            checks = new { c.Passed, c.Failed, c.InProgress, c.Pending, c.Queued, c.Cancelled, c.Total, failures = state.FailedChecks.Select(f => new { f.Name, f.Reason, f.Url }).ToList() },
            approvals = approvalNames,
            stale_approvals = staleNames,
            unresolved = unresolvedSummary,
            waiting_for_reply = waitingSummary,
            next_check_seconds = sleepSeconds,
            after_hours = isAfterHours,
            timestamp
        };

        return $"STATUS|{JsonSerializer.Serialize(statusObj, _jsonOptions)}";
    }

    private static async Task WriteLogEntryAsync(MonitorState state, MonitorAction action)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("hh:mm tt");
            string? line = action.Action switch
            {
                "ask_user" when action.Question != null => BuildTerminalLogLine(state, action),
                "stop" => $"STOPPED|{timestamp}|{action.Message}",
                "merged" => $"STOPPED|{timestamp}|{action.Message}",
                "polling" => $"RESUMING|{timestamp}|Resuming monitoring...",
                _ => null
            };

            if (line != null)
            {
                await File.AppendAllTextAsync(state.LogFile, line + Environment.NewLine);
            }
        }
        catch { /* ignore log write failures */ }
    }

    /// <summary>
    /// Build a TERMINAL| log line with JSON payload that the viewer can parse.
    /// Format: TERMINAL|{"state":"ci_failure","description":"[01:22 PM] ❌ PR #56484 has CI failures..."}
    /// </summary>
    private static string BuildTerminalLogLine(MonitorState state, MonitorAction action)
    {
        var viewerState = state.LastTerminalState switch
        {
            TerminalStateType.NewComment => "new_comment",
            TerminalStateType.MergeConflict => "merge_conflict",
            TerminalStateType.CiFailure => "ci_failure",
            TerminalStateType.CiCancelled => "ci_cancelled",
            TerminalStateType.ApprovedCiGreen => "approved_and_ci_green",
            TerminalStateType.CiPassedCommentsIgnored => "ci_passed_comments_pending",
            _ => "unknown"
        };
        var terminalObj = new { state = viewerState, description = action.Question ?? "" };
        return $"TERMINAL|{JsonSerializer.Serialize(terminalObj, _jsonOptions)}";
    }

    private static async Task PersistIgnoreFileAsync(MonitorState state)
    {
        try
        {
            if (state.IgnoredCommentIds.Count > 0)
            {
                await File.WriteAllLinesAsync(state.IgnoreFile, state.IgnoredCommentIds);
            }
            else if (File.Exists(state.IgnoreFile))
            {
                File.Delete(state.IgnoreFile);
            }
        }
        catch { /* ignore file write failures */ }
    }

    /// <summary>
    /// Checks if a viewer is already running for this PR and launches one if not.
    /// Uses a PID file written by the viewer process for reliable detection.
    /// </summary>
    private static void LaunchViewerIfNeeded(MonitorState state)
    {
        try
        {
            var pidFile = state.LogFile + ".viewer.pid";

            // Check if a viewer is already running for this PR via PID file
            if (File.Exists(pidFile))
            {
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                {
                    try
                    {
                        var existing = Process.GetProcessById(pid);
                        if (!existing.HasExited)
                            return;
                    }
                    catch (ArgumentException)
                    {
                        // Process no longer exists — stale PID file
                    }
                }

                File.Delete(pidFile);
            }

            // Wait for any in-progress update to finish so the exe exists
            UpdateService.UpdateLock.Wait(TimeSpan.FromSeconds(30));
            try
            {
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var viewerExe = isWindows ? "PrCopilot.exe" : "PrCopilot";
                var viewerPath = Path.Combine(AppContext.BaseDirectory, viewerExe);
                var viewerCommand =
                    $"{QuoteForShell(viewerPath)} --viewer --pr {state.PrNumber} " +
                    $"--log {QuoteForShell(state.LogFile)} --trigger {QuoteForShell(state.TriggerFile)} --debug {QuoteForShell(state.DebugLogFile)}";

                if (isWindows)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "wt.exe",
                        Arguments = $"-w 0 new-tab -- {viewerCommand}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    StartViewerOnMac(viewerCommand);
                }
            }
            finally
            {
                UpdateService.UpdateLock.Release();
            }
        }
        catch
        {
            // Viewer launch is best-effort — don't crash monitoring if it fails
        }
    }

    internal static string QuoteForShell(string value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"\"{value.Replace("\"", "\\\"")}\"";
        return $"'{value.Replace("'", "'\\''")}'";
    }

    private static void StartViewerOnMac(string viewerCommand)
    {
        var escaped = EscapeAppleScriptString(viewerCommand);

        // Prefer iTerm when installed; otherwise fall back to Terminal.app.
        if (Directory.Exists("/Applications/iTerm.app"))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e \"tell application \\\"iTerm\\\" to create window with default profile command \\\"{escaped}\\\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return;
            }
            catch
            {
                // Fall through to Terminal.app
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "osascript",
            Arguments = $"-e \"tell application \\\"Terminal\\\" to do script \\\"{escaped}\\\"\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string EscapeAppleScriptString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

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
