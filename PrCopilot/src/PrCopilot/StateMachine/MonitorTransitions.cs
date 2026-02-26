// Licensed under the MIT License.

using PrCopilot.Services;

namespace PrCopilot.StateMachine;

/// <summary>
/// Deterministic state machine transitions. Given current state + event,
/// produces the next state and the action for the agent.
/// No LLM involvement — pure C# logic.
/// </summary>
public static class MonitorTransitions
{
    /// <summary>
    /// Evaluate terminal states from current PR status.
    /// Returns the highest-priority terminal state, or null if none detected.
    /// Priority order: comment → merge conflict → CI failure → CI cancelled → approved+green → CI passed+ignored
    /// </summary>
    public static TerminalStateType? DetectTerminalState(
        MonitorState state,
        List<CommentInfo> newComments,
        bool hasMergeConflict)
    {
        // 1. New unresolved comment (highest priority)
        if (newComments.Count > 0)
            return TerminalStateType.NewComment;

        // 2. Merge conflict
        if (hasMergeConflict)
            return TerminalStateType.MergeConflict;

        // 3. CI failure (checked BEFORE approved — failures can never be masked)
        if (state.Checks.Failed > 0)
            return TerminalStateType.CiFailure;

        // 4. CI cancelled
        if (state.Checks.Cancelled > 0)
            return TerminalStateType.CiCancelled;

        // All checks must be complete for the remaining states
        bool allComplete = state.Checks.Pending == 0 && state.Checks.Queued == 0;
        if (!allComplete)
            return null;

        // 5. Approved + CI green (but skip if we need more approvals than we have)
        if (state.Approvals.Count > 0 && state.Checks.Failed == 0)
        {
            if (state.NeedsAdditionalApproval && state.Approvals.Count <= state.ApprovalCountAtMergeFailure)
                return null; // Don't re-trigger until we get more approvals
            return TerminalStateType.ApprovedCiGreen;
        }

        // 6. CI passed, all comments previously ignored
        if (state.Checks.Failed == 0 && state.IgnoredCommentIds.Count > 0)
            return TerminalStateType.CiPassedCommentsIgnored;

        return null;
    }

    /// <summary>
    /// Generate the action for a detected terminal state.
    /// The state machine builds the exact ask_user payload — no LLM interpretation.
    /// </summary>
    public static MonitorAction BuildTerminalAction(MonitorState state, TerminalStateType terminal)
    {
        var timestamp = DateTime.Now.ToString("hh:mm tt");
        state.LastTerminalState = terminal;
        state.CurrentState = MonitorStateId.AwaitingUser;

        return terminal switch
        {
            TerminalStateType.NewComment => BuildCommentAction(state, timestamp),
            TerminalStateType.MergeConflict => BuildMergeConflictAction(state, timestamp),
            TerminalStateType.CiFailure => BuildCiFailureAction(state, timestamp),
            TerminalStateType.CiCancelled => BuildCiCancelledAction(state, timestamp),
            TerminalStateType.ApprovedCiGreen => BuildApprovedAction(state, timestamp),
            TerminalStateType.CiPassedCommentsIgnored => BuildCiPassedIgnoredAction(state, timestamp),
            _ => new MonitorAction { Action = "stop", Message = "Unknown terminal state" }
        };
    }

