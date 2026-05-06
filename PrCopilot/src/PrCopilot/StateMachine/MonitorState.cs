// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Complete state for a monitored PR. All mutable state lives here.
/// The state machine reads and updates this; the MCP tools serialize it for responses.
/// </summary>
public class MonitorState
{
    // PR identity
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public int PrNumber { get; set; }
    public string PrTitle { get; set; } = "";
    public string PrBody { get; set; } = "";
    public string PrUrl { get; set; } = "";
    public string PrAuthor { get; set; } = "";

    /// <summary>The GitHub username of the person running this tool (from gh api user).</summary>
    public string CurrentUser { get; set; } = "";
    public string HeadSha { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string SessionFolder { get; set; } = "";

    // File paths (derived from session folder + owner/repo/PR number to avoid collisions)
    private string FilePrefix => $"pr-monitor-{Owner}-{Repo}-{PrNumber}";
    public string LogFile => Path.Combine(SessionFolder, $"{FilePrefix}.log");
    public string TriggerFile => Path.Combine(SessionFolder, $"{FilePrefix}.trigger");
    public string DebugLogFile => Path.Combine(SessionFolder, $"{FilePrefix}.debug.log");

    // State machine
    public MonitorStateId CurrentState { get; set; } = MonitorStateId.Idle;
    public CommentFlowState CommentFlow { get; set; } = CommentFlowState.None;
    public CiFailureFlowState CiFailureFlow { get; set; } = CiFailureFlowState.None;
    public TerminalStateType? LastTerminalState { get; set; }

    // CI check status
    public CheckRunCounts Checks { get; set; } = new();

    // Reviews
    public List<ReviewInfo> Approvals { get; set; } = [];
    public List<ReviewInfo> StaleApprovals { get; set; } = [];
    public bool HasMergeConflict { get; set; }

    // Comments
    public List<CommentInfo> UnresolvedComments { get; set; } = [];
    public List<CommentInfo> WaitingForReplyComments { get; set; } = [];
    public CommentInfo? ActiveWaitingComment { get; set; }
    public int CurrentCommentIndex { get; set; }

    // CI failures (populated when terminal state is CiFailure)
    public List<FailedCheckInfo> FailedChecks { get; set; } = [];

    // Investigation results (populated by LLM after investigation)
    public string? InvestigationFindings { get; set; }
    public string? SuggestedFix { get; set; }
    public string? IssueType { get; set; }

    // Polling
    public DateTime? LastPollTime { get; set; }
    public int PollCount { get; set; }

    // After-hours: null = no extension, otherwise monitoring extended until this time
    public DateTime? AfterHoursExtendedUntil { get; set; }

    // Set when merge fails due to branch policy — requires more approvals before merge terminal state fires again
    public bool NeedsAdditionalApproval { get; set; }
    public int ApprovalCountAtMergeFailure { get; set; }

    // Set when auto-resolving a thread after addressing a comment
    public bool PendingResolveAfterAddress { get; set; }

    // Re-request review tracking: reviewer login to re-request after current auto_execute completes
    public string? PendingReRequestReviewer { get; set; }

    // Reviewers already re-requested during this comment flow (prevents duplicates)
    public List<string> ReviewsReRequested { get; set; } = [];

    // Summary message for the post-resolve transition (e.g., "Comment addressed" vs "Replied to comment")
    public string? PendingResolveSummary { get; set; }

    // Branch protection: repo requires all review conversations to be resolved before merging
    public bool RequiresConversationResolution { get; set; }

    // Set when explain task completes — show post-explain choices instead of original prompt
    public bool PendingExplainResult { get; set; }

    /// <summary>Last recommendation text from the agent's explain_comment analysis, shown in post-explain elicitations.</summary>
    public string? LastRecommendation { get; set; }

    // Set when user chooses rerun but other checks are still pending/queued — defer until complete
    public bool PendingRerunWhenChecksComplete { get; set; }

    /// <summary>Reply text composed by the agent, to be posted by the server via the REST API.</summary>
    public string? PendingReplyText { get; set; }

    /// <summary>
    /// HEAD SHA snapshotted when the state most recently entered <see cref="MonitorStateId.ExecutingTask"/>.
    /// Used by the recovery path for <c>(ExecutingTask, "ready")</c> to detect whether the agent
    /// pushed during the task — if HEAD has advanced, "ready" is reinterpreted as a completion event
    /// (<c>comment_addressed</c> in comment flows, <c>push_completed</c> in CI flows).
    /// Set via <see cref="EnterExecutingTask"/>; null/empty means no snapshot was captured.
    /// </summary>
    public string? HeadShaAtTaskStart { get; set; }

    /// <summary>Transient: completion event set by sampling handler for MonitorFlowTools to feed back to state machine.</summary>
    public string? SamplingCompletionEvent { get; set; }
    /// <summary>Transient: completion event set by EmitComposeReplyAction for the sampling compose_reply handler.</summary>
    public string? PendingCompletionEvent { get; set; }

    /// <summary>
    /// Expected completion event for the currently-executing task, when known unambiguously.
    /// Read by the (ExecutingTask, "ready") recovery path: when HEAD has advanced and this
    /// value matches a single completion event (e.g., "comment_addressed", "push_completed"),
    /// the recovery dispatches that event automatically. When null (ambiguous — e.g., the
    /// apply_recommendation task can complete as either "comment_addressed" for an implementation
    /// push OR "comment_replied" for a proving-test push), the recovery falls back to the
    /// flow-aware ask_user prompt so the user disambiguates instead of the engine guessing.
    ///
    /// Reset to null on every <see cref="EnterExecutingTask"/> call; emitters that know
    /// their task's unambiguous completion event must set this explicitly after that call.
    /// </summary>
    public string? ExecutingTaskExpectedCompletion { get; set; }

    /// <summary>
    /// When set, ProcessTaskComplete calls AdvanceAfterComment with this summary.
    /// Used after posting a thread reply (auto_execute) when there's no subsequent resolve step.
    /// </summary>
    public string? PendingAdvanceAfterReply { get; set; }

    /// <summary>
    /// Clears all pending comment-flow state in one call. Used by TransitionToPolling
    /// and error paths that bail to ask_user to ensure no stale flags leak across flows.
    /// </summary>
    public void ClearPendingCommentState()
    {
        PendingReplyText = null;
        PendingResolveAfterAddress = false;
        PendingResolveSummary = null;
        PendingAdvanceAfterReply = null;
        PendingExplainResult = false;
        LastRecommendation = null;
        ActiveWaitingComment = null;
        PendingReRequestReviewer = null;
        SamplingCompletionEvent = null;
        PendingCompletionEvent = null;
    }

    /// <summary>
    /// Comment that received a reviewer reply (detected during polling).
    /// Set when ReviewerReplied terminal state is detected.
    /// </summary>
    public CommentInfo? RepliedComment { get; set; }

    /// <summary>
    /// Transition to <see cref="MonitorStateId.ExecutingTask"/> and snapshot the current
    /// <see cref="HeadSha"/> as <see cref="HeadShaAtTaskStart"/>. The snapshot lets the
    /// recovery path detect post-push resumes (where the agent pushed during the task and
    /// then re-entered via the post-push <c>pr_monitor_start</c> hook with event=ready
    /// instead of calling the documented completion event).
    ///
    /// Use this instead of assigning <c>CurrentState = MonitorStateId.ExecutingTask</c>
    /// directly so the snapshot is never forgotten at a new task-entry site.
    /// </summary>
    public void EnterExecutingTask()
    {
        CurrentState = MonitorStateId.ExecutingTask;
        SnapshotForRecovery();
    }

    /// <summary>
    /// Transition to <see cref="MonitorStateId.ApplyingFix"/> and snapshot HEAD for
    /// post-push recovery. The CI fix flow's apply_fix task tells the agent to push
    /// before reporting push_completed; if a post-push hook fires event=ready instead,
    /// the (ApplyingFix, "ready") recovery uses this snapshot to detect that the push
    /// happened and re-dispatch as push_completed (rather than wiping CiFailureFlow).
    /// </summary>
    public void EnterApplyingFix()
    {
        CurrentState = MonitorStateId.ApplyingFix;
        SnapshotForRecovery();
    }

    private void SnapshotForRecovery()
    {
        HeadShaAtTaskStart = HeadSha;
        // Reset expected completion to "ambiguous" on every entry. Emitters that know their
        // task's single completion event must set this AFTER calling EnterExecutingTask.
        ExecutingTaskExpectedCompletion = null;
    }
}
