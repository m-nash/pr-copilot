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

        // Token should be cancelled
        Assert.True(tokenBefore.IsCancellationRequested);
        // PollToken should still return the same cancelled token (not a new one)
        Assert.True(session.PollToken.IsCancellationRequested);
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
}
