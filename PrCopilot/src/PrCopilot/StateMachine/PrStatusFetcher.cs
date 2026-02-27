// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;

namespace PrCopilot.StateMachine;

/// <summary>
/// Fetches PR status data via the gh CLI. All GitHub API interactions
/// go through this class — no LLM involvement in data fetching.
/// </summary>
public static class PrStatusFetcher
{
    // Check run names to filter out (pipeline internal steps)
    private static readonly HashSet<string> FilteredCheckNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Prepare",
        "Cleanup artifacts",
        "Upload results",
        "Agent",
        "Handle pull_request_target opened event with azure login"
    };

    // CI/infrastructure bots to filter from reviews and comments
    private static readonly HashSet<string> CiBots = new(StringComparer.OrdinalIgnoreCase)
    {
        "azure-sdk[bot]",
        "github-actions[bot]",
        "azure-pipelines[bot]",
        "codecov[bot]",
        "dependabot[bot]",
        "azure-sdk"
    };

    /// <summary>
    /// Fetches the authenticated GitHub user's login name.
    /// </summary>
    public static async Task<string> FetchCurrentUserAsync()
    {
        var login = await RunGhAsync("api user --jq .login");
        return login.Trim();
    }

    /// <summary>
    /// Fetches basic PR info: title, head SHA, URL, mergeable state.
    /// </summary>
    public static async Task<PrInfo> FetchPrInfoAsync(string owner, string repo, int prNumber)
    {
        var json = await RunGhAsync(
            $"api repos/{owner}/{repo}/pulls/{prNumber} --jq \"{{title: .title, sha: .head.sha, head_branch: .head.ref, url: .html_url, author: .user.login, mergeable: .mergeable, mergeable_state: .mergeable_state, state: .state, merged: .merged}}\"");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new PrInfo
        {
            Title = root.GetProperty("title").GetString() ?? "",
            HeadSha = root.GetProperty("sha").GetString() ?? "",
            HeadBranch = root.TryGetProperty("head_branch", out var hb) ? hb.GetString() ?? "" : "",
            Url = root.GetProperty("url").GetString() ?? "",
            Author = root.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
            Mergeable = root.TryGetProperty("mergeable", out var m) && m.ValueKind == JsonValueKind.True,
            MergeableState = root.TryGetProperty("mergeable_state", out var ms) ? ms.GetString() ?? "" : "",
            IsMerged = root.TryGetProperty("merged", out var mg) && mg.ValueKind == JsonValueKind.True,
            State = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : ""
        };
    }

    /// <summary>
    /// Fetches check runs with filtering, dedup, and legacy status merge.
    /// Matches the exact GitHub PR UI counts.
    /// </summary>
    public static async Task<CheckRunResult> FetchCheckRunsAsync(string owner, string repo, string headSha)
    {
        // Fetch check runs (per_page=100 — API defaults to 30)
        var checkRunsJson = await RunGhAsync(
            $"api \"repos/{owner}/{repo}/commits/{headSha}/check-runs?per_page=100\"");

        using var checkDoc = JsonDocument.Parse(checkRunsJson);
        var checkRuns = checkDoc.RootElement.GetProperty("check_runs");

        // Filter and dedup by name
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new CheckRunCounts();
        var failures = new List<FailedCheckInfo>();

        foreach (var cr in checkRuns.EnumerateArray())
        {
            var name = cr.GetProperty("name").GetString() ?? "";

            // Filter out pipeline internal steps
            if (FilteredCheckNames.Contains(name))
                continue;

            // Dedup by name (first occurrence wins — matches unique_by behavior)
            if (!seen.Add(name))
                continue;

            var status = cr.GetProperty("status").GetString() ?? "";
            var conclusion = cr.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

            ClassifyCheckRun(status, conclusion, counts);

            if (conclusion is "failure" or "timed_out")
            {
                var outputTitle = cr.TryGetProperty("output", out var output) &&
                                  output.TryGetProperty("title", out var t) &&
                                  t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "" : "";
                var externalId = cr.TryGetProperty("external_id", out var eid) && eid.ValueKind == JsonValueKind.String
                    ? eid.GetString() : null;

                failures.Add(new FailedCheckInfo
                {
                    Name = name,
                    Conclusion = conclusion,
                    Reason = outputTitle,
                    Url = cr.TryGetProperty("details_url", out var u) ? u.GetString() ?? "" : "",
                    ExternalId = externalId
                });
            }
        }

        counts.Total = seen.Count;

        // Fetch legacy commit statuses and merge
        var legacyJson = await RunGhAsync(
            $"api \"repos/{owner}/{repo}/commits/{headSha}/status\"");

        using var legacyDoc = JsonDocument.Parse(legacyJson);
        var statuses = legacyDoc.RootElement.GetProperty("statuses");

        int legacyPending = 0, legacySuccess = 0, legacyFailure = 0;
        foreach (var s in statuses.EnumerateArray())
        {
            var state = s.GetProperty("state").GetString() ?? "";
            switch (state)
            {
                case "pending": legacyPending++; break;
                case "success": legacySuccess++; break;
                case "failure" or "error": legacyFailure++; break;
            }
        }

        counts.Passed += legacySuccess;
        counts.Failed += legacyFailure;
        counts.Pending += legacyPending;
        counts.Total += legacyPending + legacySuccess + legacyFailure;

        return new CheckRunResult { Counts = counts, Failures = failures };
    }

    /// <summary>
    /// Fetches reviews and classifies them as current approvals, stale approvals, etc.
    /// Implements the exact step-by-step algorithm from SKILL.md.
    /// </summary>
    public static async Task<ReviewResult> FetchReviewsAsync(string owner, string repo, int prNumber, string headSha)
    {
        var json = await RunGhAsync(
            $"api \"repos/{owner}/{repo}/pulls/{prNumber}/reviews?per_page=100\" --jq \"[.[] | {{user: .user.login, state: .state, commit_id: .commit_id}}]\"");

        using var doc = JsonDocument.Parse(json);
        var reviews = doc.RootElement;

        // De-duplicate by user: latest review wins
        var latestByUser = new Dictionary<string, (string state, string commitId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reviews.EnumerateArray())
        {
            var user = r.GetProperty("user").GetString() ?? "";
            var state = r.GetProperty("state").GetString() ?? "";
            var commitId = r.GetProperty("commit_id").GetString() ?? "";

            // Skip CI bots (but NOT copilot-pull-request-reviewer[bot])
            if (IsCiBot(user))
                continue;

            // Latest review wins (array is chronological)
            latestByUser[user] = (state, commitId);
        }

        var approvals = new List<ReviewInfo>();
        var staleApprovals = new List<ReviewInfo>();

        foreach (var (user, (state, commitId)) in latestByUser)
        {
            if (state != "APPROVED")
                continue;

            var review = new ReviewInfo { Author = user, State = state };

            if (string.Equals(commitId, headSha, StringComparison.OrdinalIgnoreCase))
            {
                approvals.Add(review);
            }
            else
            {
                review.IsStale = true;
                staleApprovals.Add(review);
            }
        }

        return new ReviewResult { Approvals = approvals, StaleApprovals = staleApprovals };
    }

    /// <summary>
    /// Fetches unresolved review comment threads via GraphQL.
    /// Classifies threads as "needs action" vs "waiting for reply" based on last commenter.
    /// </summary>
    public static async Task<List<CommentInfo>> FetchUnresolvedCommentsAsync(string owner, string repo, int prNumber, string prAuthor = "")
    {
        // Use GraphQL to get review threads with resolution status
        // Fetch first comment (original) and last comment (to check who replied last)
        var query = $$"""
            query {
              repository(owner: \"{{owner}}\", name: \"{{repo}}\") {
                pullRequest(number: {{prNumber}}) {
                  reviewThreads(first: 100) {
                    nodes {
                      id
                      isResolved
                      isOutdated
                      comments(first: 1) {
                        totalCount
                        nodes {
                          author { login }
                          body
                          url
                          path
                          line
                        }
                      }
                      lastReply: comments(last: 1) {
                        nodes {
                          author { login }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var json = await RunGhAsync($"api graphql -f query=\"{query.Replace("\n", " ")}\"");

        using var doc = JsonDocument.Parse(json);
        var threads = doc.RootElement
            .GetProperty("data")
            .GetProperty("repository")
            .GetProperty("pullRequest")
            .GetProperty("reviewThreads")
            .GetProperty("nodes");

        var comments = new List<CommentInfo>();
        foreach (var thread in threads.EnumerateArray())
        {
            var isResolved = thread.GetProperty("isResolved").GetBoolean();
            if (isResolved)
                continue;

            var firstComments = thread.GetProperty("comments");
            var nodes = firstComments.GetProperty("nodes");
            if (nodes.GetArrayLength() == 0)
                continue;

            var totalCount = firstComments.GetProperty("totalCount").GetInt32();
            var firstComment = nodes[0];
            var author = firstComment.GetProperty("author").GetProperty("login").GetString() ?? "";

            // Skip CI bot comments (but NOT copilot-pull-request-reviewer[bot])
            if (IsCiBot(author))
                continue;

            // Determine if PR author is the last replier
            var lastReplyAuthor = "";
            var isWaitingForReply = false;
            if (thread.TryGetProperty("lastReply", out var lastReply))
            {
                var lastNodes = lastReply.GetProperty("nodes");
                if (lastNodes.GetArrayLength() > 0)
                {
                    lastReplyAuthor = lastNodes[0].GetProperty("author").GetProperty("login").GetString() ?? "";
                    // If PR author is the last commenter and there are replies (totalCount > 1),
                    // the ball is in the reviewer's court
                    if (totalCount > 1 && !string.IsNullOrEmpty(prAuthor) &&
                        string.Equals(lastReplyAuthor, prAuthor, StringComparison.OrdinalIgnoreCase))
                    {
                        isWaitingForReply = true;
                    }
                }
            }

            comments.Add(new CommentInfo
            {
                Id = thread.GetProperty("id").GetString() ?? "",
                Author = author,
                Body = firstComment.GetProperty("body").GetString() ?? "",
                Url = firstComment.GetProperty("url").GetString() ?? "",
                FilePath = firstComment.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "" : "",
                Line = firstComment.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number
                    ? l.GetInt32() : null,
                IsResolved = false,
                IsWaitingForReply = isWaitingForReply,
                LastReplyAuthor = lastReplyAuthor,
                ReplyCount = totalCount - 1
            });
        }

        return comments;
    }

    /// <summary>
    /// Resolves a review thread via GraphQL mutation.
    /// Returns true if successfully resolved, false otherwise.
    /// Retries once on failure.
    /// </summary>
    public static async Task<bool> ResolveThreadAsync(string threadId)
    {
        var mutation = $"mutation {{ resolveReviewThread(input: {{threadId: \\\"{threadId}\\\"}}) {{ thread {{ isResolved }} }} }}";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var json = await RunGhAsync($"api graphql -f query=\"{mutation}\"");
                using var doc = JsonDocument.Parse(json);
                var isResolved = doc.RootElement
                    .GetProperty("data")
                    .GetProperty("resolveReviewThread")
                    .GetProperty("thread")
                    .GetProperty("isResolved")
                    .GetBoolean();

                if (isResolved)
                    return true;
            }
            catch
            {
                if (attempt == 1) return false;
                await Task.Delay(1000);
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the given username is a CI/infrastructure bot that should be filtered.
    /// copilot-pull-request-reviewer[bot] is NOT a CI bot — treat it as human.
    /// </summary>
    public static bool IsCiBot(string username)
    {
        if (string.Equals(username, "copilot-pull-request-reviewer[bot]", StringComparison.OrdinalIgnoreCase))
            return false;

        return CiBots.Contains(username);
    }

    private static void ClassifyCheckRun(string status, string conclusion, CheckRunCounts counts)
    {
        if (status == "queued")
        {
            counts.Queued++;
        }
        else if (status == "in_progress")
        {
            counts.Pending++;
        }
        else if (conclusion is "success" or "skipped" or "neutral")
        {
            counts.Passed++;
        }
        else if (conclusion is "failure" or "timed_out")
        {
            counts.Failed++;
        }
        else if (conclusion == "cancelled")
        {
            counts.Cancelled++;
        }
    }

    private static async Task<string> RunGhAsync(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh CLI failed (exit {process.ExitCode}): {error}");
        }

        return output.Trim();
    }
}

public class PrInfo
{
    public string Title { get; set; } = "";
    public string HeadSha { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string Url { get; set; } = "";
    public string Author { get; set; } = "";
    public bool Mergeable { get; set; }
    public string MergeableState { get; set; } = "";
    public bool IsMerged { get; set; }
    public string State { get; set; } = ""; // open, closed, merged
}

public class CheckRunResult
{
    public CheckRunCounts Counts { get; set; } = new();
    public List<FailedCheckInfo> Failures { get; set; } = [];
}

public class ReviewResult
{
    public List<ReviewInfo> Approvals { get; set; } = [];
    public List<ReviewInfo> StaleApprovals { get; set; } = [];
}
