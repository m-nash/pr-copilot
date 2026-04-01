// Licensed under the MIT License.

using ModelContextProtocol.Protocol;
using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

public class SamplingHelperTests
{
    [Theory]
    [InlineData("```json\n{\"foo\":1}\n```", "{\"foo\":1}")]
    [InlineData("```\n{\"foo\":1}\n```", "{\"foo\":1}")]
    [InlineData("```json\n{\"foo\":1}```", "{\"foo\":1}")]
    [InlineData("{\"foo\":1}", "{\"foo\":1}")]
    [InlineData("  {\"foo\":1}  ", "{\"foo\":1}")]
    [InlineData("```javascript\nconsole.log(1)\n```", "console.log(1)")]
    [InlineData("no fences here", "no fences here")]
    [InlineData("```\nmulti\nline\ncontent\n```", "multi\nline\ncontent")]
    [InlineData("```json {\"foo\":1}```", "{\"foo\":1}")]
    [InlineData("```{\"foo\":1}```", "{\"foo\":1}")]
    [InlineData("```json{\"foo\":1}```", "{\"foo\":1}")]
    public void StripCodeFences_RemovesFencesCorrectly(string input, string expected)
    {
        var result = SamplingHelper.StripCodeFences(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SampleTextAsync_Throws_WhenSamplingUnavailable()
    {
        var server = new FakeMcpServer();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SamplingHelper.SampleTextAsync(server, "system", "user", maxTokens: 100));
    }

    [Fact]
    public async Task SampleTextAsync_ReturnsText_WhenSamplingAvailable()
    {
        var server = new FakeSamplingMcpServer("Hello from sampling!");
        var result = await SamplingHelper.SampleTextAsync(
            server, "system", "user", maxTokens: 100);
        Assert.Equal("Hello from sampling!", result);
    }

    [Fact]
    public async Task SampleTextAsync_CapturesRequest()
    {
        var server = new FakeSamplingMcpServer("response");
        await SamplingHelper.SampleTextAsync(
            server, "Be helpful", "What is 2+2?", maxTokens: 500, temperature: 0.5f);

        Assert.NotNull(server.LastRequest);
        Assert.Equal(500, server.LastRequest!.MaxTokens);
        Assert.Equal(0.5f, server.LastRequest.Temperature);
        Assert.Equal("Be helpful", server.LastRequest.SystemPrompt);
        Assert.Single(server.LastRequest.Messages);
        Assert.Equal(Role.User, server.LastRequest.Messages[0].Role);
    }

    [Fact]
    public async Task SampleStructuredAsync_ParsesJson()
    {
        var server = new FakeSamplingMcpServer("{\"name\": \"test\", \"value\": 42}");
        var result = await SamplingHelper.SampleStructuredAsync<TestResponse>(
            server, "system", "user", maxTokens: 100);

        Assert.NotNull(result);
        Assert.Equal("test", result!.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task SampleStructuredAsync_HandlesCodeFencedJson()
    {
        var server = new FakeSamplingMcpServer("```json\n{\"name\": \"fenced\", \"value\": 99}\n```");
        var result = await SamplingHelper.SampleStructuredAsync<TestResponse>(
            server, "system", "user", maxTokens: 100);

        Assert.NotNull(result);
        Assert.Equal("fenced", result!.Name);
        Assert.Equal(99, result.Value);
    }

    [Fact]
    public async Task SampleStructuredAsync_ReturnsNull_ForInvalidJson()
    {
        var server = new FakeSamplingMcpServer("this is not json");
        var result = await SamplingHelper.SampleStructuredAsync<TestResponse>(
            server, "system", "user", maxTokens: 100);

        Assert.Null(result);
    }

    [Fact]
    public async Task SampleStructuredAsync_Throws_WhenSamplingUnavailable()
    {
        var server = new FakeMcpServer();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SamplingHelper.SampleStructuredAsync<TestResponse>(server, "system", "user", maxTokens: 100));
    }

    [Fact]
    public async Task ClassifyFreeformAsync_MapsToChoice()
    {
        var server = new FakeSamplingMcpServer("{\"mapsToChoice\": \"address_all\", \"reasoning\": \"User said fix everything\"}");
        var result = await SamplingHelper.ClassifyFreeformAsync(
            server,
            "fix everything",
            "What would you like to do?",
            ["Address all comments", "I'll handle them myself"],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("address_all", result!.MapsToChoice);
    }

    [Fact]
    public async Task ClassifyFreeformAsync_CustomInstruction()
    {
        var server = new FakeSamplingMcpServer("{\"mapsToChoice\": null, \"reasoning\": \"User asked a question\"}");
        var result = await SamplingHelper.ClassifyFreeformAsync(
            server,
            "why did this test fail?",
            "What would you like to do?",
            ["Address all comments", "I'll handle them myself"],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.MapsToChoice);
    }

    [Fact]
    public async Task ClassifyFreeformAsync_InvalidChoiceValue_TreatedAsCustom()
    {
        // LLM returns a choice value that doesn't exist in the valid set
        var server = new FakeSamplingMcpServer("{\"mapsToChoice\": \"nonexistent_choice\", \"reasoning\": \"bad mapping\"}");
        var result = await SamplingHelper.ClassifyFreeformAsync(
            server,
            "do something",
            "What would you like to do?",
            ["Address all comments", "I'll handle them myself"],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.MapsToChoice); // Should be null because nonexistent_choice isn't valid
    }

    [Fact]
    public async Task ClassifyFreeformAsync_ReturnsNull_OnSamplingFailure()
    {
        // FakeSamplingMcpServer returns invalid JSON for the classification
        var server = new FakeSamplingMcpServer("not valid json at all");
        var result = await SamplingHelper.ClassifyFreeformAsync(
            server,
            "fix it",
            "What would you like to do?",
            ["Address all comments"],
            CancellationToken.None);

        // Should return null because JSON parsing failed (caught by ClassifyFreeformAsync)
        Assert.Null(result);
    }

    [Fact]
    public async Task ClassifyFreeformAsync_ReturnsNull_WhenNoChoices()
    {
        var server = new FakeSamplingMcpServer("{}");
        var result = await SamplingHelper.ClassifyFreeformAsync(
            server,
            "hello",
            "What?",
            null,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExplainCommentAsync_ReturnsExplanation()
    {
        var json = "{\"explanation\": \"The reviewer wants a null check\", \"recommendation\": \"Add null check at line 42\", \"recommendationType\": \"implement\"}";
        var server = new FakeSamplingMcpServer(json);
        // Use empty FilePath to avoid triggering GitHub CLI/file fetching in this unit test.
        var comment = new CommentInfo { Author = "reviewer", FilePath = "", Line = 42, Body = "Add null check", Url = "https://github.com/test" };
        var state = new MonitorState { Owner = "owner", Repo = "repo", PrNumber = 1, HeadBranch = "feature" };

        var result = await SamplingHelper.ExplainCommentAsync(server, comment, state, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("The reviewer wants a null check", result!.Explanation);
        Assert.Equal("Add null check at line 42", result.Recommendation);
        Assert.Equal("implement", result.RecommendationType);
    }

    [Fact]
    public async Task InvestigateCiFailureAsync_ReturnsInvestigation()
    {
        var json = "{\"findings\": \"Test X failed with assertion error\", \"suggestedFix\": \"Update expected value\", \"issueType\": \"code\"}";
        var server = new FakeSamplingMcpServer(json);
        var state = new MonitorState
        {
            Owner = "owner",
            Repo = "repo",
            PrNumber = 1,
            // Use a non-matching URL to avoid triggering gh CLI log fetching in this unit test.
            FailedChecks = [new FailedCheckInfo { Name = "tests", Url = "https://example.com/ci/build/123" }]
        };

        var result = await SamplingHelper.InvestigateCiFailureAsync(server, state, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Test X failed with assertion error", result!.Findings);
        Assert.Equal("Update expected value", result.SuggestedFix);
        Assert.Equal("code", result.IssueType);
    }

    [Fact]
    public async Task ComposeReplyAsync_ReturnsReply()
    {
        var json = "{\"replyText\": \"Added the null check — owner/repo@abc1234\"}";
        var server = new FakeSamplingMcpServer(json);
        var comment = new CommentInfo { Author = "reviewer", FilePath = "src/Foo.cs", Line = 42, Body = "Add null check" };
        var state = new MonitorState { Owner = "owner", Repo = "repo", HeadSha = "abc1234" };

        var result = await SamplingHelper.ComposeReplyAsync(server, comment, state, "comment_addressed", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("null check", result!.ReplyText);
    }

    private class TestResponse
    {
        public string? Name { get; set; }
        public int Value { get; set; }
    }
}
