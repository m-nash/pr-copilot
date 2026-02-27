// Licensed under the MIT License.

using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

public class StateMachineTests
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

    private static CommentInfo MakeComment(string id = "c1", string author = "reviewer1", string file = "src/File.cs") => new()
    {
        Id = id,
        Author = author,
        FilePath = file,
        Line = 10,
        Body = "Please fix this",
        Url = "https://github.com/test/pr/42#comment"
    };

    private static void SetChecksAllGreen(MonitorState state, int total = 5)
    {
        state.Checks = new CheckRunCounts { Passed = total, Total = total };
    }

    private static void SetChecksFailed(MonitorState state, int passed = 3, int failed = 2)
    {
        state.Checks = new CheckRunCounts { Passed = passed, Failed = failed, Total = passed + failed };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];
    }

    #region DetectTerminalState

    [Fact]
    public void DetectTerminalState_NewComment_ReturnsNewComment()
    {
        var state = CreateState();
        var comments = new List<CommentInfo> { MakeComment() };

        var result = MonitorTransitions.DetectTerminalState(state, comments, false);

        Assert.Equal(TerminalStateType.NewComment, result);
    }

    [Fact]
    public void DetectTerminalState_MergeConflict_ReturnsMergeConflict()
    {
        var state = CreateState();

        var result = MonitorTransitions.DetectTerminalState(state, [], true);

        Assert.Equal(TerminalStateType.MergeConflict, result);
    }

    [Fact]
    public void DetectTerminalState_CiFailure_ReturnsCiFailure()
    {
        var state = CreateState();
        SetChecksFailed(state);

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.CiFailure, result);
    }

    [Fact]
    public void DetectTerminalState_CiFailure_EvenWhenApproved()
    {
        var state = CreateState();
        SetChecksFailed(state);
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.CiFailure, result);
    }

    [Fact]
    public void DetectTerminalState_CiCancelled_ReturnsCiCancelled()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 3, Cancelled = 1, Total = 4 };

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.CiCancelled, result);
    }

    [Fact]
    public void DetectTerminalState_ApprovedCiGreen_ReturnsApprovedCiGreen()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.ApprovedCiGreen, result);
    }

    [Fact]
    public void DetectTerminalState_CiPassedWithIgnoredComments_ReturnsCiPassedCommentsIgnored()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.IgnoredCommentIds = ["c1", "c2"];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.CiPassedCommentsIgnored, result);
    }

    [Fact]
    public void DetectTerminalState_ChecksPending_ReturnsNull()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 3, Pending = 2, Total = 5 };
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    [Fact]
    public void DetectTerminalState_ChecksQueued_ReturnsNull()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 3, Queued = 2, Total = 5 };
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    [Fact]
    public void DetectTerminalState_CommentWinsOverCiFailure()
    {
        var state = CreateState();
        SetChecksFailed(state);
        var comments = new List<CommentInfo> { MakeComment() };

        var result = MonitorTransitions.DetectTerminalState(state, comments, false);

        Assert.Equal(TerminalStateType.NewComment, result);
    }

    [Fact]
    public void DetectTerminalState_CommentWinsOverMergeConflict()
    {
        var state = CreateState();
        var comments = new List<CommentInfo> { MakeComment() };

        var result = MonitorTransitions.DetectTerminalState(state, comments, true);

        Assert.Equal(TerminalStateType.NewComment, result);
    }

    [Fact]
    public void DetectTerminalState_CiFailureWinsOverApprovedGreen()
    {
        var state = CreateState();
        SetChecksFailed(state);
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.CiFailure, result);
    }

    [Fact]
    public void DetectTerminalState_MergeConflictWinsOverCiFailure()
    {
        var state = CreateState();
        SetChecksFailed(state);

        var result = MonitorTransitions.DetectTerminalState(state, [], true);

        Assert.Equal(TerminalStateType.MergeConflict, result);
    }

    [Fact]
    public void DetectTerminalState_NeedsAdditionalApproval_BlocksApprovedCiGreen()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];
        state.NeedsAdditionalApproval = true;
        state.ApprovalCountAtMergeFailure = 1;

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    [Fact]
    public void DetectTerminalState_NeedsAdditionalApproval_AllowsWhenMoreApprovals()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals =
        [
            new ReviewInfo { Author = "approver1", State = "APPROVED" },
            new ReviewInfo { Author = "approver2", State = "APPROVED" }
        ];
        state.NeedsAdditionalApproval = true;
        state.ApprovalCountAtMergeFailure = 1;

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.ApprovedCiGreen, result);
    }

    [Fact]
    public void DetectTerminalState_NoTerminalConditions_ReturnsNull()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 3, Pending = 2, Total = 5 };

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    #endregion

    #region BuildTerminalAction

    [Fact]
    public void BuildTerminalAction_SetsLastTerminalStateAndAwaitingUser()
    {
        var state = CreateState();
        SetChecksFailed(state);

        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiFailure);

        Assert.Equal(TerminalStateType.CiFailure, state.LastTerminalState);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
    }

    [Fact]
    public void BuildTerminalAction_SingleComment_SetsSingleCommentPrompt()
    {
        var state = CreateState();
        state.UnresolvedComments = [MakeComment()];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.SingleCommentPrompt, state.CommentFlow);
        Assert.Contains("new comment", action.Question, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(action.Choices);
        Assert.Equal(3, action.Choices.Count);
    }

    [Fact]
    public void BuildTerminalAction_MultipleComments_SetsMultiCommentPrompt()
    {
        var state = CreateState();
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.MultiCommentPrompt, state.CommentFlow);
        Assert.Contains("2 unresolved comments", action.Question);
        Assert.NotNull(action.Choices);
        Assert.Contains("Address all comments", action.Choices);
        Assert.Contains("Address a specific comment", action.Choices);
    }

    [Fact]
    public void BuildTerminalAction_CiFailure_ReturnsCiFailurePrompt()
    {
        var state = CreateState();
        SetChecksFailed(state);

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiFailure);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CiFailureFlowState.CiFailurePrompt, state.CiFailureFlow);
        Assert.Contains("CI failures", action.Question);
        Assert.NotNull(action.Choices);
        Assert.Contains("Investigate the failures", action.Choices);
    }

    [Fact]
    public void BuildTerminalAction_MergeConflict_ReturnsAskUser()
    {
        var state = CreateState();

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.MergeConflict);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("merge conflict", action.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTerminalAction_ApprovedCiGreen_ShowsApprovers()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("alice", action.Question);
        Assert.Contains("approved", action.Question, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(action.Choices);
        Assert.Contains("Merge the PR", action.Choices);
    }

    [Fact]
    public void BuildTerminalAction_CiCancelled_ReturnsAskUser()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 3, Cancelled = 1, Total = 4 };

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiCancelled);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("cancelled", action.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTerminalAction_CiPassedIgnored_ReturnsAskUser()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.IgnoredCommentIds = ["c1"];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiPassedCommentsIgnored);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("CI is green", action.Question);
        Assert.Contains("ignored", action.Question, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region ProcessEvent — Core Transitions

    [Theory]
    [InlineData(MonitorStateId.Idle)]
    [InlineData(MonitorStateId.Polling)]
    public void ProcessEvent_Ready_TransitionsToPolling(MonitorStateId startState)
    {
        var state = CreateState();
        state.CurrentState = startState;

        var action = MonitorTransitions.ProcessEvent(state, "ready", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UnknownStateEvent_ReturnsRecoveryAction()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.Stopped;

        var action = MonitorTransitions.ProcessEvent(state, "some_random_event", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Unexpected state", action.Question);
        Assert.NotNull(action.Choices);
    }

    [Fact]
    public void ProcessEvent_TaskCompleteFromAwaitingUser_RecoveryResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
    }

    #endregion

    #region ProcessEvent — Comment Flow Choices

    [Fact]
    public void ProcessEvent_UserChoice_HandleMyself_CommentFlow_StopsMonitoring()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "handle_myself", null);

        // handle_myself in comment flow ignores comments and resumes polling
        Assert.Equal("polling", action.Action);
        Assert.Contains("c1", state.IgnoredCommentIds);
    }

    [Fact]
    public void ProcessEvent_UserChoice_AddressAll_BeginsIteration()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2")];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "address_all", null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.AddressAllIterating, state.CommentFlow);
        Assert.Equal(0, state.CurrentCommentIndex);
    }

    [Fact]
    public void ProcessEvent_UserChoice_AddressSpecific_ShowsPickList()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2")];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "address_specific", null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.PickComment, state.CommentFlow);
        Assert.Contains("Which comment", action.Question);
    }

    [Fact]
    public void ProcessEvent_UserChoice_Address_SingleComment_ExecutesTask()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "address", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("address_comment", action.Task);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_Explain_SingleComment_ExecutesExplainTask()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("explain_comment", action.Task);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    #endregion

    #region ProcessEvent — CI Failure Flow Choices

    [Fact]
    public void ProcessEvent_UserChoice_Investigate_CiFlow_BeginsInvestigation()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "investigate", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("investigate_ci_failure", action.Task);
        Assert.Equal(MonitorStateId.Investigating, state.CurrentState);
        Assert.Equal(CiFailureFlowState.Investigating, state.CiFailureFlow);
    }

    [Fact]
    public void ProcessEvent_UserChoice_RerunFailed_CiFlow_ExecutesRerun()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("rerun_via_browser", action.Task);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_HandleMyself_CiFlow_Stops()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "handle_myself", null);

        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
    }

    #endregion

    #region ProcessEvent — Top-Level Choices (No Sub-Flow)

    [Fact]
    public void ProcessEvent_UserChoice_HandleMyself_NoFlow_Stops()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "handle_myself", null);

        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_Resume_NoFlow_ResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "resume", null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_Merge_ReturnsAutoExecute()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "merge", null);

        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("merge_pr", action.Task);
    }

    [Fact]
    public void ProcessEvent_UserChoice_Investigate_NoFlow_BeginsInvestigation()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "investigate", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("investigate_ci_failure", action.Task);
        Assert.Equal(MonitorStateId.Investigating, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_RerunFailed_NoFlow_ExecutesRerun()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun_failed", null);

        // Top-level choice "rerun_failed" goes through BuildRerunAction
        Assert.Equal("execute", action.Action);
        Assert.Equal("rerun_via_browser", action.Task);
    }

    #endregion

    #region ProcessEvent — Investigation Complete & Apply Fix

    [Fact]
    public void ProcessEvent_InvestigationComplete_TransitionsToAwaitingUser()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.Investigating;
        state.CiFailureFlow = CiFailureFlowState.Investigating;

        var action = MonitorTransitions.ProcessEvent(state, "investigation_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
        Assert.Equal(CiFailureFlowState.InvestigationResults, state.CiFailureFlow);
    }

    [Fact]
    public void ProcessEvent_PushCompleted_ResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ApplyingFix;

        var action = MonitorTransitions.ProcessEvent(state, "push_completed", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
    }

    #endregion

    #region ProcessEvent — Comment Addressed Flow

    [Fact]
    public void ProcessEvent_CommentAddressed_AddressAllIterating_AdvancesToNext()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2")];
        state.CurrentCommentIndex = 0;

        // First: auto-resolve the addressed thread
        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);
        Assert.True(state.PendingResolveAfterAddress);

        // Then: task_complete from resolve advances to next comment
        var action2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("ask_user", action2.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
        Assert.Equal(1, state.CurrentCommentIndex);
    }

    [Fact]
    public void ProcessEvent_CommentAddressed_LastComment_ResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        // First: auto-resolve
        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);

        // Then: task_complete resumes polling (last comment)
        var action2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("polling", action2.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_CommentAddressed_SingleComment_ShowsRemaining()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2")];
        state.CurrentCommentIndex = 0;

        // First: auto-resolve
        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);

        // Then: task_complete shows remaining
        var action2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("ask_user", action2.Action);
        Assert.Equal(CommentFlowState.PickRemaining, state.CommentFlow);
        Assert.Contains("1 more", action2.Question);
    }

    #endregion

    #region ProcessEvent — WaitForAdditionalApprover

    [Fact]
    public void ProcessEvent_UserChoice_WaitForApprover_SetsFlag()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.Approvals = [new ReviewInfo { Author = "approver1", State = "APPROVED" }];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "wait_for_approver", null);

        Assert.True(state.NeedsAdditionalApproval);
        Assert.Equal(1, state.ApprovalCountAtMergeFailure);
        Assert.Equal("polling", action.Action);
    }

    #endregion

    #region ProcessEvent — Explain Then Apply Flow

    [Fact]
    public void ExplainFlow_AfterExplainTaskComplete_ShowsApplyAsFirstChoice()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        // User chooses "explain" → agent completes
        MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);
        var postExplainAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", postExplainAction.Action);
        Assert.Equal("Apply the recommendation", postExplainAction.Choices![0]);
    }

    [Fact]
    public void ExplainFlow_AfterExplainTaskComplete_OnlyTwoChoices()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);
        var postExplainAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal(2, postExplainAction.Choices!.Count);
        Assert.DoesNotContain("Explain and suggest what to do", postExplainAction.Choices!);
    }

    [Fact]
    public void ExplainFlow_ApplyAfterExplain_ExecutesAddressTask()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        // explain → task_complete → user chooses apply
        MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);
        MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        var applyAction = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);

        Assert.Equal("execute", applyAction.Action);
        Assert.Equal("address_comment", applyAction.Task);
    }

    #endregion
}
