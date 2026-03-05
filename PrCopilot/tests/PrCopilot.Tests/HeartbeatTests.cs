// Licensed under the MIT License.

using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

public class HeartbeatTests
{
    private static MonitorState CreateState() => new()
    {
        Owner = "test-owner",
        Repo = "test-repo",
        PrNumber = 42,
        HeadSha = "abc123",
        HeadBranch = "feature/test",
        SessionFolder = Path.GetTempPath()
    };

    [Fact]
    public void BuildHeartbeatMessage_AllGreen_ShowsPassedCount()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 43, Total = 43 };

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.Contains("43✅", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_MixedChecks_ShowsAllCategories()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 10, Failed = 2, InProgress = 3, Total = 15 };

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.Contains("10✅", message);
        Assert.Contains("2❌", message);
        Assert.Contains("3⏳", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_NoChecks_OmitsCiSection()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Total = 0 };

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.DoesNotContain("CI:", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_PendingChecks_ShowsPending()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Total = 5 };

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.Contains("CI: pending", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_ContainsTimestamp()
    {
        var state = CreateState();

        var before = DateTime.Now;
        var message = HeartbeatManager.BuildHeartbeatMessage(state);
        var after = DateTime.Now;

        // Accept either the before or after timestamp to avoid flakiness at second boundaries
        var beforeStr = before.ToString("h:mm:ss tt");
        var afterStr = after.ToString("h:mm:ss tt");
        Assert.True(
            message.Contains(beforeStr) || message.Contains(afterStr),
            $"Expected message to contain '{beforeStr}' or '{afterStr}', but was: {message}");
    }

    [Fact]
    public void BuildHeartbeatMessage_ContainsBackgroundMonitoring()
    {
        var state = CreateState();

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.Contains("Background Monitoring", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_ContainsPrNumber()
    {
        var state = CreateState();

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.Contains("PR #42", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_ContainsPromptMessage()
    {
        var state = CreateState();

        var message = HeartbeatManager.BuildHeartbeatMessage(state);

        Assert.Contains("Will prompt when your attention is needed", message);
    }

    [Fact]
    public void BuildHeartbeatMessage_SpinnerCycles()
    {
        HeartbeatManager.HeartbeatCounter = 0;
        var state = CreateState();

        var first = HeartbeatManager.BuildHeartbeatMessage(state);
        var second = HeartbeatManager.BuildHeartbeatMessage(state);

        // Spinner is based on HeartbeatCounter which doesn't change between BuildHeartbeatMessage calls,
        // but the counter changes via SendAsync. The spinner frame is determined
        // by the current counter value. Verify the spinner icons are from the expected set.
        var firstSpinner = first[..2]; // emoji is 2 chars (surrogate pair or single)
        var secondSpinner = second[..2];
        // Both calls use counter=0, so they should have the same spinner
        Assert.Equal(firstSpinner, secondSpinner);
        Assert.Contains(firstSpinner, HeartbeatManager.SpinnerFrames);

        // Simulate counter increment (what SendAsync does)
        HeartbeatManager.HeartbeatCounter = 1;
        var third = HeartbeatManager.BuildHeartbeatMessage(state);
        var thirdSpinner = third[..2];

        // Counter 0 and 1 should produce different spinner frames
        Assert.NotEqual(firstSpinner, thirdSpinner);
        Assert.Contains(thirdSpinner, HeartbeatManager.SpinnerFrames);
    }

    [Fact]
    public void BuildMultiPrHeartbeatMessage_ContainsSessionCount()
    {
        var message = HeartbeatManager.BuildMultiPrHeartbeatMessage(3);

        Assert.Contains("3 PR(s)", message);
    }

    [Fact]
    public void BuildMultiPrHeartbeatMessage_ContainsBackgroundMonitoring()
    {
        var message = HeartbeatManager.BuildMultiPrHeartbeatMessage(3);

        Assert.Contains("Background Monitoring", message);
    }
}
