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
    /// Serialize a MonitorAction to JSON.
    /// </summary>
    private static string SerializeAction(MonitorAction action)
    {
        return JsonSerializer.Serialize(action, _jsonOptions);
    }

    /// <summary>
    /// Build an execute action for freeform text interpretation by the agent.
    /// </summary>
    private static MonitorAction BuildFreeformInterpretAction(ElicitChoiceResult result, MonitorState state, string? monitorId = null)
    {
        var choicesContext = result.OriginalChoices != null
            ? string.Join(", ", result.OriginalChoices.Select(c =>
            {
                var mapped = MonitorTransitions.ChoiceValueMap.TryGetValue(c, out var v) ? v : c;
                return $"'{c}' → {mapped}";
            }))
            : "none";

        // Build comment context for Path B (custom instruction that modifies code)
        var commentContext = "";
        if (state.CommentFlow != CommentFlowState.None &&
            state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            var c = state.UnresolvedComments[state.CurrentCommentIndex];
            commentContext = $" Active comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}.";
        }

        return new MonitorAction
        {
            Action = "execute",
            MonitorId = monitorId,
            Task = "interpret_freeform",
            Instructions = $"The user typed: \"{result.Value}\". " +
                $"The original question was: \"{result.OriginalQuestion}\". " +
                $"The available choices were: [{choicesContext}]. " +
                "**Path A — clean choice match:** If the text cleanly maps to ONE of the available choices with NO extra instructions, " +
                "tell the user 'I'm interpreting this as [choice display text]' and call pr_monitor_next_step " +
                "with event='user_chose' and choice=<mapped_value>. " +
                "**Path B — custom instruction:** If the text is a custom instruction that doesn't map to a single choice " +
                "(or has extra instructions beyond the choice), execute the user's request directly. " +
                "If the instruction involves code changes: STOP and present your changes to the user for review before committing — " +
                "honor the user's custom instructions for git workflow. Only commit/push after the user approves. " +
                "After pushing, compose a reply describing what was changed and link the commit " +
                $"(use `git rev-parse HEAD` to get the SHA, then format as {state.Owner}/{state.Repo}@SHA). " +
                "Do NOT post the reply yourself — pass it via data='{\"reply_text\": \"your reply\"}' in pr_monitor_next_step. " +
                "Then call pr_monitor_next_step with event='comment_addressed' and data containing reply_text." +
                commentContext +
                (state.CommentFlow != CommentFlowState.None ? MonitorTransitions.CopilotFooter(state) : "") +
                " If the instruction does NOT involve code changes, execute it and call pr_monitor_next_step with event='task_complete'."
        };
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

    /// <summary>
    /// Auto-request a review from Copilot if available and not already requested/reviewed.
    /// Returns true if a review was successfully requested, false otherwise.
    /// Failures are non-critical (copilot may not be available for the repo).
    /// </summary>
    private static async Task<bool> TryRequestCopilotReviewAsync(MonitorState state, ReviewResult reviewResult)
    {
        // The bot's login varies by GitHub API context:
        //   Request reviewer API: "copilot-pull-request-reviewer[bot]"
        //   requested_reviewers response: "Copilot" (login field)
        //   REST /pulls/{n}/reviews: "copilot-pull-request-reviewer[bot]"
        // The no-[bot] alias is defensive — AllReviewAuthors comes from REST reviews,
        // but other code paths use GraphQL which may strip the [bot] suffix.
        const string copilotRequestName = "copilot-pull-request-reviewer[bot]";
        string[] copilotAliases = ["Copilot", "copilot-pull-request-reviewer[bot]", "copilot-pull-request-reviewer"];

        try
        {
            // Check if copilot has already submitted a review (any state)
            if (copilotAliases.Any(alias => reviewResult.AllReviewAuthors.Contains(alias)))
            {
                DebugLogger.Log("CopilotReview", "Copilot has already reviewed this PR — skipping request");
                return false;
            }

            // Check if copilot is already in the requested reviewers list
            var requestedReviewers = await PrStatusFetcher.FetchRequestedReviewersAsync(
                state.Owner, state.Repo, state.PrNumber);
            if (requestedReviewers == null)
            {
                DebugLogger.Log("CopilotReview", "Could not fetch requested reviewers (API failure) — skipping to avoid redundant requests");
                return false;
            }
            if (copilotAliases.Any(alias => requestedReviewers.Contains(alias)))
            {
                DebugLogger.Log("CopilotReview", "Copilot already in requested reviewers — skipping");
                return false;
            }

            // Request using the bot's full login name
            var (success, output) = await GitHubCliExecutor.RequestReviewAsync(
                state.Owner, state.Repo, state.PrNumber, copilotRequestName);

            if (!success)
            {
                DebugLogger.Log("CopilotReview", $"Copilot review request API failed: {output}");
                return false;
            }

            // GitHub API returns 200 even when a reviewer isn't valid for the repo —
            // it silently drops invalid names. Verify copilot was actually added.
            var postRequestReviewers = await PrStatusFetcher.FetchRequestedReviewersAsync(
                state.Owner, state.Repo, state.PrNumber);
            if (postRequestReviewers != null && copilotAliases.Any(alias => postRequestReviewers.Contains(alias)))
            {
                DebugLogger.Log("CopilotReview", "Copilot review requested and verified successfully");
                return true;
            }

            DebugLogger.Log("CopilotReview", "Copilot review request returned 200 but reviewer was not added — copilot likely not available for this repo");
            return false;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("CopilotReview", $"Failed to request copilot review (non-critical): {ex}");
            return false;
        }
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
                copilot_review_requested = false,
                message = $"Resuming existing monitor for PR #{prNumber}. Call pr_monitor_next_step with event='ready' to continue."
            }, _jsonOptions);
        }

        // Fetch all PR data
        var prInfo = await PrStatusFetcher.FetchPrInfoAsync(owner, repo, prNumber);

        var checkResult = await PrStatusFetcher.FetchCheckRunsAsync(owner, repo, prInfo.HeadSha);

        var reviewResult = await PrStatusFetcher.FetchReviewsAsync(owner, repo, prNumber, prInfo.HeadSha);
        var allComments = await PrStatusFetcher.FetchAndCleanUnresolvedCommentsAsync(owner, repo, prNumber, prInfo.Author, reviewResult.Approvals);

        // Fetch branch protection conversation requirement (cached for session lifetime)
        var requiresConversationResolution = false;
        if (!string.IsNullOrEmpty(prInfo.BaseBranch))
        {
            requiresConversationResolution = await PrStatusFetcher.FetchRequiresConversationResolutionAsync(owner, repo, prInfo.BaseBranch);
        }

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

        DebugLogger.Log("PrMonitorStart", $"Fetched: {checkResult.Counts.Total} checks ({checkResult.Counts.Failed} failed), {reviewResult.Approvals.Count} approvals, {allComments.Count} comments, conversationResolution={requiresConversationResolution}");

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
            BaseBranch = prInfo.BaseBranch,
            SessionFolder = sessionFolder,
            CurrentState = MonitorStateId.Idle,
            Checks = checkResult.Counts,
            FailedChecks = checkResult.Failures,
            Approvals = reviewResult.Approvals,
            StaleApprovals = reviewResult.StaleApprovals,
            HasMergeConflict = !prInfo.Mergeable && prInfo.MergeableState == "dirty",
            UnresolvedComments = allComments.Where(c => !c.IsWaitingForReply).ToList(),
            WaitingForReplyComments = allComments.Where(c => c.IsWaitingForReply).ToList(),
            RequiresConversationResolution = requiresConversationResolution,
            LastPollTime = DateTime.UtcNow
        };

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

        // Auto-request copilot review if not already requested/reviewed on this PR
        var copilotRequested = await TryRequestCopilotReviewAsync(state, reviewResult);

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
            copilot_review_requested = copilotRequested,
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
        [Description("Event: ready, user_chose, comment_addressed, comment_replied, investigation_complete, push_completed, task_complete")] string @event,
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

                // Elicit directly for multi-PR terminal states
                if (action.Action == "ask_user" && action.Choices?.Count > 0)
                {
                    await WriteLogEntryAsync(
                        action.MonitorId != null && _sessions.TryGetValue(action.MonitorId, out var logSession) ? logSession.State : heartbeatSession.State,
                        action);

                    var elicitResult = await ElicitationHelper.ElicitChoiceAsync(server!, action, cancellationToken);
                    DebugLogger.Log("NextStep", $"Multi-PR elicited: {(elicitResult.IsFreeform ? "freeform" : "choice")}={elicitResult.Value}");

                    // Log the elicitation result
                    if (action.MonitorId != null && _sessions.TryGetValue(action.MonitorId, out var elicitSession))
                        await WriteElicitedLogEntryAsync(elicitSession.State, elicitResult.Value);

                    // Freeform text — let the agent interpret it
                    if (elicitResult.IsFreeform)
                    {
                        var freeformState = action.MonitorId != null && _sessions.TryGetValue(action.MonitorId, out var fs)
                            ? fs.State : heartbeatSession.State;
                        freeformState.CurrentState = MonitorStateId.ExecutingTask;
                        var freeformAction = BuildFreeformInterpretAction(elicitResult, freeformState, action.MonitorId);
                        return SerializeAction(freeformAction);
                    }

                    // Return the choice to the agent with instructions to feed it to the specific PR's monitor
                    return SerializeAction(new MonitorAction
                    {
                        Action = "execute",
                        MonitorId = action.MonitorId,
                        Task = "relay_choice",
                        Instructions = $"The user chose '{elicitResult.Value}' for PR monitor {action.MonitorId}. Call pr_monitor_next_step with monitorId='{action.MonitorId}', event='user_chose', choice='{elicitResult.Value}'. After handling, call pr_monitor_next_step with monitorId='all' and event='ready' to resume monitoring all PRs."
                    });
                }

                await WriteLogEntryAsync(
                    action.MonitorId != null && _sessions.TryGetValue(action.MonitorId, out var actionSession) ? actionSession.State : heartbeatSession.State,
                    action);

                return SerializeAction(action);
            }
            catch (Exception ex)
            {
                DebugLogger.Error("NextStep", ex);
                var errorAction = new MonitorAction
                {
                    Action = "ask_user",
                    Question = $"Internal error during multi-PR monitoring: {ex.Message}. What would you like to do?",
                    Choices = ["Resume monitoring", "Stop monitoring"]
                };
                var errorResult = await ElicitationHelper.ElicitChoiceAsync(server!, errorAction, cancellationToken);
                return SerializeAction(new MonitorAction
                {
                    Action = errorResult.Value == "stop" ? "stop" : "polling",
                    Message = errorResult.Value == "stop" ? "Monitoring stopped." : "Resuming monitoring..."
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

        // Start session-level heartbeat to keep MCP client alive.
        // Capture generation before try so the finally block can use it.
        var heartbeatGen = session.StartHeartbeat(server, progressToken);

        try
        {
            var state = session.State;
            DebugLogger.Log("NextStep", $"Called: event={@event}, choice={choice ?? "null"}, state={state.CurrentState}");
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
                    // Build the waiting comment action and let the main elicitation loop handle it
                    var triggerAction = MonitorTransitions.BuildWaitingCommentAction(state, comment);
                    if (triggerAction.Action == "ask_user" && triggerAction.Choices?.Count > 0)
                    {
                        await WriteLogEntryAsync(state, triggerAction);
                        var triggerResult = await ElicitationHelper.ElicitChoiceAsync(server, triggerAction, cancellationToken);
                        DebugLogger.Log("NextStep", $"Elicited viewer trigger: {(triggerResult.IsFreeform ? "freeform" : "choice")}={triggerResult.Value}");
                        await WriteElicitedLogEntryAsync(state, triggerResult.Value);

                        // Freeform text — let the agent interpret it
                        if (triggerResult.IsFreeform)
                        {
                            state.CurrentState = MonitorStateId.ExecutingTask;
                            return SerializeAction(BuildFreeformInterpretAction(triggerResult, state));
                        }

                        // Override event/choice so the state machine processes the user's choice
                        @event = "user_chose";
                        choice = triggerResult.Value;
                    }
                    else
                    {
                        await WriteLogEntryAsync(state, triggerAction);
                        DebugLogger.Log("NextStep", $"Returning trigger action: {triggerAction.Action}");
                        return SerializeAction(triggerAction);
                    }
                }
                else
                {
                    session.PendingTriggerContent = null;
                }
            }

            // Clear stale reply text before parsing — prevents a prior comment's reply leaking to this one
            if (@event == "comment_addressed" || @event == "comment_replied")
                state.PendingReplyText = null;

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
                    if (root.TryGetProperty("reply_text", out var replyText))
                    {
                        // Only accept reply_text for comment events to prevent stale values leaking across steps
                        if (@event == "comment_addressed" || @event == "comment_replied")
                            state.PendingReplyText = replyText.GetString();
                    }
                }
                catch { /* ignore parse errors in data */ }
            }

            // Feed event to state machine
            var action = MonitorTransitions.ProcessEvent(state, @event, choice, data);
            DebugLogger.Log("NextStep", $"State machine returned: action={action.Action}, task={action.Task ?? "null"}");

            // Core loop: handle auto_execute, polling, and elicitation before returning to agent.
            // The agent only sees execute/stop actions — all ask_user flows are resolved here
            // via MCP elicitation (bypassing the LLM entirely for user choices).
            while (true)
            {
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

                    // After polling, continue the loop (may get ask_user, auto_execute, etc.)
                    continue;
                }

                // Elicit: ask_user actions are resolved directly via MCP elicitation
                if (action.Action == "ask_user" && action.Choices?.Count > 0)
                {
                    await WriteLogEntryAsync(state, action);

                    var elicitResult = await ElicitationHelper.ElicitChoiceAsync(server, action, cancellationToken);
                    DebugLogger.Log("NextStep", $"Elicited: {(elicitResult.IsFreeform ? "freeform" : "choice")}={elicitResult.Value}");
                    await WriteElicitedLogEntryAsync(state, elicitResult.Value);

                    // Freeform text — transition to executing and let the agent interpret it
                    if (elicitResult.IsFreeform)
                    {
                        state.CurrentState = MonitorStateId.ExecutingTask;
                        action = BuildFreeformInterpretAction(elicitResult, state);
                        break;
                    }

                    // Feed the user's choice back into the state machine and continue the loop
                    action = MonitorTransitions.ProcessEvent(state, "user_chose", elicitResult.Value, null);
                    DebugLogger.Log("NextStep", $"Post-elicit: action={action.Action}, task={action.Task ?? "null"}");
                    continue;
                }

                // Any other action (execute, stop, merged) — break out and return to agent
                break;
            }

            // Write to log if transitioning
            await WriteLogEntryAsync(state, action);

            return SerializeAction(action);
        }
        catch (Exception ex)
        {
            DebugLogger.Error("NextStep", ex);
            var errorAction = new MonitorAction
            {
                Action = "ask_user",
                Question = $"Internal error: {ex.Message}. What would you like to do?",
                Choices = ["Resume monitoring", "Stop monitoring"]
            };
            try
            {
                var errorResult = await ElicitationHelper.ElicitChoiceAsync(server, errorAction, cancellationToken);
                return SerializeAction(new MonitorAction
                {
                    Action = errorResult.Value == "stop" ? "stop" : "polling",
                    Message = errorResult.Value == "stop" ? "Monitoring stopped." : "Resuming monitoring..."
                });
            }
            catch
            {
                // If elicitation itself fails, just stop
                return SerializeAction(new MonitorAction { Action = "stop", Message = $"Internal error: {ex.Message}" });
            }
        }
        finally
        {
            // Stop heartbeat when tool call returns — but only if a newer call hasn't
            // already started a new heartbeat (prevents stale finally blocks from killing
            // a replacement call's heartbeat).
            session.StopHeartbeat(heartbeatGen);
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

                var allComments = await PrStatusFetcher.FetchAndCleanUnresolvedCommentsAsync(state.Owner, state.Repo, state.PrNumber, state.PrAuthor, reviewResult.Approvals);

                var needsAction = allComments.Where(c => !c.IsWaitingForReply).ToList();
                var waitingForReply = allComments.Where(c => c.IsWaitingForReply).ToList();
                state.UnresolvedComments = needsAction;
                state.WaitingForReplyComments = waitingForReply;

                // Re-request review for reviewers whose comments are all waiting-for-reply.
                // This catches cases where the session was interrupted before the re-request fired.
                await ReRequestReviewForFullyRepliedReviewersAsync(state, waitingForReply, needsAction);

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

        var allComments = await PrStatusFetcher.FetchAndCleanUnresolvedCommentsAsync(state.Owner, state.Repo, state.PrNumber, state.PrAuthor, reviewResult.Approvals);

        var needsAction = allComments.Where(c => !c.IsWaitingForReply).ToList();
        var waitingForReply = allComments.Where(c => c.IsWaitingForReply).ToList();
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

                    // Post pending reply before resolving (agent composed the text, server posts it)
                    if (!await PostPendingReplyAsync(state, comment))
                    {
                        state.ClearPendingCommentState();
                        state.CurrentState = MonitorStateId.AwaitingUser;
                        return new MonitorAction
                        {
                            Action = "ask_user",
                            Question = "Failed to post thread reply after 3 attempts. What would you like to do?",
                            Choices = ["Resume monitoring", "I'll handle it myself"]
                        };
                    }

                    var (success, output) = await GitHubCliExecutor.ResolveThreadAsync(comment.Id);
                    DebugLogger.Log("AutoExec", $"resolve_thread {comment.Id}: success={success}");

                    if (!success)
                    {
                        state.ClearPendingCommentState();
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
            case "post_thread_reply":
                {
                    // Post reply without resolving (used for human reviewer pushback/clarification)
                    var comment = state.ActiveWaitingComment;
                    if (comment != null && !await PostPendingReplyAsync(state, comment))
                    {
                        state.ClearPendingCommentState();
                        state.CurrentState = MonitorStateId.AwaitingUser;
                        return new MonitorAction
                        {
                            Action = "ask_user",
                            Question = "Failed to post thread reply after 3 attempts. What would you like to do?",
                            Choices = ["Resume monitoring", "I'll handle it myself"]
                        };
                    }

                    state.ActiveWaitingComment = null;
                    return MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
                }
            case "request_review":
                {
                    var reviewer = state.PendingReRequestReviewer ?? "";
                    if (!string.IsNullOrEmpty(reviewer))
                    {
                        // Check if reviewer already has a pending review request (ground truth from API)
                        var alreadyRequested = await PrStatusFetcher.FetchRequestedReviewersAsync(
                            state.Owner, state.Repo, state.PrNumber);
                        if (alreadyRequested is not null && alreadyRequested.Contains(reviewer))
                        {
                            DebugLogger.Log("AutoExec", $"request_review {reviewer}: skipped — already in requested_reviewers");
                            return MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
                        }

                        // Fresh check: verify the reviewer has no new unresolved comments
                        // that arrived after the current batch was captured
                        try
                        {
                            var freshComments = await PrStatusFetcher.FetchUnresolvedCommentsAsync(
                                state.Owner, state.Repo, state.PrNumber, state.PrAuthor);
                            var hasUnresolved = freshComments.Any(c =>
                                string.Equals(c.Author, reviewer, StringComparison.OrdinalIgnoreCase) && !c.IsWaitingForReply);
                            if (hasUnresolved)
                            {
                                DebugLogger.Log("AutoExec", $"request_review {reviewer}: skipped — reviewer still has unresolved comments");
                                return MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Non-critical — proceed with re-request if fresh check fails
                            DebugLogger.Log("AutoExec", $"request_review fresh check failed (non-critical): {ex.Message}");
                        }

                        var (success, output) = await GitHubCliExecutor.RequestReviewAsync(state.Owner, state.Repo, state.PrNumber, reviewer);
                        DebugLogger.Log("AutoExec", $"request_review {reviewer}: success={success}");
                        // Non-critical — continue even if re-request fails
                        if (!success)
                            DebugLogger.Log("AutoExec", $"request_review failed (non-critical): {output}");
                    }
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

    /// <summary>
    /// Post the agent's pending reply text to the review thread via the REST API.
    /// Retries up to 2 times on transient failures. Returns true if posted (or nothing to post).
    /// </summary>
    private static async Task<bool> PostPendingReplyAsync(MonitorState state, CommentInfo comment)
    {
        if (string.IsNullOrWhiteSpace(state.PendingReplyText))
            return true;

        var replyText = state.PendingReplyText;

        if (!comment.RestCommentId.HasValue)
        {
            DebugLogger.Error("AutoExec", $"Cannot post reply — no RestCommentId for thread {comment.Id}");
            state.PendingReplyText = null;
            return false;
        }

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var (success, output) = await GitHubCliExecutor.PostThreadReplyAsync(
                state.Owner, state.Repo, state.PrNumber, comment.RestCommentId.Value, replyText);
            DebugLogger.Log("AutoExec", $"post_thread_reply {comment.Id} (REST ID {comment.RestCommentId.Value}): attempt={attempt}, success={success}");

            if (success)
            {
                state.PendingReplyText = null;
                return true;
            }

            DebugLogger.Error("AutoExec", $"Failed to post reply (attempt {attempt}/{maxRetries}): {output}");
            if (attempt < maxRetries)
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
        }

        // All attempts failed; clear pending reply to avoid posting stale text to another thread
        state.PendingReplyText = null;
        return false;
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
    /// Re-requests review from reviewers whose unresolved comments are ALL waiting-for-reply
    /// (i.e., we've replied to every one) but whose review hasn't been re-requested yet.
    /// Uses the GitHub API to check who already has a pending review request — no in-memory
    /// tracking needed. This is resilient to session interruptions and restarts.
    /// </summary>
    private static async Task ReRequestReviewForFullyRepliedReviewersAsync(
        MonitorState state, List<CommentInfo> waitingForReply, List<CommentInfo> needsAction)
    {
        if (waitingForReply.Count == 0)
            return;

        // Fetch current requested reviewers from GitHub — this is the source of truth
        var alreadyRequested = await PrStatusFetcher.FetchRequestedReviewersAsync(
            state.Owner, state.Repo, state.PrNumber);

        // If the API call failed, skip re-request logic to avoid false positives
        if (alreadyRequested is null)
        {
            DebugLogger.Log("PollLoop", "Skipping re-request catch-up — FetchRequestedReviewersAsync failed");
            return;
        }

        // Get unique reviewers with waiting-for-reply comments
        var reviewersWithWaiting = waitingForReply
            .Where(c => !string.IsNullOrEmpty(c.Author))
            .Select(c => c.Author)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Combine all comments for the shared check
        var allComments = needsAction.Concat(waitingForReply).ToList();

        foreach (var reviewer in reviewersWithWaiting)
        {
            // Use the shared core check with GitHub's requested_reviewers as the already-requested set
            if (!MonitorTransitions.ShouldReRequestReview(
                    reviewer, state.PrAuthor, state.CurrentUser,
                    alreadyRequested, allComments))
                continue;

            DebugLogger.Log("PollLoop", $"Re-requesting review from {reviewer} — all their comments are waiting-for-reply");
            var (success, output) = await GitHubCliExecutor.RequestReviewAsync(state.Owner, state.Repo, state.PrNumber, reviewer);
            if (success)
                DebugLogger.Log("PollLoop", $"Re-requested review from {reviewer}");
            else
                DebugLogger.Log("PollLoop", $"Failed to re-request review from {reviewer} (non-critical): {output}");
        }
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
            summary = u.Body.Length > 200 ? u.Body[..200] + "..." : u.Body,
            url = u.Url
        }).ToList();
        var waitingSummary = state.WaitingForReplyComments.Select(u => new
        {
            id = u.Id,
            author = u.Author,
            summary = u.Body.Length > 200 ? u.Body[..200] + "..." : u.Body,
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
    /// Write an ELICITED| log line when the user makes a choice via MCP elicitation.
    /// Format: ELICITED|hh:mm tt|choice_value
    /// </summary>
    private static async Task WriteElicitedLogEntryAsync(MonitorState state, string choiceValue)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("hh:mm tt");
            var line = $"ELICITED|{timestamp}|{choiceValue}";
            await File.AppendAllTextAsync(state.LogFile, line + Environment.NewLine);
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
            _ => "unknown"
        };
        var terminalObj = new { state = viewerState, description = action.Question ?? "" };
        return $"TERMINAL|{JsonSerializer.Serialize(terminalObj, _jsonOptions)}";
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
