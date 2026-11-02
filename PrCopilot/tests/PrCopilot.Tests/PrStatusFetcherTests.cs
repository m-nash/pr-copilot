// Licensed under the MIT License.

using System.Text.Json;
using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

[Collection("GitHub CLI")]
public class PrStatusFetcherTests : IDisposable
{
    private readonly FakeGitHubCliFixture _gh = new();

    [Fact]
    public async Task FetchReviewsAsync_ReadsApprovalsBeyondFirstPage()
    {
        _gh.RespondToPages("repos/owner/repo/pulls/42/reviews",
            """[{"user":{"login":"early-reviewer"},"state":"APPROVED","commit_id":"old-sha","submitted_at":"2026-10-27T02:45:08Z"}]""",
            """[{"user":{"login":"late-reviewer"},"state":"APPROVED","commit_id":"head-sha","submitted_at":"2026-10-30T21:02:17Z"}]""");

        var result = await PrStatusFetcher.FetchReviewsAsync("owner", "repo", 42, "head-sha");

        Assert.Equal("late-reviewer", Assert.Single(result.Approvals).Author);
        Assert.Equal("early-reviewer", Assert.Single(result.StaleApprovals).Author);
    }

    [Fact]
    public async Task FetchReviewsAsync_LaterCommentReviewKeepsApproval()
    {
        _gh.RespondToPages("repos/owner/repo/pulls/42/reviews", """
            [
              {"user":{"login":"approver"},"state":"APPROVED","commit_id":"head-sha","submitted_at":"2026-10-30T20:00:00Z"},
              {"user":{"login":"approver"},"state":"COMMENTED","commit_id":"head-sha","submitted_at":"2026-10-30T20:05:00Z"},
              {"user":{"login":"blocker"},"state":"APPROVED","commit_id":"head-sha","submitted_at":"2026-10-30T20:00:00Z"},
              {"user":{"login":"blocker"},"state":"CHANGES_REQUESTED","commit_id":"head-sha","submitted_at":"2026-10-30T20:10:00Z"},
              {"user":{"login":"blocker"},"state":"COMMENTED","commit_id":"head-sha","submitted_at":"2026-10-30T20:15:00Z"}
            ]
            """);

        var result = await PrStatusFetcher.FetchReviewsAsync("owner", "repo", 42, "head-sha");

        var approval = Assert.Single(result.Approvals);
        Assert.Equal("approver", approval.Author);
        Assert.Equal(new DateTime(2026, 10, 30, 20, 0, 0, DateTimeKind.Utc), approval.SubmittedAt);
        Assert.Empty(result.StaleApprovals);
    }

    [Fact]
    public async Task FetchUnresolvedCommentsAsync_ReadsThreadsBeyondFirstPage()
    {
        _gh.RespondToPages("graphql",
            ReviewThreadPage("resolved-thread", isResolved: true, hasNextPage: true),
            ReviewThreadPage("open-thread", isResolved: false, hasNextPage: false));

        var comments = await PrStatusFetcher.FetchUnresolvedCommentsAsync("owner", "repo", 42, "author");

        Assert.Equal("open-thread", Assert.Single(comments).Id);
    }

    [Fact]
    public async Task FetchCheckRunsAsync_CountsFailedCheckRunBeyondFirstPage()
    {
        _gh.RespondToPages("repos/owner/repo/commits/head-sha/check-runs",
            """{"check_runs":[{"name":"build","status":"completed","conclusion":"success"}]}""",
            """{"check_runs":[{"name":"test","status":"completed","conclusion":"failure"}]}""");
        _gh.RespondToPages("repos/owner/repo/commits/head-sha/status", """{"statuses":[]}""");

        var result = await PrStatusFetcher.FetchCheckRunsAsync("owner", "repo", "head-sha");

        Assert.Equal(1, result.Counts.Failed);
        Assert.Equal("test", Assert.Single(result.Failures).Name);
    }

    [Fact]
    public async Task FetchCheckRunsAsync_CountsFailedStatusBeyondFirstPage()
    {
        _gh.RespondToPages("repos/owner/repo/commits/head-sha/check-runs", """{"check_runs":[]}""");
        _gh.RespondToPages("repos/owner/repo/commits/head-sha/status",
            """{"statuses":[{"context":"license/cla","state":"success"}]}""",
            """{"statuses":[{"context":"legacy-ci","state":"failure"}]}""");

        var result = await PrStatusFetcher.FetchCheckRunsAsync("owner", "repo", "head-sha");

        Assert.Equal(1, result.Counts.Failed);
    }

    private static string ReviewThreadPage(string threadId, bool isResolved, bool hasNextPage) => JsonSerializer.Serialize(new
    {
        data = new
        {
            repository = new
            {
                pullRequest = new
                {
                    reviewThreads = new
                    {
                        pageInfo = new { hasNextPage, endCursor = threadId },
                        nodes = new[]
                        {
                            new
                            {
                                id = threadId,
                                isResolved,
                                comments = new
                                {
                                    totalCount = 1,
                                    nodes = new[] { new { author = new { login = "reviewer" }, body = "Please fix", url = "https://github.com/owner/repo/pull/42" } }
                                }
                            }
                        }
                    }
                }
            }
        }
    });

    public void Dispose() => _gh.Dispose();
}
