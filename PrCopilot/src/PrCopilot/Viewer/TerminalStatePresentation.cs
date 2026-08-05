// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Viewer;

internal enum TerminalStateColor
{
    Success,
    Error,
    Warning,
    Comment,
    Stopped
}

internal static class TerminalStatePresentation
{
    public static string GetViewerState(TerminalStateType? state) => state switch
    {
        TerminalStateType.NewComment => "new_comment",
        TerminalStateType.MergeConflict => "merge_conflict",
        TerminalStateType.CiFailure => "ci_failure",
        TerminalStateType.CiCancelled => "ci_cancelled",
        TerminalStateType.ApprovedCiGreen => "approved_and_ci_green",
        TerminalStateType.StaleApprovalCiGreen => "stale_approval_ci_green",
        _ => "unknown"
    };

    public static string GetEmoji(string state) => state switch
    {
        "approved" or "approved_and_ci_green" or "ci_passed_comments_pending" => "✅",
        "stale_approval_ci_green" => "🔄",
        "ci_failure" => "❌",
        "ci_cancelled" => "🚫",
        "unresolved_comments" or "new_comment" => "💬",
        "merge_conflict" => "⚠️",
        "stopped" => "⏹️",
        _ => "⚡"
    };

    public static TerminalStateColor GetColor(string state) => state switch
    {
        "approved" or "approved_and_ci_green" or "ci_passed_comments_pending" => TerminalStateColor.Success,
        "ci_failure" or "merge_conflict" => TerminalStateColor.Error,
        "unresolved_comments" or "new_comment" => TerminalStateColor.Comment,
        "stopped" => TerminalStateColor.Stopped,
        _ => TerminalStateColor.Warning
    };
}
