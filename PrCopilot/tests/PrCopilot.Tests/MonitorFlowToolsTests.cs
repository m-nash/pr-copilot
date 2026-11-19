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

        var handled = await HandleViaSamplingAsync(server, state, "explain_comment");

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

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HandleViaSamplingAsync(server, state, "explain_comment"));

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

        Assert.True(await HandleViaSamplingAsync(server, state, "explain_comment"));
        Assert.Single(server.Requests);
        Assert.Equal("Add a test", state.LastRecommendation);
    }

    [Fact]
    public async Task ComposeReply_RetriesWithLargerBudget()
    {
        var server = new FakeSamplingMcpServer(request =>
            [request.MaxTokens >= 8192 ? "{\"replyText\":\"Added the test\"}" : "{\"replyText\":\"Added the"]);
        var state = new MonitorState { PendingCompletionEvent = "comment_addressed" };

        Assert.True(await HandleViaSamplingAsync(server, state, "compose_reply"));
        Assert.Equal([4096, 8192], server.Requests.Select(request => request.MaxTokens));
        Assert.Equal("Added the test", state.PendingReplyText);
        Assert.Equal("comment_addressed", state.SamplingCompletionEvent);
    }

    [Fact]
    public async Task PrMonitorNextStep_ComposeReplyFailure_DelegatesReplyToAgent()
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        using var session = new MonitorSession { State = CreateResolvingCommentState(owner) };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("resume");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            using var response = JsonDocument.Parse(await new MonitorFlowTools().PrMonitorNextStep(
                monitorId, "comment_addressed", server: server, cancellationToken: cancellation.Token));

            Assert.Equal("compose_reply", response.RootElement.GetProperty("task").GetString());
            Assert.Contains("reply_text", response.RootElement.GetProperty("instructions").GetString()!);
            Assert.Equal("compose_reply", session.State.PendingAction?.Task);
            Assert.Empty(server.Elicitations);
            Assert.Equal(2, server.Requests.Count);
            Assert.Empty(_gh.ReadRequests());
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
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
                new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "", cancellationToken: cancellation.Token));

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrMonitorNextStep_ReadyResumesPendingWorkWithoutSideEffects(bool monitorAll, bool pendingGitHubAction)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = CreateCommentState(owner);
        var pending = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);
        if (pendingGitHubAction)
        {
            state.PendingReplyText = "Approved reply linking the fix";
            pending = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        }
        state.HeadSha = "already-pushed-fix";
        var sessions = GetSessions();
        state.WaitingForReplyComments = [new CommentInfo { Id = "waiting", Author = "another-reviewer" }];
        using var session = new MonitorSession { State = state, PendingTriggerContent = "ACTION|waiting" };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("stop");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            var tools = new MonitorFlowTools();
            using var start = JsonDocument.Parse(await tools.PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath));
            Assert.Contains("event='ready'", start.RootElement.GetProperty("message").GetString()!);
            using var response = JsonDocument.Parse(await tools.PrMonitorNextStep(
                monitorAll ? "all" : monitorId, "ready", server: server, cancellationToken: cancellation.Token));

            Assert.Equal("execute", response.RootElement.GetProperty("action").GetString());
            Assert.Equal("resume_pending_task", response.RootElement.GetProperty("task").GetString());
            var context = response.RootElement.GetProperty("context");
            Assert.Equal(pending.Task, context.GetProperty("task").GetString());
            Assert.Equal("second", context.GetProperty("context").GetProperty("id").GetString());
            Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
            Assert.Equal(CommentFlowState.ExplainAllIterating, state.CommentFlow);
            Assert.Equal(1, state.CurrentCommentIndex);
            Assert.Equal("Preserve the completion marker", state.LastRecommendation);
            Assert.Equal(pendingGitHubAction, state.UnresolvedComments[1].IsAddressed);
            Assert.Equal("ACTION|waiting", session.PendingTriggerContent);
            Assert.Empty(server.Elicitations);
            Assert.Empty(server.Requests);
            Assert.Empty(_gh.ReadRequests());
            if (monitorAll)
                Assert.Equal(monitorId, response.RootElement.GetProperty("monitorId").GetString());
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrMonitorNextStep_ReadyRePresentsPromptAndRetainsFreeformTask(bool monitorAll)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = CreateCommentState(owner);
        state.CurrentState = MonitorStateId.ExecutingTask;
        var prompt = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        var sessions = GetSessions();
        using var session = new MonitorSession { State = state };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("Continue with our previous plan");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            var tools = new MonitorFlowTools();
            await tools.PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath);
            var nextMonitorId = monitorAll ? "all" : monitorId;
            using var response = JsonDocument.Parse(await tools.PrMonitorNextStep(
                nextMonitorId, "ready", server: server, cancellationToken: cancellation.Token));

            Assert.Equal("interpret_freeform", response.RootElement.GetProperty("task").GetString());
            Assert.Equal(prompt.Question, Assert.Single(server.Elicitations).Message);
            Assert.Equal(1, state.CurrentCommentIndex);
            Assert.Equal(CommentFlowState.ExplainAllIterating, state.CommentFlow);

            using var resumed = JsonDocument.Parse(await tools.PrMonitorNextStep(
                nextMonitorId, "ready", server: server, cancellationToken: cancellation.Token));

            Assert.Equal("resume_pending_task", resumed.RootElement.GetProperty("task").GetString());
            var pending = resumed.RootElement.GetProperty("context");
            Assert.Equal("interpret_freeform", pending.GetProperty("task").GetString());
            Assert.Contains("Continue with our previous plan", pending.GetProperty("instructions").GetString()!);
            Assert.Single(server.Elicitations);
            Assert.Single(server.Requests);
            Assert.Empty(_gh.ReadRequests());
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrMonitorNextStep_MergeConflictTargetMerge_DelegatesWithoutGitHubWrites(bool monitorAll)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            HeadBranch = "feature/conflict-fix",
            BaseBranch = "release",
            HeadSha = "original-head",
            HasMergeConflict = true
        };
        var prompt = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.MergeConflict);
        var sessions = GetSessions();
        using var session = new MonitorSession { State = state };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("merge_target_branch", "stop");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            var tools = new MonitorFlowTools();
            var result = await tools.PrMonitorNextStep(
                monitorAll ? "all" : monitorId, "ready", server: server, cancellationToken: cancellation.Token);
            Assert.Equal(prompt.Question, Assert.Single(server.Elicitations).Message);
            if (monitorAll)
            {
                using var relay = JsonDocument.Parse(result);
                Assert.Equal("relay_choice", relay.RootElement.GetProperty("task").GetString());
                Assert.Equal(monitorId, relay.RootElement.GetProperty("monitorId").GetString());
                result = await tools.PrMonitorNextStep(
                    monitorId, "user_chose", choice: "merge_target_branch", server: server, cancellationToken: cancellation.Token);
            }

            using var response = JsonDocument.Parse(result);
            Assert.Equal("execute", response.RootElement.GetProperty("action").GetString());
            Assert.Equal("merge_target_branch", response.RootElement.GetProperty("task").GetString());
            var instructions = response.RootElement.GetProperty("instructions").GetString();
            Assert.Contains("target branch 'release' into PR head branch 'feature/conflict-fix'", instructions);
            Assert.Contains("git merge --no-commit", instructions);
            Assert.Contains("Do not rebase", instructions);
            var context = response.RootElement.GetProperty("context");
            Assert.Equal("feature/conflict-fix", context.GetProperty("headBranch").GetString());
            Assert.Equal("release", context.GetProperty("baseBranch").GetString());
            Assert.Equal("original-head", context.GetProperty("headSha").GetString());
            Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
            Assert.Single(server.Elicitations);
            Assert.Empty(server.Requests);
            Assert.Empty(_gh.ReadRequests());
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrMonitorNextStep_MergeConflictTargetMerge_FreeformUsesTheSameMergeInstructions(bool monitorAll)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            HeadBranch = "feature/conflict-fix",
            BaseBranch = "release",
            HasMergeConflict = true
        };
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.MergeConflict);
        var sessions = GetSessions();
        using var session = new MonitorSession { State = state };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("rebase and run the focused tests");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            var tools = new MonitorFlowTools();
            using var response = JsonDocument.Parse(await tools.PrMonitorNextStep(
                monitorAll ? "all" : monitorId, "ready", server: server, cancellationToken: cancellation.Token));

            Assert.Equal("interpret_freeform", response.RootElement.GetProperty("task").GetString());
            var instructions = response.RootElement.GetProperty("instructions").GetString();
            Assert.Contains("target branch 'release' into PR head branch 'feature/conflict-fix'", instructions);
            Assert.Contains("git merge --no-commit", instructions);
            Assert.Contains("Do not rebase", instructions);
            Assert.Contains("push_completed", instructions);
            Assert.Single(server.Elicitations);
            Assert.Single(server.Requests);
            Assert.Empty(_gh.ReadRequests());
            Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(false, "ready", false, false)]
    [InlineData(false, "task_complete", false, false)]
    [InlineData(true, "ready", false, false)]
    [InlineData(false, "ready", true, false)]
    [InlineData(true, "ready", true, false)]
    [InlineData(false, "ready", false, true)]
    [InlineData(true, "ready", false, true)]
    public async Task PrMonitorNextStep_InFlightWrite_BlocksReconciliationAndStaleCompletion(
        bool monitorAll, string nextEvent, bool cancelOriginal, bool cancelWaiter)
    {
        _gh.PauseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = CreateResolvingCommentState(owner);
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        var tools = new MonitorFlowTools();
        var originalServer = new FakeElicitationMcpServer("stop");
        var replacementServer = new FakeElicitationMcpServer("Explain the remaining comment without editing code");
        using var originalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var replacementCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var original = tools.PrMonitorNextStep(monitorId, "comment_addressed",
            data: "{\"reply_text\":\"Approved reply\"}", server: originalServer, cancellationToken: originalCancellation.Token);
        Task<string>? replacement = null;
        try
        {
            await _gh.WaitForResolveRequestsAsync(1);
            if (cancelOriginal)
                originalCancellation.Cancel();
            replacement = tools.PrMonitorNextStep(monitorAll ? "all" : monitorId, nextEvent,
                data: "{\"recommendation\":\"stale completion\"}", server: replacementServer, cancellationToken: replacementCancellation.Token);

            await Assert.ThrowsAsync<TimeoutException>(() => replacement.WaitAsync(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(0, state.CurrentCommentIndex);
            Assert.True(state.PendingResolveAfterAddress);
            Assert.Empty(replacementServer.Elicitations);
            if (cancelWaiter)
            {
                replacementCancellation.Cancel();
                using var canceled = JsonDocument.Parse(await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal("stop", canceled.RootElement.GetProperty("action").GetString());
                Assert.False(original.IsCompleted);
            }

            _gh.ReleaseResolveRequests();
            using var originalResult = JsonDocument.Parse(await original.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("stop", originalResult.RootElement.GetProperty("action").GetString());
            if (cancelWaiter)
            {
                replacement = tools.PrMonitorNextStep(monitorAll ? "all" : monitorId, "ready", server: replacementServer);
            }
            using var response = JsonDocument.Parse(await replacement.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.Equal("interpret_freeform", response.RootElement.GetProperty("task").GetString());
            Assert.Contains("Second comment", Assert.Single(replacementServer.Elicitations).Message);
            Assert.Empty(originalServer.Elicitations);
            Assert.Equal(1, state.CurrentCommentIndex);
            Assert.False(state.PendingResolveAfterAddress);
            Assert.Equal("Original recommendation", state.LastRecommendation);
            var requests = _gh.ReadRequests();
            Assert.Equal(2, requests.Count);
            Assert.Single(requests, request => request.Any(argument => argument.Contains("resolveReviewThread", StringComparison.Ordinal)));
            Assert.Single(requests, request => request.Any(argument => argument.EndsWith("/replies", StringComparison.Ordinal)));
        }
        finally
        {
            _gh.ReleaseResolveRequests();
            originalCancellation.Cancel();
            replacementCancellation.Cancel();
            await Record.ExceptionAsync(() => original.WaitAsync(TimeSpan.FromSeconds(3)));
            if (replacement != null)
                await Record.ExceptionAsync(() => replacement.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task PrMonitorNextStep_InFlightWrite_DoesNotBlockDifferentPr()
    {
        _gh.PauseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var otherId = $"pr-{owner}-other-repo-42";
        using var session = new MonitorSession { State = CreateResolvingCommentState(owner) };
        var otherState = CreateCommentState(owner);
        MonitorTransitions.ProcessEvent(otherState, "user_chose", "apply_fix", null);
        using var other = new MonitorSession { State = otherState };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        Assert.True(sessions.TryAdd(otherId, other));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tools = new MonitorFlowTools();
        var original = tools.PrMonitorNextStep(monitorId, "comment_addressed", data: "{\"reply_text\":\"Approved reply\"}",
            server: new FakeElicitationMcpServer("stop"), cancellationToken: cancellation.Token);
        try
        {
            await _gh.WaitForResolveRequestsAsync(1);

            using var response = JsonDocument.Parse(await tools.PrMonitorNextStep(
                otherId, "ready", server: new FakeMcpServer()).WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("resume_pending_task", response.RootElement.GetProperty("task").GetString());
            Assert.False(original.IsCompleted);
            Assert.Equal(0, session.State.CurrentCommentIndex);
        }
        finally
        {
            cancellation.Cancel();
            _gh.ReleaseResolveRequests();
            await Record.ExceptionAsync(() => original.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(monitorId, out _);
            sessions.TryRemove(otherId, out _);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PrMonitorNextStep_MultiPrMemberStop_DoesNotStopOtherMembers(bool disposeMember, bool stopRemaining)
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        var firstId = $"pr-{owner}-first-repo-42";
        var secondId = $"pr-{owner}-second-repo-42";
        var firstState = new MonitorState
        {
            Owner = owner,
            Repo = "first-repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath
        };
        var prompt = MonitorTransitions.BuildTerminalAction(firstState, TerminalStateType.MergeConflict);
        var secondState = CreateCommentState(owner);
        secondState.Repo = "second-repo";
        MonitorTransitions.ProcessEvent(secondState, "user_chose", "apply_fix", null);
        using var first = new MonitorSession { State = firstState };
        using var second = new MonitorSession { State = secondState };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(firstId, first));
        Assert.True(sessions.TryAdd(secondId, second));
        var elicited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new FakeElicitationMcpServer("stop")
        {
            BeforeResponse = async (request, token) =>
            {
                Assert.Equal(prompt.Question, request.Message);
                elicited.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tools = new MonitorFlowTools();
        var combined = tools.PrMonitorNextStep("all", "ready", server: server, cancellationToken: cancellation.Token);
        try
        {
            await elicited.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (stopRemaining)
                tools.PrMonitorStop(secondId);
            if (disposeMember)
            {
                firstState.CurrentState = MonitorStateId.Stopped;
                first.Dispose();
            }
            else
            {
                tools.PrMonitorStop(firstId);
            }
            using var response = JsonDocument.Parse(await combined.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.Equal(stopRemaining ? "stop" : "execute", response.RootElement.GetProperty("action").GetString());
            if (!stopRemaining)
            {
                Assert.Equal("resume_pending_task", response.RootElement.GetProperty("task").GetString());
                Assert.Equal(secondId, response.RootElement.GetProperty("monitorId").GetString());
                Assert.False(second.IsStopped);
                Assert.Equal(MonitorStateId.ExecutingTask, secondState.CurrentState);
            }
            Assert.False(cancellation.IsCancellationRequested);
            Assert.Single(server.Elicitations);
            Assert.Empty(server.Requests);
            Assert.Empty(_gh.ReadRequests());
        }
        finally
        {
            cancellation.Cancel();
            await Record.ExceptionAsync(() => combined.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(firstId, out _);
            sessions.TryRemove(secondId, out _);
        }
    }

    [Theory]
    [InlineData(MonitorStateId.Idle)]
    [InlineData(MonitorStateId.Polling)]
    public async Task PrMonitorNextStep_MultiPrMembership_NewInitializationJoinsRunningPoll(MonitorStateId currentState)
    {
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        _gh.ReleaseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var existingId = $"pr-{owner}-existing-repo-42";
        var addedId = $"pr-{owner}-added-repo-42";
        using var existing = new MonitorSession
        {
            State = new MonitorState
            {
                Owner = owner,
                Repo = "existing-repo",
                PrNumber = 42,
                SessionFolder = _gh.DirectoryPath,
                CurrentState = currentState,
                HeadSha = "fixture-sha",
                PrAuthor = "fixture-author",
                CurrentUser = "fixture-author"
            }
        };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(existingId, existing));
        var addedState = new MonitorState { Owner = owner, Repo = "added-repo", PrNumber = 42, SessionFolder = _gh.DirectoryPath };
        File.WriteAllText(addedState.LogFile + ".viewer.pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tools = new MonitorFlowTools();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var combined = tools.PrMonitorNextStep("all", "ready", server: new FakeMcpServer(), cancellationToken: cancellation.Token);
        try
        {
            await WaitForStatusAsync(existing.State.LogFile, cancellation.Token);
            Assert.False(combined.IsCompleted);

            using var start = JsonDocument.Parse(await tools.PrMonitorStart(owner, "added-repo", 42, _gh.DirectoryPath));
            Assert.Equal(addedId, start.RootElement.GetProperty("monitor_id").GetString());
            try
            {
                await _gh.WaitForPrInfoRequestsAsync(owner, "added-repo", 2, cancellation.Token);
            }
            catch (TimeoutException)
            {
                var result = combined.IsCompletedSuccessfully ? await combined : combined.Status.ToString();
                var count = _gh.ReadStartupRequests().Count(request => request[1] == $"repos/{owner}/added-repo/pulls/42");
                var debug = File.Exists(addedState.DebugLogFile) ? File.ReadAllLines(addedState.DebugLogFile).TakeLast(8) : [];
                Assert.Fail($"Expected initialization plus polling; observed {count} PR-info request(s). Combined result: {result}; debug: {string.Join(Environment.NewLine, debug)}");
            }

            Assert.False(combined.IsCompleted);
            Assert.True(sessions[addedId].State.PollCount > 0);
            Assert.True(existing.State.PollCount > 0);
        }
        finally
        {
            cancellation.Cancel();
            await Record.ExceptionAsync(() => combined.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(existingId, out _);
            if (sessions.TryRemove(addedId, out var added))
            {
                added.StopPermanently();
                added.Dispose();
            }
        }
    }

    [Fact]
    public async Task PrMonitorNextStep_QueuedMemberStop_ExcludesStoppedMemberAfterAnotherWriteCompletes()
    {
        _gh.PauseResolveRequests();
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        _gh.ReleaseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var firstId = $"pr-{owner}-repo-42";
        var secondId = $"pr-{owner}-second-repo-42";
        var firstState = CreateResolvingCommentState(owner);
        firstState.UnresolvedComments.RemoveAt(1);
        var secondState = CreateCommentState(owner);
        secondState.Repo = "second-repo";
        MonitorTransitions.ProcessEvent(secondState, "user_chose", "apply_fix", null);
        using var first = new MonitorSession { State = firstState };
        using var second = new MonitorSession { State = secondState };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(firstId, first));
        Assert.True(sessions.TryAdd(secondId, second));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tools = new MonitorFlowTools();
        var original = tools.PrMonitorNextStep(firstId, "comment_addressed", data: "{\"reply_text\":\"Approved reply\"}",
            server: new FakeElicitationMcpServer("stop"), cancellationToken: cancellation.Token);
        Task<string>? combined = null;
        try
        {
            await _gh.WaitForResolveRequestsAsync(1);
            combined = tools.PrMonitorNextStep("all", "ready", server: new FakeMcpServer(), cancellationToken: cancellation.Token);

            tools.PrMonitorStop(secondId);

            Assert.False(original.IsCompleted);
            Assert.False(combined.IsCompleted);
            Assert.True(second.IsStopped);
            Assert.Equal(MonitorStateId.ExecutingTask, secondState.CurrentState);
            _gh.ReleaseResolveRequests();
            using var originalResult = JsonDocument.Parse(await original.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("stop", originalResult.RootElement.GetProperty("action").GetString());
            await _gh.WaitForPrInfoRequestsAsync(owner, "repo", 1, cancellation.Token);

            Assert.False(combined.IsCompleted);
            Assert.False(sessions.ContainsKey(secondId));
            Assert.Equal(0, secondState.PollCount);
            Assert.Equal(1, firstState.CurrentCommentIndex);
            Assert.DoesNotContain(_gh.ReadRequests(), request => request.Any(argument => argument.Contains("second-repo", StringComparison.Ordinal)));
            Assert.DoesNotContain(_gh.ReadStartupRequests(), request => request[1].Contains("second-repo", StringComparison.Ordinal));
            Assert.Single(_gh.ReadRequests(), request => request.Any(argument => argument.Contains("resolveReviewThread", StringComparison.Ordinal)));
        }
        finally
        {
            cancellation.Cancel();
            _gh.ReleaseResolveRequests();
            await Record.ExceptionAsync(() => original.WaitAsync(TimeSpan.FromSeconds(3)));
            if (combined != null)
                await Record.ExceptionAsync(() => combined.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(firstId, out _);
            sessions.TryRemove(secondId, out _);
        }
    }

    [Theory]
    [InlineData("fixture-sha", "rerun_via_browser")]
    [InlineData("older-sha", "investigate_ci_failure")]
    public async Task PrMonitorNextStep_AllPrs_ServicesDeferredRerun(string deferredOnSha, string expectedTask)
    {
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        _gh.RespondToPrInfo("""{"title":"Fixture PR","body":"","sha":"fixture-sha","head_branch":"fixture","base_branch":"main","url":"https://github.com/owner/repo/pull/42","author":"fixture-author","mergeable":true,"mergeable_state":"clean","state":"open","merged":false}""");
        _gh.ReleaseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        _gh.RespondToPages($"repos/{owner}/repo/commits/fixture-sha/check-runs",
            """{"check_runs":[{"name":"build","status":"completed","conclusion":"failure","details_url":"https://dev.azure.com/org/project/_build/results?buildId=1"}]}""");
        _gh.RespondToPages($"repos/{owner}/repo/commits/fixture-sha/status", """{"statuses":[]}""");
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            HeadBranch = "fixture",
            BaseBranch = "main",
            HeadSha = deferredOnSha,
            PrAuthor = "fixture-author",
            CurrentUser = "fixture-author",
            PendingRerunWhenChecksComplete = true
        };
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            using var response = JsonDocument.Parse(await new MonitorFlowTools().PrMonitorNextStep("all", "ready",
                server: new FakeElicitationMcpServer(), cancellationToken: cancellation.Token));

            Assert.Equal("execute", response.RootElement.GetProperty("action").GetString());
            Assert.Equal(expectedTask, response.RootElement.GetProperty("task").GetString());
            Assert.False(state.PendingRerunWhenChecksComplete);
        }
        finally
        {
            cancellation.Cancel();
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrMonitorNextStep_ChangedTargetBranch_UsesFreshTargetBranchForConflictResolution(bool monitorAll)
    {
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        _gh.RespondToPrInfo("""{"title":"Fixture PR","body":"","sha":"fixture-sha","head_branch":"feature/conflict-fix","base_branch":"release","url":"https://github.com/owner/repo/pull/42","author":"fixture-author","mergeable":false,"mergeable_state":"dirty","state":"open","merged":false}""");
        _gh.ReleaseStartupRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            HeadBranch = "feature/conflict-fix",
            BaseBranch = "main",
            HeadSha = "fixture-sha",
            PrAuthor = "fixture-author",
            CurrentUser = "fixture-author"
        };
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        var tools = new MonitorFlowTools();
        var server = new FakeElicitationMcpServer("merge_target_branch");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await tools.PrMonitorNextStep(monitorAll ? "all" : monitorId, "ready",
                server: server, cancellationToken: cancellation.Token);
            if (monitorAll)
            {
                using var relay = JsonDocument.Parse(result);
                Assert.Equal("relay_choice", relay.RootElement.GetProperty("task").GetString());
                result = await tools.PrMonitorNextStep(monitorId, "user_chose", choice: "merge_target_branch",
                    server: server, cancellationToken: cancellation.Token);
            }
            using var response = JsonDocument.Parse(result);

            Assert.Equal("merge_target_branch", response.RootElement.GetProperty("task").GetString());
            Assert.Equal("release", state.BaseBranch);
            Assert.Equal("release", response.RootElement.GetProperty("context").GetProperty("baseBranch").GetString());
            Assert.Contains("target branch 'release' into PR head branch 'feature/conflict-fix'",
                response.RootElement.GetProperty("instructions").GetString());
            Assert.Single(server.Elicitations);
            Assert.DoesNotContain(_gh.ReadRequests(), request => request.Contains("POST") || request.Contains("PATCH"));
        }
        finally
        {
            cancellation.Cancel();
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrMonitorNextStep_CompletedMerge_DoesNotReviveStoppedSessionOnReconnect(bool viewerTrigger)
    {
        _gh.PauseMergeRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            WaitingForReplyComments = [new CommentInfo { Id = "waiting", Author = "reviewer", Body = "Old waiting thread" }]
        };
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        var tools = new MonitorFlowTools();
        var replacementServer = new FakeElicitationMcpServer("stop");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var original = tools.PrMonitorNextStep(monitorId, "user_chose", choice: "merge",
            server: new FakeMcpServer(), cancellationToken: cancellation.Token);
        Task<string>? replacement = null;
        try
        {
            await _gh.WaitForMergeRequestsAsync(1);
            if (viewerTrigger)
                session.PendingTriggerContent = "ACTION|waiting";
            replacement = tools.PrMonitorNextStep(monitorId, "ready",
                server: replacementServer, cancellationToken: cancellation.Token);
            await Assert.ThrowsAsync<TimeoutException>(() => replacement.WaitAsync(TimeSpan.FromMilliseconds(200)));
            _gh.ReleaseMergeRequests();
            using var originalResult = JsonDocument.Parse(await original.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("stop", originalResult.RootElement.GetProperty("action").GetString());
            using var response = JsonDocument.Parse(await replacement.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.Equal("stop", response.RootElement.GetProperty("action").GetString());
            Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
            Assert.Empty(replacementServer.Elicitations);
            Assert.Empty(replacementServer.Requests);
            Assert.Null(state.PendingAction);
            Assert.Null(state.ActiveWaitingComment);
            Assert.Single(_gh.ReadRequests());
        }
        finally
        {
            cancellation.Cancel();
            _gh.ReleaseMergeRequests();
            await Record.ExceptionAsync(() => original.WaitAsync(TimeSpan.FromSeconds(3)));
            if (replacement != null)
                await Record.ExceptionAsync(() => replacement.WaitAsync(TimeSpan.FromSeconds(3)));
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
            second = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "", cancellationToken: cancellation.Token);
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
        var first = new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath, cancellationToken: cancellation.Token);
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

    private static async Task WaitForStatusAsync(string filename, CancellationToken cancellationToken)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (!File.Exists(filename))
                return;
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            if (reader.ReadToEnd().Contains("STATUS|", StringComparison.Ordinal))
                written.TrySetResult();
        }
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(filename)!, Path.GetFileName(filename));
        watcher.Created += (_, _) => Check();
        watcher.Changed += (_, _) => Check();
        watcher.EnableRaisingEvents = true;
        Check();
        await written.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrMonitorStart_AutoMode_HandsPendingPromptToAutoMode(bool monitorAll)
    {
        _gh.PauseMergeRequests();
        _gh.ReleaseMergeRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            Checks = new CheckRunCounts { Passed = 3, Total = 3 },
            Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }]
        };
        var prompt = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);
        Assert.Contains(MonitorTransitions.SwitchToAutoModeChoice, prompt.Choices!);
        var sessions = GetSessions();
        using var session = new MonitorSession { State = state };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var tools = new MonitorFlowTools();
            using var start = JsonDocument.Parse(await tools.PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath, mode: "auto"));
            using var response = JsonDocument.Parse(await tools.PrMonitorNextStep(
                monitorAll ? "all" : monitorId, "ready", server: server, cancellationToken: cancellation.Token));

            Assert.Equal("auto", start.RootElement.GetProperty("mode").GetString());
            Assert.Equal("merged", response.RootElement.GetProperty("action").GetString());
            Assert.Contains(_gh.ReadRequests(), request => request is ["pr", "merge", "42", "--squash", "--repo", _]);
            Assert.Empty(server.Elicitations);
            Assert.Equal(monitorAll, !sessions.ContainsKey(monitorId));
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task PrMonitorStart_UnknownMode_ReturnsErrorWithoutStarting()
    {
        var owner = $"fixture-{Guid.NewGuid():N}";

        using var response = JsonDocument.Parse(await new MonitorFlowTools().PrMonitorStart(owner, "repo", 42, "", mode: "manual"));

        Assert.Equal("error", response.RootElement.GetProperty("action").GetString());
        Assert.False(GetSessions().ContainsKey($"pr-{owner}-repo-42"));
        Assert.Empty(_gh.ReadRequests());
    }

    [Fact]
    public async Task PrMonitorNextStep_AutoResults_PostsOnlyVerifiedClaims()
    {
        _gh.PauseResolveRequests();
        _gh.ReleaseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        var state = CreateAutoCommentState(owner, "fixed", "pushback", "no-commit", "no-evidence");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        var sessions = GetSessions();
        using var session = new MonitorSession { State = state };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("handle_myself", "stop");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var data = JsonSerializer.Serialize(new
            {
                results = new object[]
                {
                    new { comment_id = "fixed", decision = "fixed", reply_text = "Added the guard", evidence = "GuardTests.RejectsNull failed before the fix", commit = $"{owner}/repo@abc1234" },
                    new { comment_id = "pushback", decision = "pushed_back", reply_text = "The guard already exists", evidence = "GuardTests.RejectsNull passes" },
                    new { comment_id = "no-commit", decision = "fixed", reply_text = "Added the check", evidence = "CheckTests failed before the fix" },
                    new { comment_id = "no-evidence", decision = "pushed_back", reply_text = "I think this is fine" }
                }
            });
            using var response = JsonDocument.Parse(await new MonitorFlowTools().PrMonitorNextStep(
                monitorId, "comments_processed", data: data, server: server, cancellationToken: cancellation.Token));

            Assert.Equal("stop", response.RootElement.GetProperty("action").GetString());
            var prompt = server.Elicitations[0].Message;
            Assert.Contains("Auto mode needs you on 2 comments", prompt);
            Assert.Contains("without the SHA of the commit", prompt);
            Assert.Contains("a pushback without evidence", prompt);
            var requests = _gh.ReadRequests();
            Assert.Equal(
                [$"repos/{owner}/repo/pulls/42/comments/1/replies", $"repos/{owner}/repo/pulls/42/comments/2/replies"],
                requests.Where(r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal)).Select(r => r[1]));
            var resolve = Assert.Single(requests, r => r.Any(arg => arg.Contains("resolveReviewThread", StringComparison.Ordinal)));
            Assert.Contains("threadId: \"fixed\"", resolve[^1]);
            Assert.Equal(["pushback"], state.WaitingForReplyComments.Select(c => c.Id));
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task AutoResultsApplier_ClaimsGitHubCannotConfirm_GoToUserWithoutReplies()
    {
        var owner = $"fixture-{Guid.NewGuid():N}";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        var state = CreateAutoCommentState(owner, "not-pushed", "no-issue");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        MonitorTransitions.ProcessEvent(state, "comments_processed", null, JsonSerializer.Serialize(new
        {
            results = new object[]
            {
                new { comment_id = "not-pushed", decision = "fixed", reply_text = "Added the check", evidence = "CheckTests failed before the fix", commit = "9999999" },
                new { comment_id = "no-issue", decision = "follow_up", reply_text = "Tracked separately", evidence = "The PR description limits it to renames", issue_url = $"{owner}/repo#7" }
            }
        }));

        await AutoResultsApplier.ApplyAsync(state, TimeSpan.Zero);

        Assert.All(state.PendingAutoResults, r => Assert.Equal(AutoCommentResult.NeedsUser, r.Decision));
        Assert.Contains("not on the PR branch", state.PendingAutoResults[0].Reason);
        Assert.Contains($"{owner}/repo#7 could not be found", state.PendingAutoResults[1].Reason);
        Assert.DoesNotContain(_gh.ReadRequests(), r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutoResultsApplier_ResolveFails_HandsOnlyHumanThreadsToUser()
    {
        _gh.PauseResolveRequests();
        _gh.ReleaseResolveRequests();
        _gh.FailResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        var state = CreateAutoCommentState(owner, "human", "bot");
        state.UnresolvedComments[1].Author = "copilot-pull-request-reviewer[bot]";
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        MonitorTransitions.ProcessEvent(state, "comments_processed", null, JsonSerializer.Serialize(new
        {
            results = new object[]
            {
                new { comment_id = "human", decision = "fixed", reply_text = "Added the guard", evidence = "GuardTests.RejectsNull failed before the fix", commit = "abc1234" },
                new { comment_id = "bot", decision = "fixed", reply_text = "Added the check", evidence = "CheckTests failed before the fix", commit = "abc1234" }
            }
        }));

        await AutoResultsApplier.ApplyAsync(state, TimeSpan.Zero);
        MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal(2, _gh.ReadRequests().Count(r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal)));
        Assert.Contains("already posted", Assert.Contains("human", state.AutoDeferred).Reason);
        Assert.DoesNotContain("bot", state.AutoDeferred.Keys);
    }

    [Fact]
    public async Task PrMonitorNextStep_AutoMode_ThreadSignedInUserRepliedToLastWaitsForReviewer()
    {
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        _gh.RespondToPrInfo("""{"title":"Fixture PR","body":"","sha":"fixture-sha","head_branch":"fixture","base_branch":"main","url":"https://github.com/owner/repo/pull/42","author":"fixture-author","mergeable":false,"mergeable_state":"dirty","state":"open","merged":false}""");
        _gh.ReleaseStartupRequests();
        _gh.RespondToPages("graphql", """
            {"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":"c"},"nodes":[
              {"id":"thread","isResolved":false,
               "comments":{"totalCount":2,"nodes":[{"author":{"login":"reviewer"},"authorAssociation":"MEMBER","body":"Please fix","url":"https://github.com/owner/repo/pull/42"}]},
               "lastReply":{"nodes":[{"author":{"login":"helper"},"authorAssociation":"COLLABORATOR","createdAt":"2026-11-18T18:49:30Z"}]}}]}}}}}
            """);
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = new MonitorState
        {
            Owner = owner,
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            HeadBranch = "fixture",
            BaseBranch = "main",
            HeadSha = "fixture-sha",
            PrAuthor = "fixture-author",
            CurrentUser = "helper",
            AutoMode = true
        };
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            using var response = JsonDocument.Parse(await new MonitorFlowTools().PrMonitorNextStep(
                monitorId, "ready", server: new FakeElicitationMcpServer(), cancellationToken: cancellation.Token));

            Assert.Equal(["thread"], state.WaitingForReplyComments.Select(c => c.Id));
            Assert.Equal("merge_target_branch", response.RootElement.GetProperty("task").GetString());
        }
        finally
        {
            cancellation.Cancel();
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task PrMonitorStop_DuringAutoResults_PostsNoFurtherReplies()
    {
        _gh.PauseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        var state = CreateAutoCommentState(owner, "first", "second");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tools = new MonitorFlowTools();
        var step = tools.PrMonitorNextStep(monitorId, "comments_processed", data: TwoFixedResults(),
            server: new FakeElicitationMcpServer(), cancellationToken: cancellation.Token);

        try
        {
            await _gh.WaitForResolveRequestsAsync(1);
            tools.PrMonitorStop(monitorId);
            _gh.ReleaseResolveRequests();
            using var result = JsonDocument.Parse(await step.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("stop", result.RootElement.GetProperty("action").GetString());
            Assert.Single(_gh.ReadRequests(), r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal));
        }
        finally
        {
            cancellation.Cancel();
            _gh.ReleaseResolveRequests();
            await Record.ExceptionAsync(() => step.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task PrMonitorStart_InteractiveDuringAutoResults_HandsRemainingRepliesToUser()
    {
        _gh.PauseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        var state = CreateAutoCommentState(owner, "first", "second");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        using var session = new MonitorSession { State = state };
        var sessions = GetSessions();
        Assert.True(sessions.TryAdd(monitorId, session));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tools = new MonitorFlowTools();
        var step = tools.PrMonitorNextStep(monitorId, "comments_processed", data: TwoFixedResults(),
            server: new FakeElicitationMcpServer("handle_myself", "stop"), cancellationToken: cancellation.Token);

        try
        {
            await _gh.WaitForResolveRequestsAsync(1);
            await tools.PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath, mode: "interactive");
            _gh.ReleaseResolveRequests();
            await Record.ExceptionAsync(() => step.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Single(_gh.ReadRequests(), r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal));
            Assert.Contains("turned off before it posted", Assert.Contains("second", state.AutoDeferred).Reason);
        }
        finally
        {
            cancellation.Cancel();
            _gh.ReleaseResolveRequests();
            await Record.ExceptionAsync(() => step.WaitAsync(TimeSpan.FromSeconds(3)));
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public async Task AutoResultsApplier_RepliesConfirmedAfterAutoModeTurnedOff_ArePosted()
    {
        _gh.PauseResolveRequests();
        _gh.ReleaseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        var state = CreateAutoCommentState(owner, "first", "second");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        state.AutoMode = false;
        MonitorTransitions.ProcessEvent(state, "comments_processed", null, TwoFixedResults());
        MonitorTransitions.ProcessEvent(state, "user_chose", "post_auto_results", null);

        await AutoResultsApplier.ApplyAsync(state, TimeSpan.Zero);

        Assert.Equal(2, _gh.ReadRequests().Count(r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AutoResultsApplier_ReviewerRepliedDuringWork_HandsThreadToUserWithoutPosting()
    {
        _gh.PauseResolveRequests();
        _gh.ReleaseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        _gh.RespondToPages($"repos/{owner}/repo/pulls/42/commits", """[{"sha":"abc1234def5678"}]""");
        _gh.RespondToPages("graphql", """
            {"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":"c"},"nodes":[
              {"id":"first","isResolved":false,
               "comments":{"totalCount":2,"nodes":[{"author":{"login":"reviewer"},"body":"Please fix","url":"https://github.com/owner/repo/pull/42"}]},
               "lastReply":{"nodes":[{"author":{"login":"reviewer"},"createdAt":"2026-11-18T18:49:30Z"}]}},
              {"id":"second","isResolved":false,
               "comments":{"totalCount":2,"nodes":[{"author":{"login":"reviewer"},"body":"Please fix","url":"https://github.com/owner/repo/pull/42"}]},
               "lastReply":{"nodes":[{"author":{"login":"author"},"createdAt":"2026-11-18T18:49:30Z"}]}}]}}}}}
            """);
        var state = CreateAutoCommentState(owner, "first", "second");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        MonitorTransitions.ProcessEvent(state, "comments_processed", null, TwoFixedResults());

        await AutoResultsApplier.ApplyAsync(state, TimeSpan.Zero);

        Assert.Equal([$"repos/{owner}/repo/pulls/42/comments/2/replies"],
            _gh.ReadRequests().Where(r => r.Length > 1 && r[1].EndsWith("/replies", StringComparison.Ordinal)).Select(r => r[1]));
        Assert.Contains("reviewer replied while auto mode was working", state.PendingAutoResults[0].Reason);
    }

    private static string TwoFixedResults() => JsonSerializer.Serialize(new
    {
        results = new object[]
        {
            new { comment_id = "first", decision = "fixed", reply_text = "Added the guard", evidence = "GuardTests.RejectsNull failed before the fix", commit = "abc1234" },
            new { comment_id = "second", decision = "fixed", reply_text = "Added the check", evidence = "CheckTests failed before the fix", commit = "abc1234" }
        }
    });

    [Fact]
    public async Task PrMonitorStart_InteractiveDuringAutoTask_PostsNothingWithoutConfirmation()
    {
        _gh.PauseResolveRequests();
        _gh.ReleaseResolveRequests();
        var owner = $"fixture-{Guid.NewGuid():N}";
        var monitorId = $"pr-{owner}-repo-42";
        var state = CreateAutoCommentState(owner, "c1");
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);
        var sessions = GetSessions();
        using var session = new MonitorSession { State = state };
        Assert.True(sessions.TryAdd(monitorId, session));
        var server = new FakeElicitationMcpServer("handle_myself", "stop");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var tools = new MonitorFlowTools();
            using var start = JsonDocument.Parse(await tools.PrMonitorStart(owner, "repo", 42, _gh.DirectoryPath, mode: "interactive"));
            var data = JsonSerializer.Serialize(new
            {
                results = new object[] { new { comment_id = "c1", decision = "answered", reply_text = "It runs once per poll", evidence = "PollLoopTests" } }
            });
            using var response = JsonDocument.Parse(await tools.PrMonitorNextStep(
                monitorId, "comments_processed", data: data, server: server, cancellationToken: cancellation.Token));

            Assert.Equal("interactive", start.RootElement.GetProperty("mode").GetString());
            Assert.Equal("stop", response.RootElement.GetProperty("action").GetString());
            Assert.Contains("Auto mode was turned off", Assert.IsType<string>(server.Elicitations.FirstOrDefault()?.Message));
            Assert.DoesNotContain(_gh.ReadRequests(), r => r.Length > 1 &&
                (r[1].EndsWith("/replies", StringComparison.Ordinal) || r[1].EndsWith("/requested_reviewers", StringComparison.Ordinal) ||
                 r.Any(arg => arg.Contains("resolveReviewThread", StringComparison.Ordinal))));
        }
        finally
        {
            sessions.TryRemove(monitorId, out _);
        }
    }

    [Fact]
    public void AutoResultsApplier_BuildReply_AddsIssueLinkAndFooterOnce()
    {
        var state = new MonitorState { Owner = "owner", Repo = "repo", CurrentUser = "me" };
        var result = new AutoCommentResult
        {
            Decision = AutoCommentResult.FollowUp,
            ReplyText = "Good idea, but out of scope here.",
            IssueUrl = "https://github.com/owner/repo/issues/7"
        };

        Assert.Equal("Good idea, but out of scope here.\n\nFollow-up: https://github.com/owner/repo/issues/7\n\n---\n🤖 me-copilot",
            AutoResultsApplier.BuildReply(state, result));

        result.ReplyText = "Tracked in #7.\n\n---\n🤖 me-copilot";
        Assert.Equal(result.ReplyText, AutoResultsApplier.BuildReply(state, result));
    }

    [Theory]
    [InlineData("https://github.com/other/lib/issues/7", "Tracked in #7.", true)]
    [InlineData("https://github.com/other/lib/issues/7", "Tracked in other/lib#7.", false)]
    [InlineData("https://github.com/owner/repo/issues/7", "Tracked in other/lib#7.", true)]
    public void AutoResultsApplier_BuildReply_AppendsIssueLinkUnlessReplyLinksThatIssue(string issueUrl, string replyText, bool appendsLink)
    {
        var state = new MonitorState { Owner = "owner", Repo = "repo" };
        var result = new AutoCommentResult { Decision = AutoCommentResult.FollowUp, ReplyText = replyText, IssueUrl = issueUrl };

        Assert.Equal(appendsLink, AutoResultsApplier.BuildReply(state, result).Contains($"Follow-up: {issueUrl}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergePr_RequiresTheEvaluatedHeadCommit()
    {
        _gh.PauseMergeRequests();
        _gh.ReleaseMergeRequests();
        var state = new MonitorState { Owner = "owner", Repo = "repo", PrNumber = 42, SessionFolder = _gh.DirectoryPath, HeadSha = "evaluated-sha" };

        var action = await ExecuteAutoActionAsync(state, new MonitorAction { Action = "auto_execute", Task = "merge_pr" });

        Assert.Equal("merged", action.Action);
        Assert.Contains(_gh.ReadRequests(), request =>
            request is ["pr", "merge", "42", "--squash", "--repo", "owner/repo", "--match-head-commit", "evaluated-sha"]);
    }

    [Theory]
    [InlineData("merge_pr")]
    [InlineData("merge_pr_admin")]
    public async Task MergePr_HeadMovedSinceEvaluation_PollsAgainInsteadOfPrompting(string task)
    {
        _gh.PauseStartupRequests();
        _gh.CompleteStartupSuccessfully();
        _gh.ReleaseStartupRequests();
        _gh.RespondToPrInfo("""{"title":"Fixture PR","body":"","sha":"newer-sha","head_branch":"fixture","base_branch":"main","url":"https://github.com/owner/repo/pull/42","author":"fixture-author","mergeable":true,"mergeable_state":"clean","state":"open","merged":false}""");
        var state = new MonitorState { Owner = "owner", Repo = "repo", PrNumber = 42, SessionFolder = _gh.DirectoryPath, HeadSha = "evaluated-sha" };

        var action = await ExecuteAutoActionAsync(state, new MonitorAction { Action = "auto_execute", Task = task });

        Assert.Equal("polling", action.Action);
    }

    [Fact]
    public async Task ReRequestStaleReviews_GitHubRefuses_AsksUserWithTheReason()
    {
        var state = new MonitorState
        {
            Owner = "owner",
            Repo = "repo",
            PrNumber = 42,
            SessionFolder = _gh.DirectoryPath,
            HeadSha = "head-sha",
            AutoMode = true,
            StaleApprovals = [new ReviewInfo { Author = "outsider", State = "APPROVED", IsStale = true }]
        };
        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.StaleApprovalCiGreen);

        var action = await ExecuteAutoActionAsync(state, new MonitorAction { Action = "auto_execute", Task = "re_request_stale_reviews" });

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("outsider: Unexpected GitHub CLI command in offline test.", action.Question);
        Assert.Contains("approval from outsider is stale", action.Question);
    }

    [Theory]
    [InlineData(true, "copilot-pull-request-reviewer[bot]", false)]
    [InlineData(true, "reviewer", true)]
    [InlineData(false, "copilot-pull-request-reviewer[bot]", true)]
    public async Task ReRequestCatchUp_InAutoMode_SkipsBotReviewers(bool autoMode, string reviewer, bool reRequested)
    {
        _gh.RespondToPages("repos/owner/repo/pulls/42/requested_reviewers", "[]");
        var state = new MonitorState { Owner = "owner", Repo = "repo", PrNumber = 42, PrAuthor = "author", CurrentUser = "author", AutoMode = autoMode };
        var method = typeof(MonitorFlowTools).GetMethod("ReRequestReviewForFullyRepliedReviewersAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        List<CommentInfo> waiting = [new CommentInfo { Id = "thread", Author = reviewer, IsWaitingForReply = true }];

        await (Task)method.Invoke(null, [state, waiting, new List<CommentInfo>()])!;

        Assert.Equal(reRequested, _gh.ReadRequests().Any(r => r.Contains("POST") && r[1].EndsWith("/requested_reviewers", StringComparison.Ordinal)));
    }

    private static async Task<MonitorAction> ExecuteAutoActionAsync(MonitorState state, MonitorAction action)
    {
        var method = typeof(MonitorFlowTools).GetMethod("ExecuteAutoAction", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<MonitorAction>)method.Invoke(null, [state, action, CancellationToken.None])!;
    }

    private MonitorState CreateAutoCommentState(string owner, params string[] commentIds) => new()
    {
        Owner = owner,
        Repo = "repo",
        PrNumber = 42,
        SessionFolder = _gh.DirectoryPath,
        PrAuthor = "author",
        CurrentUser = "author",
        AutoMode = true,
        UnresolvedComments = [.. commentIds.Select((id, i) => new CommentInfo { Id = id, Author = "reviewer", AuthorAssociation = "MEMBER", RestCommentId = i + 1 })]
    };

    private MonitorState CreateCommentState(string owner) => new()
    {
        Owner = owner,
        Repo = "repo",
        PrNumber = 42,
        SessionFolder = _gh.DirectoryPath,
        CurrentState = MonitorStateId.AwaitingUser,
        CommentFlow = CommentFlowState.ExplainAllIterating,
        CurrentCommentIndex = 1,
        LastRecommendation = "Preserve the completion marker",
        UnresolvedComments =
        [
            new CommentInfo { Id = "first", Author = "reviewer", Body = "First comment" },
            new CommentInfo { Id = "second", Author = "reviewer", Body = "Preserve the completion marker" }
        ]
    };

    private MonitorState CreateResolvingCommentState(string owner) => new()
    {
        Owner = owner,
        Repo = "repo",
        PrNumber = 42,
        SessionFolder = _gh.DirectoryPath,
        CurrentState = MonitorStateId.ExecutingTask,
        CommentFlow = CommentFlowState.AddressAllIterating,
        LastRecommendation = "Original recommendation",
        ReviewsReRequested = ["reviewer"],
        UnresolvedComments =
        [
            new CommentInfo { Id = "first", Author = "reviewer", Body = "First comment", RestCommentId = 1 },
            new CommentInfo { Id = "second", Author = "reviewer", Body = "Second comment", RestCommentId = 2 }
        ]
    };

    private static async Task<bool> HandleViaSamplingAsync(FakeSamplingMcpServer server, MonitorState state, string taskName)
    {
        var method = typeof(MonitorFlowTools).GetMethod("TryHandleViaSamplingAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var action = new MonitorAction
        {
            Action = "execute",
            Task = taskName,
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
