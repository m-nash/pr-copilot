// Licensed under the MIT License.

using System.Text.Json.Serialization;

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
    public string HeadSha { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string SessionFolder { get; set; } = "";

    // File paths (derived from session folder + PR number)
    public string LogFile => Path.Combine(SessionFolder, $"pr-monitor-{PrNumber}.log");
    public string TriggerFile => Path.Combine(SessionFolder, $"pr-monitor-{PrNumber}.trigger");
    public string DebugLogFile => Path.Combine(SessionFolder, $"pr-monitor-{PrNumber}.debug.log");
    public string IgnoreFile => Path.Combine(SessionFolder, $"pr-monitor-{PrNumber}.ignore-comments");

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
    public List<string> IgnoredCommentIds { get; set; } = [];
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
}

/// <summary>
/// Aggregated check run counts matching the GitHub PR UI.
/// </summary>
public class CheckRunCounts
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int Queued { get; set; }
    public int Cancelled { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// A PR review (approval, changes requested, etc.)
/// </summary>
public class ReviewInfo
{
    public string Author { get; set; } = "";
    public string State { get; set; } = ""; // APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED
    public DateTime SubmittedAt { get; set; }
    public bool IsStale { get; set; }
}

/// <summary>
/// An unresolved review comment thread.
/// </summary>
public class CommentInfo
{
    public string Id { get; set; } = "";
    public string Author { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int? Line { get; set; }
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsResolved { get; set; }
    /// <summary>True if the PR author is the last replier — ball is in reviewer's court.</summary>
    public bool IsWaitingForReply { get; set; }
    public string LastReplyAuthor { get; set; } = "";
    public int ReplyCount { get; set; }
}

/// <summary>
/// A failed check run with details for investigation.
/// </summary>
public class FailedCheckInfo
{
    public string Name { get; set; } = "";
    public string Conclusion { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Url { get; set; } = "";
    public string? ExternalId { get; set; }
}

/// <summary>
/// Action payload returned by the state machine to the MCP tools.
/// The MCP tool serializes this as the tool response. The agent interprets it.
/// </summary>
public class MonitorAction
{
    /// <summary>
    /// What the agent should do: "ask_user", "execute", "stop"
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>For ask_user: the question text.</summary>
    [JsonPropertyName("question")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Question { get; set; }

    /// <summary>For ask_user: the choices.</summary>
    [JsonPropertyName("choices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Choices { get; set; }

    /// <summary>For execute: the task type (address_comment, investigate_ci_failure, etc.)</summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Task { get; set; }

    /// <summary>For execute: instructions for the LLM.</summary>
    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    /// <summary>For stop/info: a message.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>Context data (comment details, failure details, etc.)</summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Context { get; set; }
}
