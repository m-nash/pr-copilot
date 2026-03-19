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

        // 2. Reviewer replied to a comment we previously replied to
        // Check if any waiting-for-reply comment got a reviewer response (LastReplyAuthor changed)
        var repliedComment = state.WaitingForReplyComments.FirstOrDefault(c =>
            !c.IsWaitingForReply && !string.IsNullOrEmpty(c.LastReplyAuthor) && c.LastReplyAuthor != state.PrAuthor && c.LastReplyAuthor != state.CurrentUser);
        if (repliedComment != null)
        {
            state.RepliedComment = repliedComment;
            return TerminalStateType.ReviewerReplied;
        }

        // 3. Merge conflict
        if (hasMergeConflict)
            return TerminalStateType.MergeConflict;

        // 4. CI failure (checked BEFORE approved — failures can never be masked)
        //    But suppress if we're already waiting to rerun after remaining checks finish
        if (state.Checks.Failed > 0 && !state.PendingRerunWhenChecksComplete)
            return TerminalStateType.CiFailure;

        // 5. CI cancelled
        if (state.Checks.Cancelled > 0)
            return TerminalStateType.CiCancelled;

        // All checks must be complete for the remaining states (ignore legacy pending policy statuses)
        bool allComplete = state.Checks.InProgress == 0 && state.Checks.Queued == 0;
        if (!allComplete)
            return null;

        // 6. Approved + CI green (but skip if we need more approvals than we have)
        if (state.Approvals.Count > 0 && state.Checks.Failed == 0)
        {
            if (state.NeedsAdditionalApproval && state.Approvals.Count <= state.ApprovalCountAtMergeFailure)
                return null; // Don't re-trigger until we get more approvals
            return TerminalStateType.ApprovedCiGreen;
        }

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
            TerminalStateType.ReviewerReplied => BuildReviewerRepliedAction(state, timestamp),
            TerminalStateType.MergeConflict => BuildMergeConflictAction(state, timestamp),
            TerminalStateType.CiFailure => BuildCiFailureAction(state, timestamp),
            TerminalStateType.CiCancelled => BuildCiCancelledAction(state, timestamp),
            TerminalStateType.ApprovedCiGreen => BuildApprovedAction(state, timestamp),
            _ => new MonitorAction { Action = "stop", Message = "Unknown terminal state" }
        };
    }

    /// <summary>
    /// Maps every choice display text to the exact value the agent must pass back
    /// in pr_monitor_next_step. Used by elicitation to build enum schemas and
    /// as fallback for choice_map in legacy ask_user flow.
    /// </summary>
    internal static readonly Dictionary<string, string> ChoiceValueMap = new()
    {
        // Terminal-level choices (ProcessUserChoice)
        ["Merge the PR"] = "merge",
        ["Force merge (--admin)"] = "merge_admin",
        ["Wait for another approver"] = "wait_for_approver",
        ["Resume monitoring"] = "resume",
        ["Stop monitoring"] = "stop",
        ["I'll handle it myself"] = "handle_myself",
        ["Re-run cancelled jobs"] = "rerun_failed",
        ["Resolve the conflict (rebase)"] = "rebase",

        // Comment flow choices (ProcessCommentChoice)
        ["Address all comments"] = "address_all",
        ["Explain each one by one"] = "explain_all",
        ["Address a specific comment"] = "address_specific",
        ["Address this comment"] = "address",
        ["Explain and suggest what to do"] = "explain",
        ["Apply the recommendation"] = "apply_fix",
        ["I'll handle the comments myself"] = "handle_myself",
        ["I'll handle them myself"] = "handle_myself",
        ["Skip this comment"] = "skip",
        ["Done — resume monitoring"] = "done",
        ["Address next comment"] = "continue",
        ["I'll handle the rest myself"] = "done",

        // Manual handling flow choices
        ["Done, continue monitoring"] = "done_handling",

        // CI failure flow choices (ProcessCiFailureChoice)
        ["Investigate the failures"] = "investigate",
        ["Re-run failed jobs"] = "rerun",
        ["Apply the recommendation"] = "apply_fix",
        ["Run a new build"] = "run_new",

        // Waiting-for-reply comment choices (ProcessWaitingCommentChoice)
        ["Resolve this thread"] = "resolve",
        ["Go back to monitoring"] = "go_back",
    };

    /// <summary>
    /// Called by the poll loop when PendingRerunWhenChecksComplete is set
    /// and all remaining checks have finished. Triggers the deferred rerun.
    /// </summary>
    public static MonitorAction CompletePendingRerun(MonitorState state)
    {
        DebugLogger.Log("StateMachine", "All checks complete — executing deferred rerun");
        state.PendingRerunWhenChecksComplete = false;
        return BuildRerunAction(state);
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

            // Freeform Path A: agent mapped freeform text back to a choice
            (MonitorStateId.ExecutingTask, "user_chose") => ProcessUserChoice(state, choice, data),

            // LLM finished addressing a comment
            (MonitorStateId.ExecutingTask, "comment_addressed") => ProcessCommentAddressed(state, data),

            // LLM replied to a comment (pushback/clarification — no code changes)
            (MonitorStateId.ExecutingTask, "comment_replied") => ProcessCommentReplied(state),

            // LLM finished investigation
            (MonitorStateId.Investigating, "investigation_complete") => ProcessInvestigationComplete(state, data),

            // LLM applied a fix and pushed
            (MonitorStateId.ApplyingFix, "push_completed") => TransitionToPolling(state),

            // Recovery: agent applied fix + pushed during investigation (skipped investigation_complete → apply_fix flow)
            (MonitorStateId.Investigating, "push_completed") => TransitionToPolling(state),

            // LLM finished executing a generic task
            (MonitorStateId.ExecutingTask, "task_complete") => ProcessTaskComplete(state),

            // Recovery: agent sent task_complete from AwaitingUser (skipped a tool call)
            (MonitorStateId.AwaitingUser, "task_complete") => RecoverFromUnexpectedTaskComplete(state),

            _ => RecoverFromUnexpectedState(state, eventType)
        };
        DebugLogger.Log("StateMachine", $"ProcessEvent result: action={result.Action}, task={result.Task ?? "null"}");
        return result;
    }

    private static MonitorAction ProcessTaskComplete(MonitorState state)
    {
        // If we just completed a re-request review, advance to next comment
        if (state.PendingReRequestReviewer != null)
        {
            var summary = state.PendingResolveSummary ?? "Comment addressed";
            state.PendingReRequestReviewer = null;
            state.PendingResolveSummary = null;
            return AdvanceAfterComment(state, summary);
        }

        // If we just auto-resolved a thread after addressing/replying to a comment, advance
        if (state.PendingResolveAfterAddress)
        {
            var summary = state.PendingResolveSummary ?? "Comment addressed";
            state.PendingResolveAfterAddress = false;
            state.ActiveWaitingComment = null;

            // Check if this was the last comment from this reviewer — if so, re-request review
            var resolvedComment = state.CurrentCommentIndex < state.UnresolvedComments.Count
                ? state.UnresolvedComments[state.CurrentCommentIndex]
                : null;
            if (resolvedComment != null && ShouldReRequestReview(state, resolvedComment.Author))
            {
                state.PendingReRequestReviewer = resolvedComment.Author;
                state.PendingResolveSummary = summary;
                state.ReviewsReRequested.Add(resolvedComment.Author);
                return BuildReRequestReviewAction(state, resolvedComment.Author);
            }

            state.PendingResolveSummary = null;
            return AdvanceAfterComment(state, summary);
        }

        // If we just posted a reply (no resolve) — advance to next comment or re-request
        if (state.PendingAdvanceAfterReply != null)
        {
            var summary = state.PendingAdvanceAfterReply;
            state.PendingAdvanceAfterReply = null;

            var comment = state.CurrentCommentIndex < state.UnresolvedComments.Count
                ? state.UnresolvedComments[state.CurrentCommentIndex]
                : null;
            if (comment != null && ShouldReRequestReview(state, comment.Author))
            {
                state.PendingReRequestReviewer = comment.Author;
                state.PendingResolveSummary = summary;
                state.ReviewsReRequested.Add(comment.Author);
                return BuildReRequestReviewAction(state, comment.Author);
            }

            return AdvanceAfterComment(state, summary);
        }

        // Explain-all flow: after explain or freeform task, re-present per-comment choices
        if (state.CommentFlow == CommentFlowState.ExplainAllIterating &&
            state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            state.PendingExplainResult = false;
            var c = state.UnresolvedComments[state.CurrentCommentIndex];
            state.CurrentState = MonitorStateId.AwaitingUser;
            return new MonitorAction
            {
                Action = "ask_user",
                Question = $"Comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}) from {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 500)}\"",
                Choices = ["Apply the recommendation", "Skip this comment", "Done — resume monitoring"],
                Context = c
            };
        }

        // Single comment flow: after explain, show post-explain choices
        if (state.CommentFlow == CommentFlowState.SingleCommentPrompt &&
            state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            var c = state.UnresolvedComments[state.CurrentCommentIndex];

            // After explain, show post-explain choices
            if (state.PendingExplainResult)
            {
                state.PendingExplainResult = false;
                state.CurrentState = MonitorStateId.AwaitingUser;
                return new MonitorAction
                {
                    Action = "ask_user",
                    Question = $"Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 500)}\"",
                    Choices = ["Apply the recommendation", "I'll handle it myself"],
                    Context = c
                };
            }

            // Safety fallback: if we reach here without explain having run, trigger it
            return BeginExplainComment(state);
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

    private static MonitorAction RecoverFromUnexpectedState(MonitorState state, string eventType)
    {
        var priorState = state.CurrentState;
        DebugLogger.Log("StateMachine", $"RECOVERY: Unexpected state {priorState}/{eventType}. Transitioning to AwaitingUser so next user_chose can recover.");
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.None;
        state.CiFailureFlow = CiFailureFlowState.None;
        state.ActiveWaitingComment = null;
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"Unexpected state: {priorState}/{eventType}. What would you like to do?",
            Choices = ["Resume monitoring", "Stop monitoring"]
        };
    }

    private static MonitorAction TransitionToPolling(MonitorState state)
    {
        state.CurrentState = MonitorStateId.Polling;
        state.CommentFlow = CommentFlowState.None;
        state.CiFailureFlow = CiFailureFlowState.None;
        state.ActiveWaitingComment = null;
        state.PendingReRequestReviewer = null;
        state.ReviewsReRequested.Clear();
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
            "rerun_failed" => BuildRerunAction(state),
            "handle_myself" => StopMonitoring(state),
            "stop" => StopMonitoring(state),
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

    private static MonitorAction BuildReviewerRepliedAction(MonitorState state, string timestamp)
    {
        if (state.RepliedComment is null)
            return TransitionToPolling(state);

        var comment = state.RepliedComment;
        // Route directly to SingleCommentPrompt — same choices as a new comment.
        // Overwrites UnresolvedComments with just this comment; other unresolved comments
        // will be re-discovered on the next poll after this one is handled.
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [comment];
        state.CurrentCommentIndex = 0;

        // Remove from waiting list since we're handling the reply now
        state.WaitingForReplyComments.RemoveAll(c => c.Id == comment.Id);

        // Always auto-explain — skip the choice prompt
        return BeginExplainComment(state, isReplyEvent: true);
    }

    private static MonitorAction BuildCommentAction(MonitorState state, string timestamp)
    {
        var comments = state.UnresolvedComments;
        if (comments.Count == 1)
        {
            state.CommentFlow = CommentFlowState.SingleCommentPrompt;
            state.CurrentCommentIndex = 0;
            // Always auto-explain single comments — skip the choice prompt
            return BeginExplainComment(state);
        }

        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        var summary = string.Join(", ", comments.Select(c => $"{c.Author} on {c.FilePath}"));
        return new MonitorAction
        {
            Action = "ask_user",
            Question = $"[{timestamp}] 💬 PR #{state.PrNumber} has {comments.Count} unresolved comments: {summary}",
            Choices = ["Address all comments", "Explain each one by one", "Address a specific comment", "I'll handle the comments myself"],
            Context = comments
        };
    }

    private static MonitorAction ProcessCommentChoice(MonitorState state, string? choice)
    {
        return (state.CommentFlow, choice) switch
        {
            (CommentFlowState.MultiCommentPrompt, "address_all") => BeginAddressAll(state),
            (CommentFlowState.MultiCommentPrompt, "explain_all") => BeginExplainAll(state),
            (CommentFlowState.MultiCommentPrompt, "address_specific") => BeginPickComment(state),
            (CommentFlowState.MultiCommentPrompt, "handle_myself") =>
                WaitForManualHandling(state),
            (CommentFlowState.SingleCommentPrompt, "handle_myself") =>
                WaitForManualHandling(state),

            (CommentFlowState.SingleCommentPrompt, "address") => BeginAddressCurrentComment(state),
            (CommentFlowState.SingleCommentPrompt, "apply_fix") => BeginApplyRecommendation(state),
            (CommentFlowState.SingleCommentPrompt, "explain") => BeginExplainComment(state),

            // Per-comment confirmation in address-all flow
            (CommentFlowState.AddressAllIterating, "address") => EmitAddressCommentAction(state),
            (CommentFlowState.AddressAllIterating, "skip") => SkipAndAdvanceComment(state),
            (CommentFlowState.AddressAllIterating, "done") => TransitionToPolling(state),

            // Per-comment confirmation in explain-all flow
            (CommentFlowState.ExplainAllIterating, "apply_fix") => BeginApplyRecommendation(state),
            (CommentFlowState.ExplainAllIterating, "skip") => AdvanceExplainAll(state),
            (CommentFlowState.ExplainAllIterating, "done") => TransitionToPolling(state),

            (CommentFlowState.PickComment, _) => HandlePickedComment(state, choice),
            (CommentFlowState.PickRemaining, "continue") => ContinueToNextComment(state),
            (CommentFlowState.PickRemaining, "done") => TransitionToPolling(state),

            (CommentFlowState.WaitingForManualHandling, "done_handling") => TransitionToPolling(state),
            (CommentFlowState.WaitingForManualHandling, "stop") => StopMonitoring(state),

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
            Question = $"Comment (1/{state.UnresolvedComments.Count}): {c.Author} on {c.FilePath}:{c.Line}: \"{Truncate(c.Body, 300)}\"",
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
                Question = $"Next comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}): {next.Author} on {next.FilePath}:{next.Line}: \"{Truncate(next.Body, 300)}\"",
                Choices = ["Address this comment", "Skip this comment", "Done — resume monitoring"],
                Context = next
            };
        }
        return TransitionToPolling(state);
    }

    private static MonitorAction BeginExplainAll(MonitorState state)
    {
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.CurrentCommentIndex = 0;
        return EmitExplainForCurrentComment(state);
    }

    private static MonitorAction EmitExplainForCurrentComment(MonitorState state)
    {
        var c = state.UnresolvedComments[state.CurrentCommentIndex];
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.PendingExplainResult = true;
        return new MonitorAction
        {
            Action = "execute",
            Task = "explain_comment",
            Instructions = $"Read and explain this review comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}). Recommend whether to implement the change or push back. ONLY analyze THIS SPECIFIC comment — do NOT address, reply to, or fix any other comments. DO NOT make any code changes, DO NOT commit, DO NOT push, DO NOT reply to the comment thread — ONLY explain and recommend. Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}. After explaining, call pr_monitor_next_step with event=task_complete.",
            Context = c
        };
    }

    private static MonitorAction AdvanceExplainAll(MonitorState state)
    {
        state.CurrentCommentIndex++;
        if (state.CurrentCommentIndex < state.UnresolvedComments.Count)
            return EmitExplainForCurrentComment(state);
        return TransitionToPolling(state);
    }

    private static MonitorAction BeginPickComment(MonitorState state)
    {
        state.CommentFlow = CommentFlowState.PickComment;
        var choices = state.UnresolvedComments
            .Select((c, i) => $"{i + 1}. {c.Author}: {Truncate(c.Body, 120)} ({c.FilePath})")
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
            return WaitForManualHandling(state);

        // Parse the comment index from "1. author: ..."
        if (int.TryParse(choice?.Split('.').FirstOrDefault(), out int idx) && idx > 0 && idx <= state.UnresolvedComments.Count)
        {
            state.CurrentCommentIndex = idx - 1;
            state.CommentFlow = CommentFlowState.SingleCommentPrompt;
            // Always auto-explain picked comments
            return BeginExplainComment(state);
        }

        return BeginPickComment(state);
    }

    private static MonitorAction BeginAddressCurrentComment(MonitorState state)
    {
        state.CurrentState = MonitorStateId.ExecutingTask;
        return EmitAddressCommentAction(state);
    }

    private static MonitorAction BeginApplyRecommendation(MonitorState state)
    {
        if (state.CurrentCommentIndex >= state.UnresolvedComments.Count)
            return TransitionToPolling(state);

        var c = state.UnresolvedComments[state.CurrentCommentIndex];
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "apply_recommendation",
            Instructions = $"Apply the recommendation you made during your analysis of this comment. " +
                $"{ReplyDataInstruction()} " +
                $"If you recommended implementing the change, make the code changes. ONLY address THIS SPECIFIC comment — do NOT address, reply to, or fix any other comments. STOP and present your changes to the user for review before committing — use ask_user to show what you changed and ask for approval. Only commit/push after the user approves (honor user's custom instructions for git workflow). After pushing, compose a reply describing what was changed and link the commit (use `git rev-parse HEAD` to get the SHA, then format as {state.Owner}/{state.Repo}@SHA), then call pr_monitor_next_step with event=comment_addressed and data containing reply_text. " +
                $"If you recommended pushing back or disagreeing, first try to find or write a test that proves the comment is wrong. " +
                $"If you CANNOT write such a test, reconsider — the comment may be valid — implement the change instead (follow the implement path above, use event=comment_addressed). " +
                $"If you CAN write the test, check it in (present to user for approval first, commit/push after approval), compose a reply referencing your analysis and the new test (link the commit), then call pr_monitor_next_step with event=comment_replied and data containing reply_text. " +
                $"If you recommended asking a clarifying question, compose a reply referencing your analysis, then call pr_monitor_next_step with event=comment_replied and data containing reply_text. " +
                $"If you recommended agreeing with the comment but no code changes are needed, compose a reply acknowledging the comment, then call pr_monitor_next_step with event=comment_addressed and data containing reply_text. " +
                $"Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}.{CopilotFooter(state)}",
            Context = c
        };
    }

    private static MonitorAction BeginExplainComment(MonitorState state, bool isReplyEvent = false)
    {
        var c = state.UnresolvedComments[state.CurrentCommentIndex];
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.PendingExplainResult = true;
        var replyContext = isReplyEvent && !string.IsNullOrEmpty(c.LastReplyAuthor)
            ? $"Note: This is a reply from {c.LastReplyAuthor} ({(c.LastReplyAt.HasValue ? $"at {c.LastReplyAt.Value:u}, " : "")}{c.ReplyCount} replies in thread) to an existing review thread — not a brand-new comment. "
            : "";
        return new MonitorAction
        {
            Action = "execute",
            Task = "explain_comment",
            Instructions = $"Read and explain this review comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}). Recommend whether to implement the change or push back. " +
                replyContext +
                $"If you lean toward pushing back, consider what test evidence would prove the comment is wrong — a strong pushback recommendation should explain what test could validate your position. " +
                $"ONLY analyze THIS SPECIFIC comment — do NOT address, reply to, or fix any other comments. DO NOT make any code changes, DO NOT commit, DO NOT push, DO NOT reply to the comment thread — ONLY explain and recommend. Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}. After explaining, call pr_monitor_next_step with event=task_complete.",
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
            Instructions = $"Read the comment, implement the fix. ONLY address THIS SPECIFIC comment — do NOT address, reply to, or fix any other comments. STOP and present your changes to the user for review before committing — use ask_user to show what you changed and ask for approval. Only commit/push after the user approves (honor user's custom instructions for git workflow). After pushing, compose a reply describing what was changed and link the commit (use `git rev-parse HEAD` to get the SHA, then format as {state.Owner}/{state.Repo}@SHA). {ReplyDataInstruction()} Comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\". URL: {c.Url}.{CopilotFooter(state)}",
            Context = c
        };
    }

    private static MonitorAction ProcessCommentAddressed(MonitorState state, object? data)
    {
        var addressedComment = state.UnresolvedComments.Count > state.CurrentCommentIndex
            ? state.UnresolvedComments[state.CurrentCommentIndex]
            : null;
        if (addressedComment != null)
        {
            // If agent didn't provide reply text, ask for it before resolving
            if (string.IsNullOrEmpty(state.PendingReplyText))
            {
                return EmitComposeReplyAction(state, addressedComment, "comment_addressed");
            }

            addressedComment.IsAddressed = true;
            state.ActiveWaitingComment = addressedComment;
            state.PendingResolveAfterAddress = true;
            state.PendingResolveSummary = "Comment addressed";
            return BuildResolveThreadAction(state, addressedComment);
        }

        return AdvanceAfterCommentAddressed(state);
    }

    private static MonitorAction ProcessCommentReplied(MonitorState state)
    {
        // Agent replied to a comment (pushback/clarification) without code changes.
        // Auto-resolve if the reviewer is a bot (they won't reply back).
        // Track as waiting-for-reply if the reviewer is human.
        var comment = state.CurrentCommentIndex < state.UnresolvedComments.Count
            ? state.UnresolvedComments[state.CurrentCommentIndex]
            : null;

        if (comment != null)
        {
            // If agent didn't provide reply text, ask for it before proceeding
            if (string.IsNullOrEmpty(state.PendingReplyText))
            {
                return EmitComposeReplyAction(state, comment, "comment_replied");
            }

            if (PrStatusFetcher.IsBotReviewer(comment.Author))
            {
                // Bot reviewer — auto-resolve, they won't respond.
                // Mark as addressed so ShouldReRequestReview doesn't see it as "still needs action"
                // when checking whether all comments from this reviewer have been handled.
                comment.IsAddressed = true;
                state.ActiveWaitingComment = comment;
                state.PendingResolveAfterAddress = true;
                state.PendingResolveSummary = "Replied to comment";
                return BuildResolveThreadAction(state, comment);
            }

            // Human reviewer — track as waiting-for-reply, don't resolve.
            // Post the reply via auto_execute and advance after.
            if (!state.WaitingForReplyComments.Any(c => c.Id == comment.Id))
            {
                comment.IsWaitingForReply = true;
                state.WaitingForReplyComments.Add(comment);
            }

            state.ActiveWaitingComment = comment;
            state.PendingAdvanceAfterReply = "Replied to comment";
            return BuildPostReplyAction(state, comment);
        }

        return AdvanceAfterComment(state, "Replied to comment");
    }

    private static MonitorAction AdvanceAfterCommentAddressed(MonitorState state) =>
        AdvanceAfterComment(state, "Comment addressed");

    private static MonitorAction AdvanceAfterComment(MonitorState state, string summary)
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
                    Question = $"Next comment ({state.CurrentCommentIndex + 1}/{state.UnresolvedComments.Count}): {next.Author} on {next.FilePath}:{next.Line}: \"{Truncate(next.Body, 300)}\"",
                    Choices = ["Address this comment", "Skip this comment", "Done — resume monitoring"],
                    Context = next
                };
            }
            // All done
            return TransitionToPolling(state);
        }

        // Explain-all flow: after addressing a comment, advance to explain the next one
        if (state.CommentFlow == CommentFlowState.ExplainAllIterating)
            return AdvanceExplainAll(state);

        // Single comment handled — check for remaining
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
            Question = $"{summary}. {remaining.Count} more unresolved comment(s) remaining.",
            Choices = ["Address next comment", "I'll handle the rest myself"],
            Context = remaining
        };
    }

    private static MonitorAction WaitForManualHandling(MonitorState state)
    {
        state.CommentFlow = CommentFlowState.WaitingForManualHandling;
        state.CurrentState = MonitorStateId.AwaitingUser;
        return new MonitorAction
        {
            Action = "ask_user",
            Question = "Let me know when you're done handling the comment(s).",
            Choices = ["Done, continue monitoring", "Stop monitoring"]
        };
    }

    private static MonitorAction ContinueToNextComment(MonitorState state)
    {
        // Move to next unaddressed comment
        state.CurrentCommentIndex++;
        if (state.CurrentCommentIndex < state.UnresolvedComments.Count)
        {
            state.CommentFlow = CommentFlowState.SingleCommentPrompt;
            // Always auto-explain the next comment
            return BeginExplainComment(state);
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
            Question = $"[{timestamp}] ⏳ Waiting comment from {comment.Author} on {comment.FilePath}:{comment.Line}: \"{Truncate(comment.Body, 400)}\" — You replied, ball is in reviewer's court.",
            Choices = ["Resolve this thread", "Go back to monitoring"],
            Context = comment
        };
    }

    private static MonitorAction ProcessWaitingCommentChoice(MonitorState state, string? choice)
    {
        var comment = state.ActiveWaitingComment!;

        var result = choice switch
        {
            "resolve" => BuildResolveThreadAction(state, comment),
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

    private static MonitorAction BuildPostReplyAction(MonitorState state, CommentInfo comment)
    {
        DebugLogger.Log("StateMachine", $"Posting thread reply for {comment.Id}");
        return new MonitorAction
        {
            Action = "auto_execute",
            Task = "post_thread_reply",
            Message = $"Posting reply to thread {comment.Id}...",
            Context = comment
        };
    }

    /// <summary>
    /// When the agent calls comment_addressed/comment_replied without providing reply_text,
    /// ask it to compose the reply. The agent calls back with the same event + reply_text.
    /// </summary>
    private static MonitorAction EmitComposeReplyAction(MonitorState state, CommentInfo c, string completionEvent)
    {
        state.CurrentState = MonitorStateId.ExecutingTask;
        return new MonitorAction
        {
            Action = "execute",
            Task = "compose_reply",
            Instructions = $"You called {completionEvent} but did not include the reply text. " +
                $"Compose a brief reply for the review thread describing what you did to address this comment. " +
                $"If you made code changes, link the commit (use `git rev-parse HEAD` to get the SHA, format as {state.Owner}/{state.Repo}@SHA). " +
                $"{ReplyDataInstruction()} " +
                $"Then call pr_monitor_next_step with event={completionEvent} and data containing reply_text. " +
                $"Original comment from {c.Author} on {c.FilePath}:{c.Line}: \"{c.Body}\".{CopilotFooter(state)}",
            Context = c
        };
    }

    /// <summary>
    /// Check whether we should re-request a review from the given reviewer.
    /// Returns true if this was their last unresolved comment in the current batch.
    /// Checks all indices (not just later ones) to handle out-of-order addressing.
    /// </summary>
    internal static bool ShouldReRequestReview(MonitorState state, string reviewer)
    {
        return ShouldReRequestReview(reviewer, state.PrAuthor, state.CurrentUser,
            state.ReviewsReRequested, state.UnresolvedComments, state.CurrentCommentIndex);
    }

    /// <summary>
    /// Core re-request check, usable from both the comment flow and the poll loop.
    /// Returns true if a reviewer has no remaining needs-action comments and hasn't
    /// already been re-requested (checked via alreadyReRequested set).
    /// </summary>
    internal static bool ShouldReRequestReview(
        string reviewer, string prAuthor, string currentUser,
        IEnumerable<string> alreadyReRequested,
        IReadOnlyList<CommentInfo> unresolvedComments, int skipIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
            return false;

        // Don't re-request from ourselves or the PR author
        if (string.Equals(reviewer, prAuthor, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reviewer, currentUser, StringComparison.OrdinalIgnoreCase))
            return false;

        // Already re-requested (in-memory list or GitHub API requested_reviewers)
        if (alreadyReRequested.Any(r => string.Equals(r, reviewer, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Check for remaining comments from this reviewer that still need action.
        // Skip comments already addressed or replied to (IsWaitingForReply/IsAddressed).
        for (int i = 0; i < unresolvedComments.Count; i++)
        {
            if (i == skipIndex)
                continue;
            var c = unresolvedComments[i];
            if (string.Equals(c.Author, reviewer, StringComparison.OrdinalIgnoreCase) && !c.IsWaitingForReply && !c.IsAddressed)
                return false;
        }

        return true;
    }

    private static MonitorAction BuildReRequestReviewAction(MonitorState state, string reviewer)
    {
        DebugLogger.Log("StateMachine", $"Re-requesting review from {reviewer}");
        return new MonitorAction
        {
            Action = "auto_execute",
            Task = "request_review",
            Message = $"Re-requesting review from {reviewer}..."
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
            Choices = ["Investigate the failures", "Re-run failed jobs", "I'll handle it myself"],
            Context = new { state.FailedChecks, state.Checks }
        };
    }

    private static MonitorAction ProcessCiFailureChoice(MonitorState state, string? choice)
    {
        return (state.CiFailureFlow, choice) switch
        {
            (CiFailureFlowState.CiFailurePrompt, "investigate") => BeginInvestigation(state),
            (CiFailureFlowState.CiFailurePrompt, "rerun") => BuildRerunAction(state),
            (CiFailureFlowState.CiFailurePrompt, "handle_myself") => StopMonitoring(state),

            (CiFailureFlowState.InvestigationResults, "apply_fix") => BeginApplyFix(state),
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
                "- data.findings: your analysis of what went wrong (include links to the relevant log pages you found)\n" +
                "- data.suggested_fix: (optional) a suggested code fix if applicable\n" +
                "- data.issue_type: set to \"duplicate_artifact\" if the failure is due to artifacts already existing from a previous run attempt, otherwise set to \"code\" for code/test failures or \"unknown\"\n" +
                "IMPORTANT: Ignoring the failure is NEVER an option — even if you think the failure is infrastructure-related, you must suggest a resolution (rerun, code fix, config change, etc.).",
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
                choices.Add("Apply the recommendation");
            choices.Add("Re-run failed jobs");
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

    private static MonitorAction BuildRerunAction(MonitorState state)
    {
        // If other checks are still running, defer the rerun until they complete
        // (Ignore legacy pending statuses — they stay pending until merge)
        if (state.Checks.InProgress > 0 || state.Checks.Queued > 0)
        {
            DebugLogger.Log("StateMachine", $"Deferring rerun — {state.Checks.InProgress} in-progress, {state.Checks.Queued} queued checks remaining");
            state.PendingRerunWhenChecksComplete = true;
            state.CurrentState = MonitorStateId.Polling;
            state.CommentFlow = CommentFlowState.None;
            state.CiFailureFlow = CiFailureFlowState.None;
            state.ActiveWaitingComment = null;
            return new MonitorAction { Action = "polling", Message = "Waiting for remaining checks to complete before rerunning failed jobs..." };
        }

        // Use Playwright (via the agent) to navigate to ADO and click "Rerun failed jobs".
        // Azure DevOps has no API for rerun-failed-only; only the web UI supports it.
        state.PendingRerunWhenChecksComplete = false;
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
        var question = $"[{timestamp}] ✅ PR #{state.PrNumber} is approved by {approvers} and CI is green! ({state.Checks.Passed}/{state.Checks.Total} passed)";

        // Warn if repo requires conversation resolution and there are still unresolved threads
        if (state.RequiresConversationResolution && state.WaitingForReplyComments.Count > 0)
        {
            var waitingAuthors = string.Join(", ", state.WaitingForReplyComments.Select(c => c.Author).Distinct());
            question += $" ⚠️ {state.WaitingForReplyComments.Count} unresolved conversation(s) from {waitingAuthors} may block merging (repo policy).";
        }

        return new MonitorAction
        {
            Action = "ask_user",
            Question = question,
            Choices = ["Merge the PR", "Wait for another approver", "Resume monitoring", "I'll handle it myself"]
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
    internal static string CopilotFooter(MonitorState state) =>
        $" IMPORTANT: Append this footer to the END of every comment reply you post (after a blank line and ---): '\\n---\\n🤖 {state.CurrentUser}-copilot'";

    /// <summary>
    /// Instruction telling the agent to pass reply text in data instead of posting it directly.
    /// The server posts the reply deterministically via the correct REST API endpoint.
    /// </summary>
    internal static string ReplyDataInstruction() =>
        "Do NOT post the reply yourself (do NOT use `gh api`, `gh pr comment`, or any other method to post comments). " +
        "Instead, pass your reply text in the data parameter as JSON: data='{\"reply_text\": \"your reply here\"}'. " +
        "The server will post it to the correct review thread automatically.";
}
