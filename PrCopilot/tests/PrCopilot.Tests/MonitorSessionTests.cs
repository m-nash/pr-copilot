// Licensed under the MIT License.

using PrCopilot.Tools;

namespace PrCopilot.Tests;

public class MonitorSessionTests
{
    [Fact]
    public void CancelPolling_CancelsTokenAndCreatesNew()
    {
        using var session = new MonitorSession();
        var tokenBefore = session.PollToken;
        Assert.False(tokenBefore.IsCancellationRequested);

        session.CancelPolling();

        // Old token should be cancelled
        Assert.True(tokenBefore.IsCancellationRequested);
        // New token should be fresh (not cancelled)
        Assert.False(session.PollToken.IsCancellationRequested);
    }

    [Fact]
    public void StopPermanently_SetsIsStopped()
    {
        using var session = new MonitorSession();
        Assert.False(session.IsStopped);

        session.StopPermanently();

        Assert.True(session.IsStopped);
    }

    [Fact]
    public void StopPermanently_CancelsToken_WithoutCreatingNew()
    {
        using var session = new MonitorSession();
        var tokenBefore = session.PollToken;

        session.StopPermanently();

        // Captured token should be cancelled
        Assert.True(tokenBefore.IsCancellationRequested);
        // _pollCts is nulled out — PollToken returns CancellationToken.None (not a fresh live token)
        Assert.False(session.PollToken.CanBeCanceled);
    }

    [Fact]
    public void StopPermanently_CancelsLinkedToken()
    {
        using var session = new MonitorSession();

        // Simulate what pr_monitor_next_step does: cancel old, read new token, create linked
        session.CancelPolling();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            CancellationToken.None, session.PollToken);

        // Now simulate pr_monitor_stop calling StopPermanently
        session.StopPermanently();

        // The linked token should be cancelled because PollToken was cancelled
        Assert.True(linkedCts.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RunStep_ReplacingAnActiveOwnerRequiresReconnectEvenWhenStateIsUnchanged()
    {
        using var session = new MonitorSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = session.RunStepAsync(async (token, _) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "original";
        }, CancellationToken.None);
        await entered.Task;

        var replacement = session.RunStepAsync((_, reconnect) =>
        {
            Assert.True(reconnect);
            return Task.FromResult("replacement");
        }, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        Assert.Equal("replacement", await replacement);
    }

    [Fact]
    public async Task StopPermanently_WakesExistingAndFutureTriggerWaits()
    {
        using var session = new MonitorSession();
        var existing = session.WaitForTriggerAsync(CancellationToken.None);

        session.StopPermanently();

        Assert.Equal("STOP", await existing.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("STOP", await session.WaitForTriggerAsync(CancellationToken.None));
        Assert.True(session.StopToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopPermanently_CancelsOnlyMemberScopedExecution(bool cancelOnStop)
    {
        using var session = new MonitorSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken executionToken = default;
        var step = session.RunStepAsync(async (token, _) =>
        {
            executionToken = token;
            entered.SetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            return "continued";
        }, CancellationToken.None, cancelOnStop);
        await entered.Task;

        session.StopPermanently();
        release.SetResult();

        Assert.Equal(cancelOnStop, executionToken.IsCancellationRequested);
        if (cancelOnStop)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step);
        else
            Assert.Equal("continued", await step);
    }

    [Fact]
    public async Task RunStep_StoppedMemberAbandonsCombinedOwnershipWaitWithoutReleasingWriteOwner()
    {
        using var session = new MonitorSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = session.RunStepAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return "write completed";
        }, CancellationToken.None);
        await entered.Task;
        var waiting = session.RunStepAsync((_, _) => throw new InvalidOperationException("Stopped member must not execute"),
            CancellationToken.None, cancelOnStop: false);

        session.StopPermanently();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(owner.IsCompleted);
        release.SetResult();
        Assert.Equal("write completed", await owner);
    }
}
