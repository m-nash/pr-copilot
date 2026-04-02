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

    /// <summary>Transient: completion event set by sampling handler for MonitorFlowTools to feed back to state machine.</summary>
    public string? SamplingCompletionEvent { get; set; }
    /// <summary>Transient: completion event set by EmitComposeReplyAction for the sampling compose_reply handler.</summary>
    public string? PendingCompletionEvent { get; set; }

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
}