    /// <summary>
    /// Process an event from the agent and return the next action.
    /// This is the heart of the state machine — deterministic transitions.
    /// </summary>
    public static MonitorAction ProcessEvent(MonitorState state, string eventType, string? choice, object? data)
    {
        DebugLogger.Log("StateMachine", $"ProcessEvent: state={state.CurrentState}, event={eventType}, choice={choice ?? "null"}, commentFlow={state.CommentFlow}");
        var result = (state.CurrentState, eventType) switch
        {
            // After start, agent signals ready → begin polling (blocks)
            (MonitorStateId.Idle or MonitorStateId.Polling, "ready") => TransitionToPolling(state),

            // User made a choice
            (MonitorStateId.AwaitingUser, "user_chose") => ProcessUserChoice(state, choice, data),

            // LLM finished addressing a comment
            (MonitorStateId.ExecutingTask, "comment_addressed") => ProcessCommentAddressed(state, data),

            // LLM finished investigation
            (MonitorStateId.Investigating, "investigation_complete") => ProcessInvestigationComplete(state, data),

            // LLM applied a fix and pushed
            (MonitorStateId.ApplyingFix, "push_completed") => TransitionToPolling(state),

            // LLM finished executing a generic task
            (MonitorStateId.ExecutingTask, "task_complete") => ProcessTaskComplete(state),

            // Recovery: agent sent task_complete from AwaitingUser (skipped a tool call)
            (MonitorStateId.AwaitingUser, "task_complete") => RecoverFromUnexpectedTaskComplete(state),

            _ => new MonitorAction
            {
                Action = "ask_user",
                Question = $"Unexpected state: {state.CurrentState}/{eventType}. What would you like to do?",
                Choices = ["Resume monitoring", "Stop monitoring"]
            }
        };
        DebugLogger.Log("StateMachine", $"ProcessEvent result: action={result.Action}, task={result.Task ?? "null"}");
        return result;
    }

    private static MonitorAction ProcessTaskComplete(MonitorState state)
    {
        // If we just auto-resolved a thread after addressing a comment, advance to next comment
        if (state.PendingResolveAfterAddress)
        {
            state.PendingResolveAfterAddress = false;
            state.ActiveWaitingComment = null;
            return AdvanceAfterCommentAddressed(state);
        }

        // If we were in a comment flow (e.g. explain_comment), return to the comment prompt
        if (state.CommentFlow == CommentFlowState.SingleCommentPrompt &&
            state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            var c = state.UnresolvedComments[state.CurrentCommentIndex];
            state.CurrentState = MonitorStateId.AwaitingUser;
            return new MonitorAction
            {
                Action = "ask_user",
                Question = $"Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 200)}\"",
                Choices = ["Address this comment", "Explain and suggest what to do", "I'll handle it myself"],
                Context = c
            };
        }

        // If we were handling a waiting-for-reply comment action, return to monitoring
        if (state.ActiveWaitingComment != null)
        {
            state.ActiveWaitingComment = null;
        }

        return TransitionToPolling(state);
    }

    private static MonitorAction RecoverFromUnexpectedTaskComplete(MonitorState state)
    {
        DebugLogger.Log("StateMachine", $"RECOVERY: task_complete from AwaitingUser — agent skipped a tool call. commentFlow={state.CommentFlow}, activeWaiting={state.ActiveWaitingComment != null}");

        // If we had an active waiting comment, the agent probably resolved/followed-up without asking
        if (state.ActiveWaitingComment != null)
        {
            state.ActiveWaitingComment = null;
            return TransitionToPolling(state);
        }

        // If we were in a comment flow, try to resume it
        if (state.CommentFlow != CommentFlowState.None)
            return ProcessTaskComplete(state);

        // Default: resume polling
        return TransitionToPolling(state);
    }

    private static MonitorAction TransitionToPolling(MonitorState state)
    {
        state.CurrentState = MonitorStateId.Polling;
        state.CommentFlow = CommentFlowState.None;
        state.CiFailureFlow = CiFailureFlowState.None;
        state.ActiveWaitingComment = null;
        // The MCP tool will detect this state and start the blocking poll loop
        return new MonitorAction { Action = "polling", Message = "Monitoring..." };
    }

