// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

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
    /// <summary>True if this comment has been handled (code fix pushed, or replied to and auto-resolved for bot reviewers).
    /// Used by ShouldReRequestReview to skip already-handled comments when deciding if a reviewer's last comment has been resolved.</summary>
    public bool IsAddressed { get; set; }
    public string LastReplyAuthor { get; set; } = "";
    /// <summary>When the last reply was posted (UTC). Used to compare against approval timestamp.</summary>
    public DateTime? LastReplyAt { get; set; }
    public int ReplyCount { get; set; }
}
