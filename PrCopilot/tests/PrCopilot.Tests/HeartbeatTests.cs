// Licensed under the MIT License.

using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

public class HeartbeatTests : IDisposable
{
    private readonly string _tempDir;

    public HeartbeatTests()
    {
        // Use a temp folder under the test output directory for test artifacts
        _tempDir = Path.Combine(
            Path.GetDirectoryName(typeof(HeartbeatTests).Assembly.Location)!,
            "test-tmp",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private MonitorState CreateState() => new()
    {
        Owner = "test-owner",
        Repo = "test-repo",
        PrNumber = 42,
        HeadSha = "abc123",
        HeadBranch = "feature/test",
        SessionFolder = _tempDir
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

    [Fact]
    public void StartForPr_ReturnsIncrementingGeneration()
    {
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen1 = hb.StartForPr(null, state);
        var gen2 = hb.StartForPr(null, state);
        var gen3 = hb.StartForPr(null, state);

        Assert.True(gen2 > gen1);
        Assert.True(gen3 > gen2);
    }

    [Fact]
    public async Task StopGeneration_MatchingGen_StopsRunningHeartbeat()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen = hb.StartForPr(server, state);
        Assert.True(hb.IsRunning);

        // Let at least one heartbeat fire
        await Task.Delay(100);

        hb.StopGeneration(gen);
        Assert.False(hb.IsRunning);
    }

    [Fact]
    public async Task StopGeneration_StaleGen_DoesNotStopRunningHeartbeat()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen1 = hb.StartForPr(server, state);
        var gen2 = hb.StartForPr(server, state);

        // Let heartbeat run
        await Task.Delay(100);

        // Stale gen1 should NOT kill the heartbeat started by gen2
        hb.StopGeneration(gen1);
        Assert.True(hb.IsRunning);
        Assert.Equal(gen2, hb.Generation);
    }

    [Fact]
    public async Task StartForPr_StopsExistingHeartbeatFirst_OnlyOneRunning()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen1 = hb.StartForPr(server, state);
        Assert.True(hb.IsRunning);

        // Let first heartbeat send some messages
        await Task.Delay(200);
        var countAfterFirst = server.SentMessages.Count;

        // Start second — first should be stopped
        var gen2 = hb.StartForPr(server, state);
        Assert.True(hb.IsRunning);
        Assert.NotEqual(gen1, hb.Generation);
        Assert.Equal(gen2, hb.Generation);

        // Verify messages were sent (proves heartbeat was actually running)
        Assert.True(countAfterFirst > 0, "Expected at least one message from the first heartbeat");
    }

    [Fact]
    public void StartForMultiPr_ReturnsIncrementingGeneration()
    {
        using var hb = new HeartbeatManager();

        var gen1 = hb.StartForMultiPr(null, () => 2);
        var gen2 = hb.StartForMultiPr(null, () => 3);

        Assert.True(gen2 > gen1);
    }

    [Fact]
    public async Task StopGeneration_AfterMultiPrStart_MatchingGen_Stops()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();

        var gen = hb.StartForMultiPr(server, () => 2);
        Assert.True(hb.IsRunning);

        await Task.Delay(100);

        hb.StopGeneration(gen);
        Assert.False(hb.IsRunning);
    }

    [Fact]
    public async Task StopGeneration_CrossMode_StaleGen_DoesNotStop()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen1 = hb.StartForPr(server, state);
        var gen2 = hb.StartForMultiPr(server, () => 2);

        await Task.Delay(100);

        // Stale PR gen should not kill multi-PR heartbeat
        hb.StopGeneration(gen1);
        Assert.True(hb.IsRunning);
        Assert.Equal(gen2, hb.Generation);
    }

    [Fact]
    public async Task RaceCondition_OldFinallyDoesNotKillNewHeartbeat()
    {
        // Simulates the exact race condition from the bug:
        // 1. Call A starts heartbeat (gen1)
        // 2. Call B starts heartbeat (gen2) — kills gen1, starts new
        // 3. Call A's finally runs StopGeneration(gen1) — should be no-op
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen1 = hb.StartForPr(server, state);  // Call A starts
        var gen2 = hb.StartForPr(server, state);  // Call B replaces
        Assert.True(hb.IsRunning);

        await Task.Delay(100);

        // Call A's finally block runs with stale generation
        hb.StopGeneration(gen1);

        // gen2's heartbeat should still be alive
        Assert.True(hb.IsRunning);
        Assert.Equal(gen2, hb.Generation);
    }

    [Fact]
    public async Task Stop_AlwaysStopsRegardlessOfGeneration()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        hb.StartForPr(server, state);
        Assert.True(hb.IsRunning);

        await Task.Delay(100);

        // Unconditional Stop always works (for Dispose/cleanup)
        hb.Stop();
        Assert.False(hb.IsRunning);
    }

    [Fact]
    public async Task StartForPr_WithServer_SendsMessages()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 5, InProgress = 3, Total = 8 };

        hb.StartForPr(server, state);

        // Wait for at least the initial + one interval message
        await Task.Delay(6_500);

        hb.Stop();

        // Initial send + at least one loop send
        Assert.True(server.SentMessages.Count >= 2,
            $"Expected at least 2 messages, got {server.SentMessages.Count}");
    }

    [Fact]
    public async Task StopGeneration_StopsMessages_MatchingGen()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen = hb.StartForPr(server, state);

        // Let some messages accumulate
        await Task.Delay(200);
        var countBefore = server.SentMessages.Count;
        Assert.True(countBefore > 0);

        hb.StopGeneration(gen);

        // Wait and verify no new messages
        await Task.Delay(300);
        var countAfter = server.SentMessages.Count;
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task StopGeneration_StaleGen_MessagesContinue()
    {
        await using var server = new FakeMcpServer();
        using var hb = new HeartbeatManager();
        var state = CreateState();

        var gen1 = hb.StartForPr(server, state);
        var gen2 = hb.StartForPr(server, state);

        // Let some messages accumulate
        await Task.Delay(200);
        var countBefore = server.SentMessages.Count;

        // Stale stop — should not affect running heartbeat
        hb.StopGeneration(gen1);

        // Wait for more messages
        await Task.Delay(6_000);
        var countAfter = server.SentMessages.Count;

        Assert.True(countAfter > countBefore,
            $"Expected messages to continue after stale stop, but count stayed at {countBefore}");
    }
}
