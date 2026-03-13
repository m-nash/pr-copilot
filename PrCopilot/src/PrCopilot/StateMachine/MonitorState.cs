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

    // Set when user chooses rerun but other checks are still pending/queued — defer until complete
    public bool PendingRerunWhenChecksComplete { get; set; }

    /// <summary>
    /// Comment that received a reviewer reply (detected during polling).
    /// Set when ReviewerReplied terminal state is detected.
    /// </summary>
    public CommentInfo? RepliedComment { get; set; }
}
