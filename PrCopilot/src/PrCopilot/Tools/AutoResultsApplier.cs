// Licensed under the MIT License.

using System.Text.RegularExpressions;
using PrCopilot.Services;
using PrCopilot.StateMachine;

namespace PrCopilot.Tools;

/// <summary>
/// Applies a validated auto-mode batch: verifies the agent's claims against GitHub, posts replies, resolves
/// threads, and re-requests review. A result that fails verification is handed to the user without being posted.
/// </summary>
internal static class AutoResultsApplier
{
    // A just-pushed fix can take a moment to appear in the PR's commit list.
    private static readonly TimeSpan DefaultCommitVisibilityDelay = TimeSpan.FromSeconds(5);

    public static async Task ApplyAsync(MonitorState state, TimeSpan? commitVisibilityDelay = null, CancellationToken cancellationToken = default)
    {
        var results = state.PendingAutoResults;
        await VerifyCommitsAsync(state, results, commitVisibilityDelay ?? DefaultCommitVisibilityDelay, cancellationToken);
        await VerifyFollowUpIssuesAsync(state, results);

        foreach (var result in results)
        {
            if (result.Decision == AutoCommentResult.NeedsUser || result.Comment is not { } comment)
                continue;

            // Checked before each write instead of passed to gh, because killing gh can't undo a write already in flight.
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MonitorFlowTools.PostReplyAsync(state, comment, BuildReply(state, result)))
            {
                result.HandToUser("auto mode could not post its reply");
                continue;
            }

            DebugLogger.Log("AutoMode", $"Posted {result.Decision} reply to {comment.Author} on {comment.FilePath}:{comment.Line} ({comment.Url})");
            state.AutoReplyCounts[comment.Id] = state.AutoReplyCounts.GetValueOrDefault(comment.Id) + 1;
            if (result.Decision is AutoCommentResult.Fixed or AutoCommentResult.FollowUp || PrStatusFetcher.IsBotReviewer(comment.Author))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (resolved, output) = await GitHubCliExecutor.ResolveThreadAsync(comment.Id);
                if (!resolved)
                    DebugLogger.Error("AutoMode", $"Replied but could not resolve thread {comment.Id}: {output}");

                // Each poll retries resolving bot threads we replied to; nothing retries a human reviewer's thread.
                if (resolved || PrStatusFetcher.IsBotReviewer(comment.Author))
                    comment.IsAddressed = true;
                else
                    result.HandToUser("auto mode already posted its reply but could not resolve the thread, so resolve it on GitHub (no further reply is needed)");
            }
            else
            {
                comment.IsWaitingForReply = true;
                if (!state.WaitingForReplyComments.Any(c => c.Id == comment.Id))
                    state.WaitingForReplyComments.Add(comment);
            }
        }

        await ReRequestReviewsAsync(state, results, cancellationToken);
    }

    internal static string BuildReply(MonitorState state, AutoCommentResult result)
    {
        var reply = (result.ReplyText ?? "").TrimEnd();
        if (result.Decision == AutoCommentResult.FollowUp && result.IssueUrl is { } issueUrl && !LinksIssue(state, reply, issueUrl))
            reply += $"\n\nFollow-up: {issueUrl}";

        var footer = $"🤖 {state.CurrentUser}-copilot";
        if (!string.IsNullOrEmpty(state.CurrentUser) && !reply.Contains(footer, StringComparison.Ordinal))
            reply += $"\n\n---\n{footer}";
        return reply;
    }

    private static bool LinksIssue(MonitorState state, string reply, string issueUrl)
    {
        if (reply.Contains(issueUrl, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!AutoCommentResult.TryParseIssueReference(issueUrl, state.Owner, state.Repo, out var owner, out var repo, out var number))
            return false;

        // GitHub links a bare #N to the PR's own repository, so an issue anywhere else needs owner/repo#N.
        var qualifier = $"{Regex.Escape(owner)}/{Regex.Escape(repo)}";
        var sameRepo = string.Equals(owner, state.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(repo, state.Repo, StringComparison.OrdinalIgnoreCase);
        var prefix = sameRepo ? $"(?:{qualifier})?" : qualifier;
        return Regex.IsMatch(reply, $@"(?<![\w/.-]){prefix}#{number}\b", RegexOptions.IgnoreCase);
    }

    private static async Task VerifyCommitsAsync(MonitorState state, List<AutoCommentResult> results, TimeSpan commitVisibilityDelay, CancellationToken cancellationToken)
    {
        var claims = results
            .Where(r => r.Decision == AutoCommentResult.Fixed ||
                (r.Decision == AutoCommentResult.PushedBack && !string.IsNullOrWhiteSpace(r.Commit)))
            .ToList();
        if (claims.Count == 0)
            return;

        var prCommits = await TryFetchPrCommitHashesAsync(state);
        if ((prCommits == null || claims.Any(r => !IsOnPr(prCommits, r.Commit))) && commitVisibilityDelay > TimeSpan.Zero)
        {
            await Task.Delay(commitVisibilityDelay, cancellationToken);
            prCommits = await TryFetchPrCommitHashesAsync(state) ?? prCommits;
        }

        foreach (var result in claims)
        {
            if (prCommits == null)
                result.HandToUser("auto mode could not read the PR's commits to verify its change");
            else if (!IsOnPr(prCommits, result.Commit))
                result.HandToUser($"commit {result.Commit} is not on the PR branch, so the change could not be verified as pushed");
        }
    }

    private static bool IsOnPr(List<string> prCommits, string? commit) =>
        AutoCommentResult.NormalizeCommit(commit) is { } sha &&
        prCommits.Any(c => c.StartsWith(sha, StringComparison.OrdinalIgnoreCase));

    private static async Task<List<string>?> TryFetchPrCommitHashesAsync(MonitorState state)
    {
        try
        {
            return await PrStatusFetcher.FetchPrCommitHashesAsync(state.Owner, state.Repo, state.PrNumber);
        }
        catch (Exception ex)
        {
            DebugLogger.Error("AutoMode", $"Could not read PR commits: {ex.Message}");
            return null;
        }
    }

    private static async Task VerifyFollowUpIssuesAsync(MonitorState state, List<AutoCommentResult> results)
    {
        foreach (var result in results.Where(r => r.Decision == AutoCommentResult.FollowUp))
        {
            if (!AutoCommentResult.TryParseIssueReference(result.IssueUrl, state.Owner, state.Repo, out var owner, out var repo, out var number))
            {
                result.HandToUser($"the follow-up link '{result.IssueUrl}' is not a GitHub issue reference");
                continue;
            }

            if (!await PrStatusFetcher.IssueExistsAsync(owner, repo, number))
            {
                result.HandToUser($"follow-up issue {owner}/{repo}#{number} could not be found");
                continue;
            }

            var host = Uri.TryCreate(state.PrUrl, UriKind.Absolute, out var prUri) ? prUri.GetLeftPart(UriPartial.Authority) : "https://github.com";
            result.IssueUrl = $"{host}/{owner}/{repo}/issues/{number}";
        }
    }

    private static async Task ReRequestReviewsAsync(MonitorState state, List<AutoCommentResult> results, CancellationToken cancellationToken)
    {
        // Bot reviewers are skipped: re-requesting them in auto mode can loop review -> fixes -> review indefinitely.
        var reviewers = results
            .Where(r => r.Decision != AutoCommentResult.NeedsUser && r.Comment != null && !PrStatusFetcher.IsBotReviewer(r.Comment.Author))
            .Select(r => r.Comment!.Author)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (reviewers.Count == 0)
            return;

        var requested = await PrStatusFetcher.FetchRequestedReviewersAsync(state.Owner, state.Repo, state.PrNumber);
        if (requested is null)
            return;

        foreach (var reviewer in reviewers)
        {
            if (!MonitorTransitions.ShouldReRequestReview(reviewer, state.PrAuthor, state.CurrentUser,
                    requested.Concat(state.ReviewsReRequested), state.UnresolvedComments))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var (success, output) = await GitHubCliExecutor.RequestReviewAsync(state.Owner, state.Repo, state.PrNumber, reviewer);
            if (success)
                state.ReviewsReRequested.Add(reviewer);
            else
                DebugLogger.Log("AutoMode", $"Could not re-request review from {reviewer} (non-critical): {output}");
        }
    }
}