    private static MonitorAction ProcessUserChoice(MonitorState state, string? choice, object? data)
    {
        DebugLogger.Log("StateMachine", $"ProcessUserChoice: choice={choice ?? "null"}, commentFlow={state.CommentFlow}, activeWaiting={state.ActiveWaitingComment != null}");
        // Route based on the active flow
        if (state.CommentFlow != CommentFlowState.None)
            return ProcessCommentChoice(state, choice);

        if (state.CiFailureFlow != CiFailureFlowState.None)
            return ProcessCiFailureChoice(state, choice);

        // Waiting-comment action choices
        if (state.ActiveWaitingComment != null)
            return ProcessWaitingCommentChoice(state, choice);

        // Terminal-state level choices
        return choice switch
        {
            "investigate" => BeginInvestigation(state),
            "show_logs" => BuildShowLogsAction(state),
            "rerun_failed" => BuildRerunAction(state),
            "handle_myself" => StopMonitoring(state),
            "resume" => TransitionToPolling(state),
            "merge" => BuildMergeAction(state),
            "merge_admin" => BuildMergeAdminAction(state),
            "wait_for_approver" => WaitForAdditionalApprover(state),
            _ => new MonitorAction
            {
                Action = "ask_user",
                Question = $"Unrecognized choice: {choice}",
                Choices = ["Resume monitoring", "Stop monitoring"]
            }
        };
    }

    #region Comment Flow

