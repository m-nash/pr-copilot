// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

[Collection("GitHub CLI")]
public class MonitorFlowToolsTests : IDisposable
{
    private const string Explanation = "{\"explanation\":\"Review the change\",\"recommendation\":\"Add a test\",\"recommendationType\":\"implement\"}";
    private readonly FakeGitHubCliFixture _gh = new();

    [Fact]
    public async Task ExplainComment_RetriesWithLargerBudget()
    {
        var server = new FakeSamplingMcpServer(request =>
            [request.MaxTokens >= 8192 ? Explanation : "{\"explanation\":\"Review the"]);
        var state = new MonitorState();

        var handled = await ExplainCommentAsync(server, state);

        Assert.True(handled);
        Assert.Equal([4096, 8192], server.Requests.Select(request => request.MaxTokens));
        Assert.Equal("Add a test", state.LastRecommendation);
        Assert.Equal("task_complete", state.SamplingCompletionEvent);
    }

    [Fact]
    public async Task ExplainComment_StopsAfterTwoInvalidResponses()
    {
        var server = new FakeSamplingMcpServer("{\"explanation\":\"Review the");
        var state = new MonitorState();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExplainCommentAsync(server, state));

        Assert.Contains("after 2 attempts", error.Message);
        Assert.Equal([4096, 8192], server.Requests.Select(request => request.MaxTokens));
        Assert.Null(state.LastRecommendation);
        Assert.Null(state.SamplingCompletionEvent);
    }

    [Fact]
    public async Task ExplainComment_DoesNotRetrySuccessfulResponse()
    {
        var server = new FakeSamplingMcpServer(Explanation);
        var state = new MonitorState();

        Assert.True(await ExplainCommentAsync(server, state));
        Assert.Single(server.Requests);
        Assert.Equal("Add a test", state.LastRecommendation);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PrMonitorStart_AlreadyCanceledRequestDoesNotStartWork(bool stoppedState, bool permanentlyStopped)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var sessions = GetSessions();
        using var session = new MonitorSession
        {
            State = new MonitorState
            {
                Owner = owner,
                Repo = "repo",
                PrNumber = 42,
                CurrentState = stoppedState ? MonitorStateId.Stopped : MonitorStateId.Idle
            }
        };
        var pollToken = session.PollToken;
        if (permanentlyStopped)
            session.StopPermanently();
        Assert.True(sessions.TryAdd(monitorId, session));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "", cancellation.Token));

            Assert.Same(session, sessions[monitorId]);
            Assert.Equal(permanentlyStopped, session.IsStopped);
            Assert.Equal(permanentlyStopped, pollToken.IsCancellationRequested);
            Assert.Empty(_gh.ReadRequests());
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(MonitorStateId.Idle)]
    [InlineData(MonitorStateId.ExecutingTask)]
    public async Task PrMonitorStart_ReusesActiveSession(MonitorStateId currentState)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var sessions = GetSessions();
        using var session = new MonitorSession
        {
            State = new MonitorState { Owner = owner, Repo = "repo", PrNumber = 42, CurrentState = currentState }
        };
        Assert.True(sessions.TryAdd(monitorId, session));

        try
        {
            var result = await new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "");
            using var response = JsonDocument.Parse(result);

            Assert.Equal(monitorId, response.RootElement.GetProperty("monitor_id").GetString());
            Assert.Contains("Resuming existing monitor", response.RootElement.GetProperty("message").GetString()!);
            Assert.Same(session, sessions[monitorId]);
            Assert.Equal(currentState, session.State.CurrentState);
            Assert.False(session.IsStopped);
            Assert.Empty(_gh.ReadRequests());
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task PrMonitorStart_SharesConcurrentStoppedInitialization()
    {
        _gh.PauseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var sessions = GetSessions();
        using var stopped = new MonitorSession
        {
            State = new MonitorState { CurrentState = MonitorStateId.Stopped }
        };
        Assert.True(sessions.TryAdd(monitorId, stopped));
        var first = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "");
        Task<string>? second = null;
        try
        {
            await _gh.WaitForStartupRequestsAsync(1);
            second = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "");
            _gh.ReleaseStartupRequests();

            var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            var secondError = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Contains("fixture startup failure", firstError.Message);
            Assert.Contains("fixture startup failure", secondError.Message);
            Assert.Single(_gh.ReadStartupRequests());
            Assert.False(sessions.ContainsKey(monitorId));
        }
        finally
        {
            _gh.ReleaseStartupRequests();
            await Record.ExceptionAsync(() => first);
            if (second != null)
                await Record.ExceptionAsync(() => second);
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task PrMonitorStart_CanceledWaiterDoesNotRestartSharedInitialization()
    {
        _gh.PauseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var first = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "");
        Task<string>? second = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await _gh.WaitForStartupRequestsAsync(1);
            second = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "", cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(first.IsCompleted);
            _gh.ReleaseStartupRequests();
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            Assert.Single(_gh.ReadStartupRequests());
        }
        finally
        {
            _gh.ReleaseStartupRequests();
            await Record.ExceptionAsync(() => first);
            if (second != null)
                await Record.ExceptionAsync(() => second);
        }
    }

    [Fact]
    public async Task PrMonitorStart_CanceledFirstWaiterDoesNotCancelOtherWaiters()
    {
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState { Owner = owner, Repo = "repo", PrNumber = 42, SessionFolder = _gh.DirectoryPath };
        File.WriteAllText(state.LogFile + ".viewer.pid",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var cancellation = new CancellationTokenSource();
        var first = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath, cancellation.Token);
        Task<string>? second = null;
        try
        {
            await _gh.WaitForStartupRequestsAsync(1);
            second = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            _gh.ReleaseStartupRequests();

            var result = await second;
            using var response = JsonDocument.Parse(result);
            Assert.Equal(monitorId, response.RootElement.GetProperty("monitor_id").GetString());
            Assert.Contains("Monitoring initialized", response.RootElement.GetProperty("message").GetString()!);
            Assert.Single(_gh.ReadStartupRequests());
            Assert.False(GetSessions()[monitorId].IsStopped);
        }
        finally
        {
            _gh.ReleaseStartupRequests();
            await Record.ExceptionAsync(() => first);
            if (second != null)
                await Record.ExceptionAsync(() => second);
            if (GetSessions().TryRemove(monitorId, out var session))
            {
                session.StopPermanently();
                session.Dispose();
            }
        }
    }

    [Fact]
    public async Task PrMonitorStart_DifferentPrInitializationsRemainIndependent()
    {
        _gh.PauseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var first = new MonitorFlowTools().PrMonitorStart(owner, "first-repo", 42, "");
        var second = new MonitorFlowTools().PrMonitorStart(owner, "second-repo", 42, "");
        try
        {
            await _gh.WaitForStartupRequestsAsync(2);
            _gh.ReleaseStartupRequests();
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Equal(2, _gh.ReadStartupRequests().Count);
        }
        finally
        {
            _gh.ReleaseStartupRequests();
            await Record.ExceptionAsync(() => first);
            await Record.ExceptionAsync(() => second);
        }
    }

    [Fact]
    public async Task PrMonitorStart_FailedInitializationCanBeRetried()
    {
        _gh.PauseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        _gh.ReleaseStartupRequests();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, ""));

        Assert.Equal(2, _gh.ReadStartupRequests().Count);
    }

    public void Dispose() => _gh.Dispose();

    private static async Task<bool> ExplainCommentAsync(FakeSamplingMcpServer server, MonitorState state)
    {
        var method = typeof(MonitorFlowTools).GetMethod("TryHandleViaSamplingAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var action = new MonitorAction
        {
            Action = "execute",
            Task = "explain_comment",
            Context = new CommentInfo { Author = "reviewer", FilePath = "", Body = "Add a test" }
        };
        var task = (Task<bool>)method.Invoke(null, [server, state, action, CancellationToken.None])!;
        return await task;
    }

    private static ConcurrentDictionary<string, MonitorSession> GetSessions()
    {
        var field = typeof(MonitorFlowTools).GetField("_sessions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<ConcurrentDictionary<string, MonitorSession>>(field.GetValue(null));
    }
}
