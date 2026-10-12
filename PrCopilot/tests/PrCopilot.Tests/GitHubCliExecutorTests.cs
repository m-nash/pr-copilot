// Licensed under the MIT License.

using System.Text.Json;
using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

[Collection("GitHub CLI")]
public class GitHubCliExecutorTests : IDisposable
{
    private readonly FakeGitHubCliFixture _gh = new();

    [Fact]
    public async Task FetchPrFileDiffAsync_SelectsRequestedFileAcrossPages()
    {
        _gh.Respond("""
            [
              [{"filename":"src/other.cs","patch":"unrelated change"}],
              [{"filename":"src/target.cs","patch":"@@ -1 +1 @@\n-old\n+new"}]
            ]
            """);

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, "src/target.cs");

        Assert.True(success, output);
        Assert.Equal("@@ -1 +1 @@\n-old\n+new", output);
        Assert.Equal(["api", "repos/owner/repo/pulls/42/files?per_page=100", "--paginate", "--slurp"], Assert.Single(_gh.ReadRequests()));
    }

    [Theory]
    [InlineData("src/file with spaces.cs")]
    [InlineData("src/quoted\"file.cs")]
    [InlineData("src/caf\u00E9.cs")]
    public async Task FetchPrFileDiffAsync_MatchesExactFilePath(string filePath)
    {
        _gh.Respond(JsonSerializer.Serialize(new[]
        {
            new[]
            {
                new { filename = filePath.ToUpperInvariant(), patch = "wrong file" },
                new { filename = filePath, patch = "correct file" }
            }
        }));

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, filePath);

        Assert.True(success, output);
        Assert.Equal("correct file", output);
    }

    [Theory]
    [InlineData("src/old.cs")]
    [InlineData("src/new.cs")]
    public async Task FetchPrFileDiffAsync_FindsRenamedFile(string filePath)
    {
        _gh.Respond("""[[{"filename":"src/new.cs","previous_filename":"src/old.cs","patch":"renamed change"}]]""");

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, filePath);

        Assert.True(success, output);
        Assert.Equal("renamed change", output);
    }

    [Fact]
    public async Task FetchPrFileDiffAsync_PrefersCurrentPathOverRenameSource()
    {
        _gh.Respond("""
            [[
              {"filename":"src/renamed.cs","previous_filename":"src/target.cs","patch":"renamed change"},
              {"filename":"src/target.cs","patch":"current file"}
            ]]
            """);

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, "src/target.cs");

        Assert.True(success, output);
        Assert.Equal("current file", output);
    }

    [Fact]
    public async Task FetchPrFileDiffAsync_TruncatesLargePatch()
    {
        var patch = new string('x', 6000);
        _gh.Respond(JsonSerializer.Serialize(new[] { new[] { new { filename = "src/target.cs", patch } } }));

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, "src/target.cs");

        Assert.True(success, output);
        Assert.Equal(patch[..5000] + "\n... [truncated]", output);
    }

    [Theory]
    [InlineData("""[[{"filename":"src/target.cs"}]]""")]
    [InlineData("""[[{"filename":"src/target.cs","patch":null}]]""")]
    [InlineData("""[[{"filename":"src/target.cs","patch":""}]]""")]
    [InlineData("""[[{"filename":"src/other.cs","patch":"other"}]]""")]
    public async Task FetchPrFileDiffAsync_ReportsUnavailablePatch(string response)
    {
        _gh.Respond(response);

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, "src/target.cs");

        Assert.False(success);
        Assert.Contains("src/target.cs", output);
        Assert.DoesNotContain("accepts at most", output);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[[null]]")]
    public async Task FetchPrFileDiffAsync_ReportsInvalidResponse(string response)
    {
        _gh.Respond(response);

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, "src/target.cs");

        Assert.False(success);
        Assert.Contains("parse", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchPrFileDiffAsync_PreservesCliFailure()
    {
        _gh.Respond("fixture API failure", exitCode: 1);

        var (success, output) = await GitHubCliExecutor.FetchPrFileDiffAsync("owner", "repo", 42, "src/target.cs");

        Assert.False(success);
        Assert.Equal("fixture API failure", output);
    }

    [Fact]
    public void FakeGitHubCliFixture_DisposeIsIdempotent()
    {
        var fixture = new FakeGitHubCliFixture();
        fixture.Dispose();

        Assert.Null(Record.Exception(fixture.Dispose));
    }

    public void Dispose() => _gh.Dispose();
}