    private static MonitorAction BuildCommentAction(MonitorState state, string timestamp)
    {
        var comments = state.UnresolvedComments;
        if (comments.Count == 1)
        {
            state.CommentFlow = CommentFlowState.SingleCommentPrompt;
            state.CurrentCommentIndex = 0;
            var c = comments[0];
            return new MonitorAction
            {
                Action = "ask_user",
                Question = $"[{timestamp}] 💬 PR #{state.PrNumber} has a new comment from {c.Author} on {c.FilePath}: \"{Truncate(c.Body, 100)}\"",
                Choices = ["Address this comment", "Explain and suggest what to do", "I'll handle it myself"],
                Context = c
            };
        }

        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        var summary = string.Join(", ", comments.Select(c => $"{c.Author} on {c.FilePath}"));
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] 💬 PR #{state.PrNumber} has {comments.Count} unresolved comments: {summary}",
            Choices = ["Address all comments", "Address a specific comment", "I'll handle the comments myself"],
            Context = comments
        };
    }

    private static MonitorAction ProcessCommentChoice(MonitorState state, string? choice)
    {
        return (state.CommentFlow, choice) switch
        {
            (CommentFlowState.MultiCommentPrompt, "address_all") => BeginAddressAll(state),
            (CommentFlowState.MultiCommentPrompt, "address_specific") => BeginPickComment(state),
            (CommentFlowState.MultiCommentPrompt or CommentFlowState.SingleCommentPrompt, "handle_myself") =>
                IgnoreAllCommentsAndResume(state),

            (CommentFlowState.SingleCommentPrompt, "address") => BeginAddressCurrentComment(state),
            (CommentFlowState.SingleCommentPrompt, "explain") => BeginExplainComment(state),

            // Per-comment confirmation in address-all flow
            (CommentFlowState.AddressAllIterating, "address") => EmitAddressCommentAction(state),
            (CommentFlowState.AddressAllIterating, "skip") => SkipAndAdvanceComment(state),
            (CommentFlowState.AddressAllIterating, "done") => TransitionToPolling(state),

            (CommentFlowState.PickComment, _) => HandlePickedComment(state, choice),
            (CommentFlowState.PickRemaining, "continue") => ContinueToNextComment(state),
            (CommentFlowState.PickRemaining, "done") => TransitionToPolling(state),

            _ => TransitionToPolling(state)
        };
    }

    private static MonitorAction BeginAddressAll(MonitorState state)
    {
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.CurrentCommentIndex = 0;
        // Ask for confirmation on the first comment
        var c = state.UnresolvedComments[0];
        state.CurrentState = MonitorStateId.AwaitingUser;
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"Comment (1/{state.UnresolvedComments.Count}): {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 100)}\"",
            Choices = ["Address this comment", "Skip this comment", "Done — resume monitoring"],
            Context = c
        };
    }

    private static MonitorAction SkipAndAdvanceComment(MonitorState state)
    {
        state.CurrentCommentIndex++;
        if (state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            var next = state.UnresolvedComments[state.CurrentCommentIndex];
            state.CurrentState = MonitorStateId.AwaitingUser;
            return new MonitorAction
            {
                Action = "ask_user",
                Question = $"Next comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}): {next.Author} on {next.FilePath}:{next.Line}: \"{Truncate(next.Body, 100)}\"",
                Choices = ["Address this comment", "Skip this comment", "Done — resume monitoring"],
                Context = next
            };
        }
        return TransitionToPolling(state);
    }

    private static MonitorAction BeginPickComment(MonitorState state)
    {
        state.CommentFlow = CommentFlowState.PickComment;
        var choices = state.UnresolvedComments
            .Select((c, i) => $"{i + 1}. {c.Author}: {Truncate(c.Body, 60)} ({c.FilePath})")
            .ToList();
        choices.Add("I'll handle them myself");

        return new MonitorAction
        {
            Action = "ask_user",
            Question = "Which comment would you like to address?",
            Choices = choices,
            Context = state.UnresolvedComments
        };
    }

    private static MonitorAction HandlePickedComment(MonitorState state, string? choice)
    {
        if (choice == "handle_myself")
            return IgnoreAllCommentsAndResume(state);

        // Parse the comment index from "1. author: ..."
        if (int.TryParse(choice?.Split('.').FirstOrDefault(), out int idx) && idx > 0 && idx <= state.UnresolvedComments.Count)
        {
            state.CurrentCommentIndex = idx - 1;
            state.CommentFlow = CommentFlowState.SingleCommentPrompt;
            var c = state.UnresolvedComments[state.CurrentCommentIndex];
            return new MonitorAction
            {
                Action = "ask_user",
                Question = $"Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 200)}\"",
                Choices = ["Address this comment", "Explain and suggest what to do", "I'll handle it myself"],
                Context = c
            };
        }

        return BeginPickComment(state);
    }

    private static MonitorAction BeginAddressCurrentComment(MonitorState state)
    {
        state.CurrentState = MonitorStateId.ExecutingTask;
        return EmitAddressCommentAction(state);
    }

    private static MonitorAction BeginExplainComment(MonitorState state)
    {
        var c = state.UnresolvedComments[state.CurrentCommentIndex];
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "explain_comment",
            Instructions = $"Read and explain this review comment. Recommend whether to implement the change or push back. ONLY analyze THIS SPECIFIC comment — do NOT address, reply to, or fix any other comments. Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}. After explaining, call pr_monitor_next_step with event=task_complete.",
            Context = c
        };
    }

    private static MonitorAction EmitAddressCommentAction(MonitorState state)
    {
        if (state.CurrentCommentIndex >= state.UnresolvedComments.Count)
        {
            // All comments addressed
            return TransitionToPolling(state);
        }

        var c = state.UnresolvedComments[state.CurrentCommentIndex];
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "address_comment",
            Instructions = $"Read the comment, implement the fix. ONLY address THIS SPECIFIC comment — do NOT address, reply to, or fix any other comments. STOP and present your changes to the user for review before committing — use ask_user to show what you changed and ask for approval. Only commit/push after the user approves (honor user's custom instructions for git workflow). After pushing, reply in the thread, then call pr_monitor_next_step with event=comment_addressed. Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}.{CopilotFooter(state)}",
            Context = c
        };
    }

    private static MonitorAction ProcessCommentAddressed(MonitorState state, object? data)
    {
        // Auto-resolve the thread that was just addressed
        var addressedComment = state.UnresolvedComments.Count > state.CurrentCommentIndex
            ? state.UnresolvedComments[state.CurrentCommentIndex]
            : null;
        if (addressedComment != null)
        {
            state.ActiveWaitingComment = addressedComment;
            state.PendingResolveAfterAddress = true;
            return BuildResolveThreadAction(state, addressedComment);
        }

        return AdvanceAfterCommentAddressed(state);
    }

    private static MonitorAction AdvanceAfterCommentAddressed(MonitorState state)
    {
        if (state.CommentFlow == CommentFlowState.AddressAllIterating)
        {
            state.CurrentCommentIndex++;
            if (state.CurrentCommentIndex < state.UnresolvedComments.Count)
            {
                // Ask before advancing to next comment
                var next = state.UnresolvedComments[state.CurrentCommentIndex];
                state.CurrentState = MonitorStateId.AwaitingUser;
                return new MonitorAction
                {
                    Action = "ask_user",
                    Question = $"Next comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}): {next.Author} on {next.FilePath}:{next.Line}: \"{Truncate(next.Body, 100)}\"",
                    Choices = ["Address this comment", "Skip this comment", "Done — resume monitoring"],
                    Context = next
                };
            }
            // All done
            return TransitionToPolling(state);
        }

        // Single comment addressed — check for remaining
        state.CommentFlow = CommentFlowState.PickRemaining;
        var remaining = state.UnresolvedComments
            .Where((_, i) => i != state.CurrentCommentIndex)
            .ToList();

        if (remaining.Count == 0)
            return TransitionToPolling(state);

        state.CurrentState = MonitorStateId.AwaitingUser;
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"Comment addressed. {remaining.Count} more unresolved comment(s) remaining.",
            Choices = ["Address next comment", "I'll handle the rest myself"],
            Context = remaining
        };
    }

    private static MonitorAction IgnoreAllCommentsAndResume(MonitorState state)
    {
        // Add all current unresolved comment IDs to the ignore list
        foreach (var c in state.UnresolvedComments)
        {
            if (!state.IgnoredCommentIds.Contains(c.Id))
                state.IgnoredCommentIds.Add(c.Id);
        }
        return TransitionToPolling(state);
    }

    private static MonitorAction ContinueToNextComment(MonitorState state)
    {
        // Move to next unaddressed comment
        state.CurrentCommentIndex++;
        if (state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            state.CommentFlow = CommentFlowState.SingleCommentPrompt;
            var c = state.UnresolvedComments[state.CurrentCommentIndex];
            state.CurrentState = MonitorStateId.AwaitingUser;
            return new MonitorAction
            {
                Action = "ask_user",
                Question = $"Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 200)}\"",
                Choices = ["Address this comment", "Explain and suggest what to do", "I'll handle it myself"],
                Context = c
            };
        }

        return TransitionToPolling(state);
    }

    #endregion

    #region Waiting-for-Reply Comment Actions

    /// <summary>
    /// Build choices for a viewer-initiated action on a waiting-for-reply comment.
    /// </summary>
    public static MonitorAction BuildWaitingCommentAction(MonitorState state, CommentInfo comment)
    {
        var timestamp = DateTime.Now.ToString("hh:mm tt");
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.ActiveWaitingComment = comment;

        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] ⏳ Waiting comment from {comment.Author} on {comment.FilePath}:{comment.Line}: \"{Truncate(comment.Body, 150)}\" — You replied, ball is in reviewer's court.",
            Choices = ["Resolve this thread", "Follow up with more context", "Reassess my response", "Go back to monitoring"],
            Context = comment
        };
    }

    private static MonitorAction ProcessWaitingCommentChoice(MonitorState state, string? choice)
    {
        var comment = state.ActiveWaitingComment!;

        var result = choice switch
        {
            "resolve" => BuildResolveThreadAction(state, comment),
            "follow_up" => BuildFollowUpAction(state, comment),
            "re_suggest" => BuildReSuggestAction(state, comment),
            "go_back" or "resume" => ClearWaitingAndResume(state),
            _ => ClearWaitingAndResume(state)
        };

        return result;
    }

    private static MonitorAction BuildResolveThreadAction(MonitorState state, CommentInfo comment)
    {
        DebugLogger.Log("StateMachine", $"Auto-resolving thread {comment.Id}");
        return new MonitorAction
        {
            Action = "auto_execute",
            Task = "resolve_thread",
            Message = $"Resolving thread {comment.Id}...",
            Context = comment
        };
    }

    private static MonitorAction BuildFollowUpAction(MonitorState state, CommentInfo comment)
    {
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "follow_up_comment",
            Instructions = $"Read the full thread context and write a follow-up reply to move the conversation forward. ONLY address THIS SPECIFIC comment thread — do NOT reply to or modify any other threads. STOP and present your draft reply to the user for approval before posting. Comment from {comment.Author} on {comment.FilePath}:{comment.Line}: \"{comment.Body}\". URL: {comment.Url}. After posting, call pr_monitor_next_step with event=task_complete.{CopilotFooter(state)}",
            Context = comment
        };
    }

    private static MonitorAction BuildReSuggestAction(MonitorState state, CommentInfo comment)
    {
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "re_suggest_change",
            Instructions = $"Read the full thread context including the reviewer's original comment and your previous reply. ONLY analyze THIS SPECIFIC comment thread — do NOT address, reply to, or modify any other threads. Critically reassess whether the reviewer has a valid point that you dismissed too quickly. Consider: should you actually implement the change? Is your previous reply defensible? Be honest and self-critical. Present your reassessment to the user with a recommendation: either implement the change, revise your reply, or confirm the current reply is correct. STOP and present your analysis to the user before taking any action. Comment from {comment.Author} on {comment.FilePath}:{comment.Line}: \"{comment.Body}\". URL: {comment.Url}. After the user decides, call pr_monitor_next_step with event=task_complete.{CopilotFooter(state)}",
            Context = comment
        };
    }

    private static MonitorAction ClearWaitingAndResume(MonitorState state)
    {
        state.ActiveWaitingComment = null;
        return TransitionToPolling(state);
    }

    #endregion

    #region CI Failure Flow

    private static MonitorAction BuildCiFailureAction(MonitorState state, string timestamp)
    {
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
        var failedNames = string.Join(", ", state.FailedChecks.Select(f => f.Name).Take(5));
        var c = state.Checks;
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] ❌ PR #{state.PrNumber} has CI failures. Failed: {failedNames}. {c.Passed}/{c.Total} passed, {c.Failed} failed.",
            Choices = ["Investigate the failures", "Show me the failed job logs", "Re-run failed jobs", "I'll handle it myself"],
            Context = new { state.FailedChecks, state.Checks }
        };
    }

    private static MonitorAction ProcessCiFailureChoice(MonitorState state, string? choice)
    {
        return (state.CiFailureFlow, choice) switch
        {
            (CiFailureFlowState.CiFailurePrompt, "investigate") => BeginInvestigation(state),
            (CiFailureFlowState.CiFailurePrompt, "show_logs") => BuildShowLogsAction(state),
            (CiFailureFlowState.CiFailurePrompt, "rerun") => BuildRerunAction(state),
            (CiFailureFlowState.CiFailurePrompt, "handle_myself") => StopMonitoring(state),

            (CiFailureFlowState.InvestigationResults, "apply_fix") => BeginApplyFix(state),
            (CiFailureFlowState.InvestigationResults, "ignore") => TransitionToPolling(state),
            (CiFailureFlowState.InvestigationResults, "rerun") => BuildRerunAction(state),
            (CiFailureFlowState.InvestigationResults, "run_new") => BuildRunNewAction(state),
            (CiFailureFlowState.InvestigationResults, "handle_myself") => StopMonitoring(state),

            _ => TransitionToPolling(state)
        };
    }

    private static MonitorAction BeginInvestigation(MonitorState state)
    {
        state.CurrentState = MonitorStateId.Investigating;
        state.CiFailureFlow = CiFailureFlowState.Investigating;
        return new MonitorAction
        {
            Action = "execute",
            Task = "investigate_ci_failure",
            Instructions = "Fetch logs for the failed jobs, analyze root cause, then call pr_monitor_next_step with event=investigation_complete and data containing:\n" +
                "- data.findings: your analysis of what went wrong\n" +
                "- data.suggested_fix: (optional) a suggested code fix if applicable\n" +
                "- data.issue_type: set to \"duplicate_artifact\" if the failure is due to artifacts already existing from a previous run attempt, otherwise set to \"code\" for code/test failures or \"unknown\"",
            Context = new { state.FailedChecks }
        };
    }

    private static MonitorAction ProcessInvestigationComplete(MonitorState state, object? data)
    {
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.CurrentState = MonitorStateId.AwaitingUser;

        // Build dynamic choices based on findings
        var choices = new List<string>();
        if (state.IssueType == "duplicate_artifact")
        {
            // Duplicate artifact from a rerun — only fix is a fresh pipeline run
            choices.Add("Run a new build");
            choices.Add("I'll handle it myself");
        }
        else
        {
            if (state.SuggestedFix != null)
                choices.Add("Apply the suggested fix");
            choices.Add("Ignore and resume monitoring");
            choices.Add("Re-run the failed jobs");
            choices.Add("I'll handle it myself");
        }

        return new MonitorAction
        {
            Action = "ask_user",
            Question = state.InvestigationFindings ?? "Investigation complete.",
            Choices = choices,
            Context = new { state.InvestigationFindings, state.SuggestedFix, state.IssueType, state.FailedChecks }
        };
    }

    private static MonitorAction BeginApplyFix(MonitorState state)
    {
        state.CurrentState = MonitorStateId.ApplyingFix;
        return new MonitorAction
        {
            Action = "execute",
            Task = "apply_fix",
            Instructions = $"Apply the suggested fix: {state.SuggestedFix}. STOP and present your changes to the user for review before committing — use ask_user to show what you changed and ask for approval. Only commit/push after the user approves (honor user's custom instructions for git workflow). Then call pr_monitor_next_step with event=push_completed.",
            Context = new { state.SuggestedFix, state.FailedChecks }
        };
    }

    private static MonitorAction BuildShowLogsAction(MonitorState state)
    {
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "show_logs",
            Instructions = "Fetch and display the failed job logs for the user to review. Then call pr_monitor_next_step with event=task_complete.",
            Context = new { state.FailedChecks }
        };
    }

    private static MonitorAction BuildRerunAction(MonitorState state)
    {
        // Use Playwright (via the agent) to navigate to ADO and click "Rerun failed jobs".
        // Azure DevOps has no API for rerun-failed-only; only the web UI supports it.
        var buildUrl = state.FailedChecks.FirstOrDefault()?.Url;
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.None;
        state.CiFailureFlow = CiFailureFlowState.None;
        return new MonitorAction
        {
            Action = "execute",
            Task = "rerun_via_browser",
            Instructions = $"Use Playwright to rerun ONLY the failed jobs in Azure DevOps. Follow these steps EXACTLY:\n" +
                $"1. Navigate to: {buildUrl}\n" +
                $"2. Wait for the page to load fully, then take a snapshot\n" +
                $"3. IMPORTANT: Do NOT click 'Run new' or 'Run pipeline' — those start a completely new run\n" +
                $"4. Find and click the button or link labeled EXACTLY 'Rerun failed jobs' (it may be in the build summary area or under a '...' menu)\n" +
                $"5. If a confirmation dialog appears, click 'Yes' or 'OK' to confirm\n" +
                $"6. Close the Azure DevOps browser tab after the rerun is triggered\n" +
                $"7. Then call pr_monitor_next_step with event=task_complete",
            Context = new { BuildUrl = buildUrl, state.FailedChecks }
        };
    }

    private static MonitorAction BuildRunNewAction(MonitorState state)
    {
        // Push an empty commit to trigger a fresh CI run.
        // This is deterministic and avoids Playwright/UI automation entirely.
        state.CurrentState = MonitorStateId.Polling;
        state.CommentFlow = CommentFlowState.None;
        state.CiFailureFlow = CiFailureFlowState.None;
        return new MonitorAction
        {
            Action = "auto_execute",
            Task = "run_new_build",
            Instructions = "Triggering a new CI run via empty commit — resuming monitoring."
        };
    }

    #endregion

    private static MonitorAction BuildMergeConflictAction(MonitorState state, string timestamp)
    {
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] ⚠️ PR #{state.PrNumber} has a merge conflict with the base branch.",
            Choices = ["Resolve the conflict (rebase)", "I'll handle it myself"]
        };
    }

    private static MonitorAction BuildCiCancelledAction(MonitorState state, string timestamp)
    {
        var c = state.Checks;
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] 🚫 PR #{state.PrNumber} has cancelled CI checks. {c.Passed}/{c.Total} passed, {c.Cancelled} cancelled.",
            Choices = ["Re-run cancelled jobs", "Resume monitoring", "I'll handle it myself"]
        };
    }

    private static MonitorAction BuildApprovedAction(MonitorState state, string timestamp)
    {
        var approvers = string.Join(", ", state.Approvals.Select(a => a.Author));
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] ✅ PR #{state.PrNumber} is approved by {approvers} and CI is green! ({state.Checks.Passed}/{state.Checks.Total} passed)",
            Choices = ["Merge the PR", "Resume monitoring", "I'll handle it myself"]
        };
    }

    private static MonitorAction BuildCiPassedIgnoredAction(MonitorState state, string timestamp)
    {
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] 🟢 PR #{state.PrNumber} CI is green ({state.Checks.Passed}/{state.Checks.Total} passed). Previously ignored comments are still unresolved.",
            Choices = ["Resume monitoring", "I'll handle it myself"]
        };
    }

    private static MonitorAction BuildMergeAction(MonitorState state)
    {
        DebugLogger.Log("StateMachine", $"Auto-merging PR #{state.PrNumber}");
        return new MonitorAction
        {
            Action = "auto_execute",
            Task = "merge_pr",
            Message = $"Merging PR #{state.PrNumber}...",
            Context = new { state.PrNumber, state.Owner, state.Repo }
        };
    }

    private static MonitorAction BuildMergeAdminAction(MonitorState state)
    {
        DebugLogger.Log("StateMachine", $"Admin-merging PR #{state.PrNumber}");
        return new MonitorAction
        {
            Action = "auto_execute",
            Task = "merge_pr_admin",
            Message = $"Admin-merging PR #{state.PrNumber} (bypassing branch protection)...",
            Context = new { state.PrNumber, state.Owner, state.Repo }
        };
    }

    private static MonitorAction WaitForAdditionalApprover(MonitorState state)
    {
        state.NeedsAdditionalApproval = true;
        state.ApprovalCountAtMergeFailure = state.Approvals.Count;
        DebugLogger.Log("StateMachine", $"Waiting for additional approver (current: {state.Approvals.Count})");
        return TransitionToPolling(state);
    }

    private static MonitorAction StopMonitoring(MonitorState state)
    {
        state.CurrentState = MonitorStateId.Stopped;
        return new MonitorAction { Action = "stop", Message = $"Monitoring stopped for PR #{state.PrNumber}." };
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    /// <summary>
    /// Footer instruction for AI-generated comment replies so reviewers know it's bot-assisted.
    /// </summary>
    private static string CopilotFooter(MonitorState state) =>
        $" IMPORTANT: Append this footer to the END of every comment reply you post (after a blank line and ---): '\\n---\\n🤖 {state.PrAuthor}-copilot'";
}
