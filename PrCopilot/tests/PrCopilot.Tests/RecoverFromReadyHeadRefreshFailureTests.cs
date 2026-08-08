// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Tests for the recover_from_ready auto-exec path when HEAD refresh fails
/// (FetchPrInfoAsync throws) or when HeadShaAtTaskStart is missing. In those
/// cases we cannot determine whether the task pushed a new commit, and surfacing
/// the standard "finished without a new commit" prompt is misleading — the user
/// might have actually pushed but we just couldn't verify.
///
/// See: BuildRecoverFromReadyResolution(state, headAdvanced, headRefreshFailed).
/// </summary>
public class RecoverFromReadyHeadRefreshFailureTests
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

    private static CommentInfo MakeComment(string id = "c1") => new()
    {
        Id = id,
        Author = "reviewer1",
        FilePath = "src/File.cs",
        Line = 10,
        Body = "Please fix this"
    };

    [Fact]
    public void HeadRefreshFailed_NoFlow_ShowsUncertaintyPrompt_NotNoCommitPrompt()
    {
        // Generic recovery (no comment/CI flow, no waiting comment). When refresh
        // failed we don't know whether HEAD advanced — must NOT claim "finished
        // without a new commit".
        var state = CreateState();
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(
            state, headAdvanced: false, headRefreshFailed: true);

        Assert.Equal("ask_user", action.Action);
        Assert.NotNull(action.Question);
        Assert.DoesNotContain("finished without a new commit", action.Question!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't", action.Question!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeadRefreshFailed_CommentFlow_ShowsUncertaintyPrompt_WithCommentChoices()
    {
        var state = CreateState();
        state.UnresolvedComments.Add(MakeComment());
        state.CurrentCommentIndex = 0;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(
            state, headAdvanced: false, headRefreshFailed: true);

        Assert.Equal("ask_user", action.Action);
        Assert.DoesNotContain("finished without a new commit", action.Question!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't", action.Question!, StringComparison.OrdinalIgnoreCase);
        // Still surfaces the comment-flow choices so user can mark addressed/skip/etc.
        Assert.NotNull(action.Choices);
        Assert.Contains("Treat as comment addressed", action.Choices!);
        Assert.Contains("Treat as replied externally", action.Choices!);
    }

    [Fact]
    public void HeadRefreshFailed_CiFailureFlow_ShowsUncertaintyPrompt_WithCiChoices()
    {
        var state = CreateState();
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(
            state, headAdvanced: false, headRefreshFailed: true);

        Assert.Equal("ask_user", action.Action);
        Assert.DoesNotContain("finished without a new commit", action.Question!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't", action.Question!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(action.Choices);
        Assert.Contains("Treat as push completed", action.Choices!);
    }

    [Fact]
    public void HeadShaAtTaskStartMissing_TreatedAsRefreshFailure()
    {
        // If we never captured a snapshot (HeadShaAtTaskStart is null/empty) we
        // also cannot determine headAdvanced — same uncertainty prompt should apply.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.HeadShaAtTaskStart = null;

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(
            state, headAdvanced: false, headRefreshFailed: false);

        Assert.Equal("ask_user", action.Action);
        Assert.DoesNotContain("finished without a new commit", action.Question!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't", action.Question!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeadRefreshSucceeded_NoAdvance_StillShowsNoCommitPrompt_Regression()
    {
        // The success path (refresh worked, HEAD didn't advance) must still show
        // the existing "finished without a new commit" prompt. Don't regress.
        var state = CreateState();
        state.EnterExecutingTask();

        var action = MonitorTransitions.BuildRecoverFromReadyResolution(
            state, headAdvanced: false, headRefreshFailed: false);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("finished without a new commit", action.Question!,
            StringComparison.OrdinalIgnoreCase);
    }
}
