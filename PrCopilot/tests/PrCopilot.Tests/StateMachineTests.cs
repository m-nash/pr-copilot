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
    public void DetectTerminalState_ChecksInProgress_ReturnsNull()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 3, InProgress = 2, Total = 5 };
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    [Fact]
    public void DetectTerminalState_LegacyPendingOnly_DoesNotBlockTerminal()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 4, Pending = 1, Total = 5 };
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        // Legacy pending (e.g. policy checks) should NOT block terminal state detection
        Assert.Equal(TerminalStateType.ApprovedCiGreen, result);
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

    [Fact]
    public void DetectTerminalState_CiPassedNoIgnoredComments_NoApproval_ReturnsNull()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        // CI green, no approvals, no ignored comments — keep monitoring

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    [Fact]
    public void DetectTerminalState_ReviewerRepliedToWaitingComment_ReturnsReviewerReplied()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        var comment = MakeComment("c1");
        comment.IsWaitingForReply = false; // was waiting, reviewer replied
        comment.LastReplyAuthor = "reviewer1";
        state.WaitingForReplyComments = [comment];
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.ReviewerReplied, result);
        Assert.Equal(comment, state.RepliedComment);
    }

    [Fact]
    public void DetectTerminalState_PrAuthorRepliedToWaitingComment_DoesNotTrigger()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        var comment = MakeComment("c1");
        comment.IsWaitingForReply = false;
        comment.LastReplyAuthor = "pr-author"; // PR author replied, not a reviewer
        state.WaitingForReplyComments = [comment];
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.NotEqual(TerminalStateType.ReviewerReplied, result);
    }

    [Fact]
    public void DetectTerminalState_CommentStillWaitingForReply_DoesNotTrigger()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        var comment = MakeComment("c1");
        comment.IsWaitingForReply = true; // still waiting
        comment.LastReplyAuthor = "current-user";
        state.WaitingForReplyComments = [comment];
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.NotEqual(TerminalStateType.ReviewerReplied, result);
    }

    [Fact]
    public void BuildReviewerRepliedAction_NullRepliedComment_FallsBackToPolling()
    {
        var state = CreateState();
        state.RepliedComment = null;

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ReviewerReplied);

        Assert.Equal("polling", action.Action);
    }

    #endregion

    #region BuildTerminalAction

    [Fact]
    public void BuildTerminalAction_SetsLastTerminalStateAndAwaitingUser()
    {
        var state = CreateState();
        state.Checks = new CheckRunCounts { Passed = 5, Cancelled = 1, Total = 6 };

        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiCancelled);

        Assert.Equal(TerminalStateType.CiCancelled, state.LastTerminalState);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
    }

    [Fact]
    public void BuildTerminalAction_CiFailure_SetsLastTerminalState()
    {
        var state = CreateState();
        SetChecksFailed(state);

        MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiFailure);

        Assert.Equal(TerminalStateType.CiFailure, state.LastTerminalState);
        Assert.Equal(MonitorStateId.Investigating, state.CurrentState);
    }

    [Fact]
    public void BuildTerminalAction_SingleComment_AutoExplains()
    {
        var state = CreateState();
        state.UnresolvedComments = [MakeComment()];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.NewComment);

        Assert.Equal("execute", action.Action);
        Assert.Equal("explain_comment", action.Task);
        Assert.Equal(CommentFlowState.SingleCommentPrompt, state.CommentFlow);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
        Assert.True(state.PendingExplainResult);
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
    public void BuildTerminalAction_CiFailure_AutoInvestigates()
    {
        var state = CreateState();
        SetChecksFailed(state);

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiFailure);

        Assert.Equal("execute", action.Action);
        Assert.Equal("investigate_ci_failure", action.Task);
        Assert.Equal(MonitorStateId.Investigating, state.CurrentState);
        Assert.Equal(CiFailureFlowState.Investigating, state.CiFailureFlow);
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
    public void BuildTerminalAction_ApprovedCiGreen_IncludesWaitForAnotherApprover()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.NotNull(action.Choices);
        Assert.Contains("Wait for another approver", action.Choices);
    }

    [Fact]
    public void BuildTerminalAction_ApprovedCiGreen_ChoicesHaveMappedValues()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.NotNull(action.Choices);
        // Verify each choice has an entry in ChoiceValueMap (used by ElicitationHelper)
        Assert.True(MonitorTransitions.ChoiceValueMap.ContainsKey("Merge the PR"));
        Assert.Equal("merge", MonitorTransitions.ChoiceValueMap["Merge the PR"]);
        Assert.Equal("wait_for_approver", MonitorTransitions.ChoiceValueMap["Wait for another approver"]);
        Assert.Equal("resume", MonitorTransitions.ChoiceValueMap["Resume monitoring"]);
        Assert.Equal("handle_myself", MonitorTransitions.ChoiceValueMap["I'll handle it myself"]);
    }

    [Theory]
    [InlineData(TerminalStateType.ApprovedCiGreen)]
    [InlineData(TerminalStateType.CiCancelled)]
    [InlineData(TerminalStateType.MergeConflict)]
    public void BuildTerminalAction_AllStates_AllChoicesExistInValueMap(TerminalStateType terminal)
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
        if (terminal == TerminalStateType.CiCancelled)
            state.Checks = new CheckRunCounts { Passed = 5, Cancelled = 1, Total = 6 };

        var action = MonitorTransitions.BuildTerminalAction(state, terminal);

        Assert.NotNull(action.Choices);
        foreach (var choice in action.Choices)
            Assert.True(MonitorTransitions.ChoiceValueMap.ContainsKey(choice), $"Choice '{choice}' missing from ChoiceValueMap");
    }

    [Fact]
    public void AllActionBuilders_EveryChoiceString_ExistsInChoiceValueMap()
    {
        // Collect all choice strings produced by every action builder.
        // This catches drift between display text and ChoiceValueMap.
        var allChoices = new HashSet<string>();
        var state = CreateState();

        // Terminal states
        foreach (var terminal in Enum.GetValues<TerminalStateType>())
        {
            ResetState(state);
            SetChecksAllGreen(state);
            state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
            if (terminal == TerminalStateType.CiFailure)
            {
                SetChecksFailed(state);
                continue; // CiFailure auto-investigates, covered below
            }
            if (terminal == TerminalStateType.CiCancelled)
                state.Checks = new CheckRunCounts { Passed = 5, Cancelled = 1, Total = 6 };
            if (terminal == TerminalStateType.NewComment)
            {
                state.UnresolvedComments = [MakeComment()];
                continue; // NewComment triggers comment flow, covered below
            }
            if (terminal == TerminalStateType.ReviewerReplied)
            {
                state.RepliedComment = MakeComment();
            }

            var action = MonitorTransitions.BuildTerminalAction(state, terminal);
            if (action.Choices != null) allChoices.UnionWith(action.Choices);
        }

        // Single comment flow — now auto-explains, so we get execute action (no choices)
        // but after explain completes, post-explain choices appear
        ResetState(state);
        state.UnresolvedComments = [MakeComment()];
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.PendingExplainResult = true;
        var postExplainAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        if (postExplainAction.Choices != null) allChoices.UnionWith(postExplainAction.Choices);

        // Multi comment flow
        ResetState(state);
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CurrentState = MonitorStateId.Polling;
        // Trigger NewComment via ProcessEvent
        var multiAction = MonitorTransitions.ProcessEvent(state, "ready", null, null);
        if (multiAction.Choices != null) allChoices.UnionWith(multiAction.Choices);

        // Address-all iterating flow
        ResetState(state);
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.CurrentState = MonitorStateId.AwaitingUser;
        var addressAllAction = MonitorTransitions.ProcessEvent(state, "user_chose", "address_all", null);
        if (addressAllAction.Choices != null) allChoices.UnionWith(addressAllAction.Choices);

        // Pick-comment flow (numbered choices are dynamic — only capture the static "I'll handle them myself")
        ResetState(state);
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.CurrentState = MonitorStateId.AwaitingUser;
        var pickAction = MonitorTransitions.ProcessEvent(state, "user_chose", "address_specific", null);
        if (pickAction.Choices != null)
        {
            foreach (var c in pickAction.Choices)
            {
                // Skip numbered dynamic choices (e.g., "1. reviewer1: ...")
                if (!char.IsDigit(c[0]))
                    allChoices.Add(c);
            }
        }

        // PickRemaining flow (Address next comment / I'll handle the rest myself)
        ResetState(state);
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.CurrentCommentIndex = 0;
        state.CurrentState = MonitorStateId.AwaitingUser;
        var remainingAction = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        if (remainingAction.Choices != null) allChoices.UnionWith(remainingAction.Choices);

        // Waiting-for-reply comment
        ResetState(state);
        var waitingComment = MakeComment("w1", "reviewer1", "src/File.cs");
        var waitingAction = MonitorTransitions.BuildWaitingCommentAction(state, waitingComment);
        if (waitingAction.Choices != null) allChoices.UnionWith(waitingAction.Choices);

        // Manual handling flow (handle_myself → "let me know when done")
        ResetState(state);
        state.UnresolvedComments = [MakeComment()];
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.CurrentState = MonitorStateId.AwaitingUser;
        var manualAction = MonitorTransitions.ProcessEvent(state, "user_chose", "handle_myself", null);
        if (manualAction.Choices != null) allChoices.UnionWith(manualAction.Choices);

        // CI failure flow — BuildTerminalAction auto-investigates (no choices),
        // so we test investigation results directly
        ResetState(state);
        SetChecksFailed(state);
        state.CiFailureFlow = CiFailureFlowState.Investigating;
        state.CurrentState = MonitorStateId.Investigating;
        state.SuggestedFix = "Fix the null ref";
        var investigateAction = MonitorTransitions.ProcessEvent(state, "investigation_complete", null, null);
        if (investigateAction.Choices != null) allChoices.UnionWith(investigateAction.Choices);

        // Investigation results (duplicate_artifact)
        ResetState(state);
        SetChecksFailed(state);
        state.CiFailureFlow = CiFailureFlowState.Investigating;
        state.CurrentState = MonitorStateId.Investigating;
        state.IssueType = "duplicate_artifact";
        var dupAction = MonitorTransitions.ProcessEvent(state, "investigation_complete", null, null);
        if (dupAction.Choices != null) allChoices.UnionWith(dupAction.Choices);

        // Unrecognized choice fallback
        ResetState(state);
        state.CurrentState = MonitorStateId.AwaitingUser;
        var unrecognizedAction = MonitorTransitions.ProcessEvent(state, "user_chose", "bogus_choice", null);
        if (unrecognizedAction.Choices != null) allChoices.UnionWith(unrecognizedAction.Choices);

        // Now assert every collected choice exists in ChoiceValueMap
        foreach (var choice in allChoices)
        {
            Assert.True(
                MonitorTransitions.ChoiceValueMap.ContainsKey(choice),
                $"Choice '{choice}' is produced by an action builder but has no entry in ChoiceValueMap");
        }
    }

    private static void ResetState(MonitorState state)
    {
        state.CurrentState = MonitorStateId.Polling;
        state.CommentFlow = CommentFlowState.None;
        state.CiFailureFlow = CiFailureFlowState.None;
        state.ActiveWaitingComment = null;
        state.UnresolvedComments = [];
        state.WaitingForReplyComments = [];
        state.CurrentCommentIndex = 0;
        state.InvestigationFindings = null;
        state.SuggestedFix = null;
        state.IssueType = null;
        state.NeedsAdditionalApproval = false;
        state.ApprovalCountAtMergeFailure = 0;
        state.Approvals = [];
        state.StaleApprovals = [];
        state.FailedChecks = [];
        state.HasMergeConflict = false;
        state.Checks = new CheckRunCounts();
        state.RequiresConversationResolution = false;
    }

    [Fact]
    public void WaitForApprover_EndToEnd_BlocksThenFiresOnNewApproval()
    {
        // 1. Start with approved + CI green
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];

        // Terminal state fires
        var terminal = MonitorTransitions.DetectTerminalState(state, [], false);
        Assert.Equal(TerminalStateType.ApprovedCiGreen, terminal);

        // 2. User chooses "wait_for_approver"
        state.CurrentState = MonitorStateId.AwaitingUser;
        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "wait_for_approver", null);
        Assert.Equal("polling", action.Action);
        Assert.True(state.NeedsAdditionalApproval);
        Assert.Equal(1, state.ApprovalCountAtMergeFailure);

        // 3. Same approver still there — should NOT trigger terminal state
        terminal = MonitorTransitions.DetectTerminalState(state, [], false);
        Assert.Null(terminal);

        // 4. New approver arrives — should trigger terminal state again
        state.Approvals.Add(new ReviewInfo { Author = "bob", State = "APPROVED" });
        terminal = MonitorTransitions.DetectTerminalState(state, [], false);
        Assert.Equal(TerminalStateType.ApprovedCiGreen, terminal);

        // 5. The new terminal action should still include "Wait for another approver"
        var newAction = MonitorTransitions.BuildTerminalAction(state, terminal!.Value);
        Assert.NotNull(newAction.Choices);
        Assert.Contains("Wait for another approver", newAction.Choices);
    }

    [Fact]
    public void WaitForApprover_MultipleRounds_CountIncrements()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];

        // Round 1: wait for approver
        state.CurrentState = MonitorStateId.AwaitingUser;
        MonitorTransitions.ProcessEvent(state, "user_chose", "wait_for_approver", null);
        Assert.Equal(1, state.ApprovalCountAtMergeFailure);

        // Bob approves → fires again
        state.Approvals.Add(new ReviewInfo { Author = "bob", State = "APPROVED" });
        var terminal = MonitorTransitions.DetectTerminalState(state, [], false);
        Assert.Equal(TerminalStateType.ApprovedCiGreen, terminal);

        // Round 2: wait for yet another approver
        state.CurrentState = MonitorStateId.AwaitingUser;
        MonitorTransitions.ProcessEvent(state, "user_chose", "wait_for_approver", null);
        Assert.Equal(2, state.ApprovalCountAtMergeFailure);

        // Same 2 approvers — blocked
        terminal = MonitorTransitions.DetectTerminalState(state, [], false);
        Assert.Null(terminal);

        // Charlie approves → fires again
        state.Approvals.Add(new ReviewInfo { Author = "charlie", State = "APPROVED" });
        terminal = MonitorTransitions.DetectTerminalState(state, [], false);
        Assert.Equal(TerminalStateType.ApprovedCiGreen, terminal);
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
        // Question should show the original state, not the recovery state
        Assert.Contains("Stopped", action.Question);
        Assert.DoesNotContain("AwaitingUser", action.Question);
        Assert.NotNull(action.Choices);
        // Verify recovery: state transitions to AwaitingUser so next user_chose won't loop
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UnexpectedState_ThenResume_TransitionsToPolling()
    {
        // Reproduces the PR 57090 bug: unexpected state → user picks Resume → should recover, not loop
        var state = CreateState();
        state.CurrentState = MonitorStateId.Investigating;
        state.CiFailureFlow = CiFailureFlowState.Investigating;

        // First call: unexpected event triggers recovery prompt
        var action = MonitorTransitions.ProcessEvent(state, "some_unexpected_event", null, null);
        Assert.Equal("ask_user", action.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);

        // Second call: user picks "Resume monitoring" — should escape the loop
        action = MonitorTransitions.ProcessEvent(state, "user_chose", "resume", null);
        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_PushCompletedFromInvestigating_TransitionsToPolling()
    {
        // Agent applied fix + pushed during investigation (skipped investigation_complete → apply_fix flow)
        var state = CreateState();
        state.CurrentState = MonitorStateId.Investigating;
        state.CiFailureFlow = CiFailureFlowState.Investigating;

        var action = MonitorTransitions.ProcessEvent(state, "push_completed", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
        Assert.Equal(CiFailureFlowState.None, state.CiFailureFlow);
    }

    [Fact]
    public void ProcessEvent_UnexpectedState_ThenStop_StopsMonitoring()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.Investigating;
        state.CiFailureFlow = CiFailureFlowState.Investigating;

        // First call: unexpected event triggers recovery prompt
        var action = MonitorTransitions.ProcessEvent(state, "some_unexpected_event", null, null);
        Assert.Equal("ask_user", action.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
        // Recovery clears active flows so resume/stop route through terminal switch
        Assert.Equal(CiFailureFlowState.None, state.CiFailureFlow);

        // Second call: user picks "Stop monitoring"
        action = MonitorTransitions.ProcessEvent(state, "user_chose", "stop", null);
        Assert.Equal("stop", action.Action);
        Assert.Equal(MonitorStateId.Stopped, state.CurrentState);
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
    public void ProcessEvent_UserChoice_HandleMyself_CommentFlow_WaitsForManualHandling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "handle_myself", null);

        // handle_myself transitions to WaitingForManualHandling — no ignoring
        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.WaitingForManualHandling, state.CommentFlow);
        Assert.NotNull(action.Choices);
        Assert.Contains("Done, continue monitoring", action.Choices);
        Assert.Contains("Stop monitoring", action.Choices);
    }

    [Fact]
    public void ProcessEvent_UserChoice_HandleMyself_MultiComment_WaitsForManualHandling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2"), MakeComment("c3")];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "handle_myself", null);

        // Multi-comment handle_myself also waits — no ignoring
        Assert.Equal("ask_user", action.Action);
        Assert.Equal(CommentFlowState.WaitingForManualHandling, state.CommentFlow);
    }

    [Fact]
    public void ProcessEvent_UserChoice_DoneHandling_ResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.WaitingForManualHandling;
        state.UnresolvedComments = [MakeComment()];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "done_handling", null);

        Assert.Equal("polling", action.Action);
    }

    [Fact]
    public void ProcessEvent_UserChoice_StopFromManualHandling_StopsMonitoring()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.WaitingForManualHandling;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "stop", null);

        Assert.Equal("stop", action.Action);
    }

    [Fact]
    public void ProcessEvent_CommentAddressed_FromExecutingTask_AdvancesCommentFlow()
    {
        // Freeform Path B: agent executed custom instruction, calls comment_addressed
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);

        // Should advance comment flow (resolve thread or show remaining)
        Assert.NotEqual("stop", action.Action);
    }

    [Fact]
    public void ProcessEvent_UserChose_FromExecutingTask_FreeformPathA()
    {
        // Freeform Path A: agent mapped freeform text back to a choice
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "address", null);

        // Should process the choice normally (begin addressing the comment)
        Assert.Equal("execute", action.Action);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
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

    [Fact]
    public void ProcessEvent_ExplainAll_BeginsExplainFlow()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "explain_all", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("explain_comment", action.Task);
        Assert.Equal(CommentFlowState.ExplainAllIterating, state.CommentFlow);
        Assert.Equal(0, state.CurrentCommentIndex);
    }

    [Fact]
    public void ProcessEvent_ExplainAll_TaskComplete_ShowsPerCommentChoices()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.PendingExplainResult = true;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Apply the recommendation", action.Choices!);
        Assert.Contains("Skip this comment", action.Choices!);
        Assert.Contains("Done — resume monitoring", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_ExplainAll_Skip_AdvancesToNextComment()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "skip", null);

        // Should emit explain for the second comment
        Assert.Equal("execute", action.Action);
        Assert.Equal("explain_comment", action.Task);
        Assert.Equal(1, state.CurrentCommentIndex);
    }

    [Fact]
    public void ProcessEvent_ExplainAll_SkipLastComment_ResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments = [MakeComment("c1")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "skip", null);

        Assert.Equal("polling", action.Action);
    }

    [Fact]
    public void ProcessEvent_ExplainAll_ApplyFix_BeginsApplyRecommendation()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("apply_recommendation", action.Task);
        Assert.Contains("Apply the recommendation you made", action.Instructions!);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_ExplainAll_Done_ResumesPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "done", null);

        Assert.Equal("polling", action.Action);
    }

    [Fact]
    public void ProcessEvent_ExplainAll_FreeformTaskComplete_RepresentsChoices()
    {
        // Bug: After a freeform task (e.g., "do a gap analysis") completes in explain-all flow,
        // the state machine should re-present per-comment choices — not advance to the next comment.
        // PendingExplainResult is false because it was cleared when the explain result was shown.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.PendingExplainResult = false; // already shown explain, user typed freeform
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs"),
                                    MakeComment("c3", "reviewer1", "src/Another.cs"), MakeComment("c4")];
        state.CurrentCommentIndex = 2; // comment 3/4

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        // Should re-present choices for the SAME comment, not advance
        Assert.Equal("ask_user", action.Action);
        Assert.Equal(2, state.CurrentCommentIndex); // still on comment 3
        Assert.Contains("Apply the recommendation", action.Choices!);
        Assert.Contains("Skip this comment", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_SingleComment_FreeformTaskComplete_RepresentsChoices()
    {
        // After a freeform task completes in single-comment flow,
        // re-present the post-explain choices instead of re-explaining.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.PendingExplainResult = false; // explain already ran
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
        Assert.Contains("Apply the recommendation", action.Choices!);
        Assert.Contains("I'll handle it myself", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_MultiCommentPrompt_FreeformTaskComplete_RepresentsChoices()
    {
        // After a freeform task completes in multi-comment flow,
        // re-present the multi-comment choices instead of transitioning to polling.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.MultiCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
        Assert.Contains("Address all comments", action.Choices!);
        Assert.Contains("Explain each one by one", action.Choices!);
        Assert.Contains("I'll handle the comments myself", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_CiFailure_FreeformTaskComplete_RepresentsInvestigationChoices()
    {
        // After a freeform task completes during CI investigation results,
        // re-present the investigation choices instead of transitioning to polling.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.InvestigationFindings = "The build failed due to a missing import.";
        state.SuggestedFix = "Add the missing import statement.";
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal(MonitorStateId.AwaitingUser, state.CurrentState);
        Assert.Equal(CiFailureFlowState.InvestigationResults, state.CiFailureFlow);
        Assert.Contains("Apply the recommendation", action.Choices!);
        Assert.Contains("Re-run failed jobs", action.Choices!);
        Assert.Contains("I'll handle it myself", action.Choices!);
        // Without an updated recommendation, the original SuggestedFix is preserved
        Assert.Equal("Add the missing import statement.", state.SuggestedFix);
    }

    [Fact]
    public void ProcessEvent_CiFailure_FreeformTaskComplete_UpdatesRecommendation()
    {
        // When the agent provides an updated recommendation via LastRecommendation
        // during a freeform task (e.g., user asked for clarification and agent revised
        // their analysis), SuggestedFix should be updated to reflect the new recommendation.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.InvestigationFindings = "The build failed due to a missing import.";
        state.SuggestedFix = "Add the missing import statement.";
        state.LastRecommendation = "Actually, the import is unused. Remove the reference from the csproj instead.";
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        // SuggestedFix should be updated with the new recommendation
        Assert.Equal("Actually, the import is unused. Remove the reference from the csproj instead.", state.SuggestedFix);
        // LastRecommendation should be cleared after propagation
        Assert.Null(state.LastRecommendation);
        // The updated recommendation should appear in the elicitation question
        Assert.Contains("Remove the reference from the csproj instead", action.Question);
        Assert.Contains("Apply the recommendation", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_CiFailure_FreeformTaskComplete_NoSuggestedFix_OmitsApply()
    {
        // If no suggested fix, "Apply the recommendation" should not appear.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.InvestigationFindings = "Infrastructure timeout — no code fix needed.";
        state.SuggestedFix = null;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.DoesNotContain("Apply the recommendation", action.Choices!);
        Assert.Contains("Re-run failed jobs", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_CiFailure_FreeformTaskComplete_NewRecommendation_AddsApplyChoice()
    {
        // When originally there was no suggested fix but the agent provides one via
        // LastRecommendation during a freeform task, "Apply the recommendation" should appear.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.InvestigationFindings = "Infrastructure timeout — no code fix needed.";
        state.SuggestedFix = null;
        state.LastRecommendation = "Turns out there's a config file that needs updating.";
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Equal("Turns out there's a config file that needs updating.", state.SuggestedFix);
        Assert.Contains("Apply the recommendation", action.Choices!);
    }

    [Fact]
    public void ProcessEvent_CiFailure_FreeformPushCompleted_TransitionsToPolling()
    {
        // Freeform Path B in CI flow: agent applied a fix and pushed from ExecutingTask.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "push_completed", null, null);

        Assert.Equal("polling", action.Action);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
        Assert.Equal(CiFailureFlowState.None, state.CiFailureFlow);
    }

    #endregion

    #region ProcessEvent — CI Failure Flow Choices

    [Fact]
    public void BuildTerminalAction_CiFailure_AutoInvestigates_ThenUserChoosesApplyFix()
    {
        var state = CreateState();
        SetChecksFailed(state);

        // CI failure auto-investigates
        var investigateAction = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiFailure);
        Assert.Equal("execute", investigateAction.Action);
        Assert.Equal("investigate_ci_failure", investigateAction.Task);
        Assert.Equal(MonitorStateId.Investigating, state.CurrentState);

        // Investigation completes with a suggested fix
        state.SuggestedFix = "Fix the null ref";
        var resultsAction = MonitorTransitions.ProcessEvent(state, "investigation_complete", null, null);
        Assert.Equal("ask_user", resultsAction.Action);
        Assert.Contains("Apply the recommendation", resultsAction.Choices!);
        Assert.Contains("Re-run failed jobs", resultsAction.Choices!);
        Assert.Contains("I'll handle it myself", resultsAction.Choices!);

        // User chooses to apply fix
        var applyAction = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);
        Assert.Equal("execute", applyAction.Action);
        Assert.Equal("apply_fix", applyAction.Task);
        Assert.Equal(MonitorStateId.ApplyingFix, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_RerunFailed_FromInvestigationResults_ExecutesRerun()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("rerun_via_browser", action.Task);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    [Fact]
    public void ProcessEvent_UserChoice_HandleMyself_FromInvestigationResults_Stops()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
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
        state.PendingReplyText = "test reply";
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
        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);

        // Then: task_complete queues re-request review (last comment from reviewer1)
        var action2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("auto_execute", action2.Action);
        Assert.Equal("request_review", action2.Task);

        // Then: re-request completes → resumes polling (last comment)
        var action3 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("polling", action3.Action);
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
        state.PendingReplyText = "test reply";
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
    public void ExplainFlow_ApplyAfterExplain_ExecutesApplyRecommendation()
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
        Assert.Equal("apply_recommendation", applyAction.Task);
        Assert.Contains("Apply the recommendation you made", applyAction.Instructions!);
    }

    [Fact]
    public void ExplainComment_Instructions_MentionTestEvidenceForPushback()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);

        Assert.Equal("explain_comment", action.Task);
        Assert.Contains("test evidence", action.Instructions!);
        Assert.Contains("pushing back", action.Instructions!);
    }

    [Fact]
    public void ApplyRecommendation_PushbackInstructions_RequireTestFirst()
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

        Assert.Equal("apply_recommendation", applyAction.Task);
        // Pushback path requires trying to write a test first
        Assert.Contains("first try to find or write a test", applyAction.Instructions!);
        // If test can't be written, agent should reconsider
        Assert.Contains("reconsider", applyAction.Instructions!);
        // Clarifying questions remain a separate path
        Assert.Contains("clarifying question", applyAction.Instructions!);
    }

    [Fact]
    public void CommentReplied_BotReviewer_AutoResolves()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1", "copilot-pull-request-reviewer[bot]")];
        state.CurrentCommentIndex = 0;

        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Bot reviewer → auto-resolve (same as comment_addressed)
        Assert.True(state.PendingResolveAfterAddress);
        Assert.Equal("Replied to comment", state.PendingResolveSummary);
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);
    }

    [Fact]
    public void CommentReplied_BotReviewer_PostResolve_UsesRepliedSummary()
    {
        // After bot auto-resolve completes (task_complete), re-request is queued,
        // then after re-request the summary should say "Replied to comment"
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [
            MakeComment("c1", "copilot-pull-request-reviewer[bot]"),
            MakeComment("c2", "human-reviewer", "src/Other.cs")
        ];
        state.CurrentCommentIndex = 0;

        // comment_replied on bot → resolve_thread
        state.PendingReplyText = "test reply";
        MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        // resolve completes → re-request review for bot (last from them)
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("request_review", reRequestAction.Task);
        // re-request completes → should use "Replied to comment" in user-facing question
        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Replied to comment", action.Question!);
        Assert.DoesNotContain("addressed", action.Question!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommentReplied_MultipleBotComments_ReRequestsAfterLast()
    {
        // BUG REPRO: When a bot reviewer has multiple comments and the agent pushes back
        // on all of them via comment_replied, the re-request must still fire after the last
        // one. Previously, comment_replied on bots didn't mark IsAddressed=true, so
        // ShouldReRequestReview saw earlier replied-to comments as "still needs action"
        // and never triggered a re-request.
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments =
        [
            MakeComment("c1", "copilot-pull-request-reviewer[bot]"),
            MakeComment("c2", "copilot-pull-request-reviewer[bot]", "src/Other.cs"),
            MakeComment("c3", "copilot-pull-request-reviewer[bot]", "src/Third.cs")
        ];
        state.CurrentCommentIndex = 0;

        // === Reply to comment 0 (bot) ===
        // comment_replied → auto-resolve (bot path)
        state.PendingReplyText = "test reply";
        var resolve1 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Equal("resolve_thread", resolve1.Task);
        Assert.True(state.UnresolvedComments[0].IsAddressed);

        // resolve completes → should NOT re-request yet (c2, c3 still pending)
        var advance1 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.NotEqual("request_review", advance1.Task);
        Assert.Equal(1, state.CurrentCommentIndex);

        // === Reply to comment 1 (bot) ===
        state.CurrentState = MonitorStateId.ExecutingTask;
        var resolve2 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Equal("resolve_thread", resolve2.Task);
        Assert.True(state.UnresolvedComments[1].IsAddressed);

        // resolve completes → should NOT re-request yet (c3 still pending)
        var advance2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.NotEqual("request_review", advance2.Task);
        Assert.Equal(2, state.CurrentCommentIndex);

        // === Reply to comment 2 (bot, last one) ===
        state.CurrentState = MonitorStateId.ExecutingTask;
        var resolve3 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Equal("resolve_thread", resolve3.Task);
        Assert.True(state.UnresolvedComments[2].IsAddressed);

        // resolve completes → SHOULD re-request now (all bot comments handled)
        var reRequest = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("auto_execute", reRequest.Action);
        Assert.Equal("request_review", reRequest.Task);
        Assert.Equal("copilot-pull-request-reviewer[bot]", state.PendingReRequestReviewer);
    }

    [Fact]
    public void CommentAddressed_PostResolve_UsesAddressedSummary()
    {
        // Verify comment_addressed still uses "Comment addressed" after auto-resolve and re-request
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [
            MakeComment("c1", "human-reviewer"),
            MakeComment("c2", "human-reviewer2", "src/Other.cs")
        ];
        state.CurrentCommentIndex = 0;

        // comment_addressed → resolve_thread
        state.PendingReplyText = "test reply";
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        // resolve completes → re-request review for human-reviewer (last from them)
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("request_review", reRequestAction.Task);
        // re-request completes → should use "Comment addressed"
        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("Comment addressed", action.Question!);
    }

    [Fact]
    public void CommentReplied_HumanReviewer_TracksWaitingForReply()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1", "human-reviewer")];
        state.CurrentCommentIndex = 0;

        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Human reviewer → track as waiting-for-reply, don't auto-resolve
        Assert.Single(state.WaitingForReplyComments);
        Assert.True(state.WaitingForReplyComments[0].IsWaitingForReply);
        Assert.False(state.PendingResolveAfterAddress);
        // First: post the reply via auto_execute
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("post_thread_reply", action.Task);
        // After reply posted → re-request review (single comment, last from this reviewer)
        var nextAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("auto_execute", nextAction.Action);
        Assert.Equal("request_review", nextAction.Task);
    }

    [Fact]
    public void ApplyRecommendation_Instructions_DifferentiatePaths()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);
        MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "apply_fix", null);

        // Instructions should mention both event paths
        Assert.Contains("event=comment_addressed", action.Instructions!);
        Assert.Contains("event=comment_replied", action.Instructions!);
    }

    [Fact]
    public void CommentReplied_ExplainAll_HumanReviewer_AdvancesToNextComment()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments = [MakeComment("c1", "human-reviewer"), MakeComment("c2", "human-reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Should track first comment as waiting-for-reply
        Assert.Single(state.WaitingForReplyComments);
        Assert.Equal("c1", state.WaitingForReplyComments[0].Id);
        // First: post the reply via auto_execute
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("post_thread_reply", action.Task);
        // After reply posted → re-request review (last comment from this reviewer)
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("auto_execute", reRequestAction.Action);
        Assert.Equal("request_review", reRequestAction.Task);
        // After re-request completes, should advance to explain next comment
        var nextAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal(1, state.CurrentCommentIndex);
        Assert.Equal("execute", nextAction.Action);
        Assert.Equal("explain_comment", nextAction.Task);
    }

    [Fact]
    public void CommentReplied_PickedSingleComment_MultipleUnresolved_MessageSaysReplied()
    {
        // Scenario: user picked a specific comment from multi-comment list,
        // went through explain → apply_fix → agent pushes back → comment_replied.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1", "human-reviewer"), MakeComment("c2", "human-reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // First: post the reply via auto_execute
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("post_thread_reply", action.Task);
        // After reply posted → re-request (last comment from this reviewer)
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("auto_execute", reRequestAction.Action);
        Assert.Equal("request_review", reRequestAction.Task);
        // After re-request completes → should ask about remaining comments
        var nextAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("ask_user", nextAction.Action);
        // Should NOT say "Comment addressed" since we only replied
        Assert.DoesNotContain("addressed", nextAction.Question!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommentReplied_HumanReviewer_NoReRequestWhenMoreComments()
    {
        // Two comments from the same reviewer — replying to one shouldn't re-request yet
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments = [MakeComment("c1", "human-reviewer"), MakeComment("c2", "human-reviewer", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Should track as waiting-for-reply
        Assert.Single(state.WaitingForReplyComments);
        // First: post the reply via auto_execute
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("post_thread_reply", action.Task);
        // After reply posted → reviewer has another comment (c2) → no re-request, advance
        var nextAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("ask_user", nextAction.Action);
        Assert.Null(state.PendingReRequestReviewer);
    }

    [Fact]
    public void CommentReplied_HumanReviewer_PostReRequest_AdvancesCorrectly()
    {
        // After reply post + re-request completes for human, should advance with correct summary
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [
            MakeComment("c1", "human-reviewer"),
            MakeComment("c2", "different-reviewer", "src/Other.cs")
        ];
        state.CurrentCommentIndex = 0;

        // comment_replied → post_thread_reply auto_execute
        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Equal("post_thread_reply", action.Task);

        // reply posted → re-request (last from this reviewer)
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("request_review", reRequestAction.Task);
        Assert.Equal("human-reviewer", state.PendingReRequestReviewer);

        // re-request completes → advance
        var nextAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("ask_user", nextAction.Action);
        Assert.Contains("Replied to comment", nextAction.Question!);
    }

    [Fact]
    public void CommentReplied_SameReviewer_SequentialReplies_ReRequestsAfterLast()
    {
        // Two comments from the same reviewer — reply to both sequentially.
        // Re-request should only trigger after the second reply.
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments = [MakeComment("c1", "human-reviewer"), MakeComment("c2", "human-reviewer", "src/Other.cs")];
        state.CurrentCommentIndex = 0;

        // Reply to c1 → post_thread_reply auto_execute
        state.PendingReplyText = "test reply";
        var action1 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Single(state.WaitingForReplyComments);
        Assert.Equal("auto_execute", action1.Action);
        Assert.Equal("post_thread_reply", action1.Task);
        // After reply posted → reviewer has another comment (c2) → no re-request yet, advance
        var advanceAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Null(state.PendingReRequestReviewer);
        Assert.Equal("ask_user", advanceAction.Action);

        // Simulate advancing to c2 and replying
        state.CurrentCommentIndex = 1;
        state.CurrentState = MonitorStateId.ExecutingTask;
        var action2 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // c1 is already waiting-for-reply → post reply first
        Assert.Equal(2, state.WaitingForReplyComments.Count);
        Assert.Equal("auto_execute", action2.Action);
        Assert.Equal("post_thread_reply", action2.Task);
        // After reply posted → no remaining work → re-request
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("auto_execute", reRequestAction.Action);
        Assert.Equal("request_review", reRequestAction.Task);
        Assert.Equal("human-reviewer", state.PendingReRequestReviewer);
    }

    #endregion

    #region Deferred Rerun (pending checks)

    [Fact]
    public void BuildRerunAction_WithInProgressChecks_DefersToPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.Checks = new CheckRunCounts { Passed = 2, Failed = 1, InProgress = 2, Total = 5 };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("polling", action.Action);
        Assert.True(state.PendingRerunWhenChecksComplete);
        Assert.Equal(MonitorStateId.Polling, state.CurrentState);
        Assert.Equal(CiFailureFlowState.None, state.CiFailureFlow);
    }

    [Fact]
    public void BuildRerunAction_WithQueuedChecks_DefersToPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.Checks = new CheckRunCounts { Passed = 2, Failed = 1, Queued = 1, Total = 4 };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("polling", action.Action);
        Assert.True(state.PendingRerunWhenChecksComplete);
    }

    [Fact]
    public void BuildRerunAction_LegacyPendingOnly_ExecutesImmediately()
    {
        // Legacy pending (like policy checks) should NOT defer the rerun
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.Checks = new CheckRunCounts { Passed = 3, Failed = 2, Pending = 1, Total = 6 };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("rerun_via_browser", action.Task);
        Assert.False(state.PendingRerunWhenChecksComplete);
    }

    [Fact]
    public void BuildRerunAction_NoPendingChecks_ExecutesImmediately()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        SetChecksFailed(state);

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("rerun_via_browser", action.Task);
        Assert.False(state.PendingRerunWhenChecksComplete);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    [Fact]
    public void DetectTerminalState_SuppressesCiFailure_WhenPendingRerun()
    {
        var state = CreateState();
        SetChecksFailed(state);
        state.PendingRerunWhenChecksComplete = true;

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Null(result);
    }

    [Fact]
    public void DetectTerminalState_CiFailure_WhenNotPendingRerun()
    {
        var state = CreateState();
        SetChecksFailed(state);
        state.PendingRerunWhenChecksComplete = false;

        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.CiFailure, result);
    }

    [Fact]
    public void CompletePendingRerun_ClearsFlag_ReturnsExecute()
    {
        var state = CreateState();
        state.PendingRerunWhenChecksComplete = true;
        state.Checks = new CheckRunCounts { Passed = 4, Failed = 1, Total = 5 };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];

        var action = MonitorTransitions.CompletePendingRerun(state);

        Assert.False(state.PendingRerunWhenChecksComplete);
        Assert.Equal("execute", action.Action);
        Assert.Equal("rerun_via_browser", action.Task);
        Assert.Equal(MonitorStateId.ExecutingTask, state.CurrentState);
    }

    [Fact]
    public void RerunFromInvestigationResults_WithPendingChecks_DefersToPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.InvestigationResults;
        state.Checks = new CheckRunCounts { Passed = 2, Failed = 1, InProgress = 1, Total = 4 };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun", null);

        Assert.Equal("polling", action.Action);
        Assert.True(state.PendingRerunWhenChecksComplete);
    }

    [Fact]
    public void TopLevelRerunFailed_WithPendingChecks_DefersToPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.Checks = new CheckRunCounts { Passed = 2, Failed = 1, InProgress = 1, Total = 4 };
        state.FailedChecks = [new FailedCheckInfo { Name = "build", Conclusion = "failure", Url = "https://dev.azure.com/build/1" }];

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "rerun_failed", null);

        Assert.Equal("polling", action.Action);
        Assert.True(state.PendingRerunWhenChecksComplete);
    }

    [Fact]
    public void DetectTerminalState_PendingRerunWithNewCommit_FlagShouldBeCleared()
    {
        // Simulates what the poll loop does: new SHA means flag should be cleared
        // (The actual clearing happens in MonitorFlowTools, but verify the state machine
        // still detects CiFailure normally once the flag is cleared)
        var state = CreateState();
        SetChecksFailed(state);
        state.PendingRerunWhenChecksComplete = true;

        // While flag is set, CiFailure is suppressed
        Assert.Null(MonitorTransitions.DetectTerminalState(state, [], false));

        // After poll loop clears flag (new commit detected), CiFailure fires again
        state.PendingRerunWhenChecksComplete = false;
        Assert.Equal(TerminalStateType.CiFailure, MonitorTransitions.DetectTerminalState(state, [], false));
    }

    [Fact]
    public void DetectTerminalState_PendingRerunNoFailures_ResumeNormalDetection()
    {
        // Simulates what the poll loop does: failures resolved, flag cleared,
        // normal terminal state detection resumes
        var state = CreateState();
        state.PendingRerunWhenChecksComplete = true;
        state.Checks = new CheckRunCounts { Passed = 5, Total = 5 };
        state.Approvals = [new ReviewInfo { Author = "approver", State = "APPROVED" }];

        // Poll loop would clear the flag since Failed == 0, then DetectTerminalState runs normally
        state.PendingRerunWhenChecksComplete = false;
        var result = MonitorTransitions.DetectTerminalState(state, [], false);

        Assert.Equal(TerminalStateType.ApprovedCiGreen, result);
    }

    #endregion

    #region IsBotReviewer

    [Fact]
    public void IsBotReviewer_CopilotReviewer_ReturnsTrue()
    {
        Assert.True(PrStatusFetcher.IsBotReviewer("copilot-pull-request-reviewer[bot]"));
    }

    [Fact]
    public void IsBotReviewer_GenericBot_ReturnsTrue()
    {
        Assert.True(PrStatusFetcher.IsBotReviewer("some-other-tool[bot]"));
    }

    [Fact]
    public void IsBotReviewer_HumanReviewer_ReturnsFalse()
    {
        Assert.False(PrStatusFetcher.IsBotReviewer("reviewer1"));
    }

    [Fact]
    public void IsBotReviewer_EmptyString_ReturnsFalse()
    {
        Assert.False(PrStatusFetcher.IsBotReviewer(""));
    }

    [Fact]
    public void IsBotReviewer_Null_ReturnsFalse()
    {
        Assert.False(PrStatusFetcher.IsBotReviewer(null!));
    }

    [Fact]
    public void IsBotReviewer_CaseInsensitive()
    {
        Assert.True(PrStatusFetcher.IsBotReviewer("MyBot[BOT]"));
    }

    [Fact]
    public void IsCiBot_CopilotReviewer_ReturnsFalse()
    {
        // copilot-pull-request-reviewer[bot] should NOT be filtered as a CI bot
        Assert.False(PrStatusFetcher.IsCiBot("copilot-pull-request-reviewer[bot]"));
    }

    #endregion

    #region NormalizeBotLogin

    [Fact]
    public void NormalizeBotLogin_GraphQLBot_AppendsBotSuffix()
    {
        // GraphQL returns Bot actors without [bot] suffix
        var json = """{"login": "copilot-pull-request-reviewer", "__typename": "Bot"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = PrStatusFetcher.NormalizeBotLogin(doc.RootElement);
        Assert.Equal("copilot-pull-request-reviewer[bot]", result);
    }

    [Fact]
    public void NormalizeBotLogin_HumanUser_ReturnsUnchanged()
    {
        var json = """{"login": "reviewer1", "__typename": "User"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = PrStatusFetcher.NormalizeBotLogin(doc.RootElement);
        Assert.Equal("reviewer1", result);
    }

    [Fact]
    public void NormalizeBotLogin_RestStyleAlreadyHasBotSuffix_NoDoubleAppend()
    {
        var json = """{"login": "copilot-pull-request-reviewer[bot]", "__typename": "Bot"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = PrStatusFetcher.NormalizeBotLogin(doc.RootElement);
        Assert.Equal("copilot-pull-request-reviewer[bot]", result);
    }

    [Fact]
    public void NormalizeBotLogin_NoTypename_ReturnsUnchanged()
    {
        var json = """{"login": "copilot-pull-request-reviewer"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = PrStatusFetcher.NormalizeBotLogin(doc.RootElement);
        Assert.Equal("copilot-pull-request-reviewer", result);
    }

    [Fact]
    public void NormalizeBotLogin_NullAuthor_ReturnsEmpty()
    {
        // GraphQL returns author: null for deleted/ghost users
        var json = "null";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = PrStatusFetcher.NormalizeBotLogin(doc.RootElement);
        Assert.Equal("", result);
    }

    [Fact]
    public void NormalizeBotLogin_MissingLogin_ReturnsEmpty()
    {
        var json = """{"__typename": "Bot"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = PrStatusFetcher.NormalizeBotLogin(doc.RootElement);
        Assert.Equal("", result);
    }

    #endregion

    #region ApprovalAutoResolve

    [Fact]
    public void BuildApprovedAction_NoConversationRequirement_OffersMerge()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
        state.RequiresConversationResolution = false;
        state.WaitingForReplyComments = [MakeComment("w1", "bob")];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.NotNull(action.Choices);
        Assert.Contains("Merge the PR", action.Choices);
    }

    [Fact]
    public void BuildApprovedAction_ConversationRequired_NoWaiting_NoWarning()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
        state.RequiresConversationResolution = true;
        state.WaitingForReplyComments = [];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.NotNull(action.Choices);
        Assert.Contains("Merge the PR", action.Choices);
        Assert.DoesNotContain("unresolved conversation", action.Question!);
    }

    [Fact]
    public void BuildApprovedAction_ConversationRequired_WithWaiting_WarnsButOffersMerge()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
        state.RequiresConversationResolution = true;
        state.WaitingForReplyComments = [MakeComment("w1", "bob")];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.NotNull(action.Choices);
        Assert.Contains("Merge the PR", action.Choices);
        Assert.Contains("bob", action.Question!);
        Assert.Contains("unresolved conversation", action.Question!);
    }

    [Fact]
    public void BuildApprovedAction_ConversationRequired_MultipleWaitingAuthors_ShowsAllAuthors()
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
        state.RequiresConversationResolution = true;
        state.WaitingForReplyComments =
        [
            MakeComment("w1", "bob"),
            MakeComment("w2", "carol"),
            MakeComment("w3", "bob") // duplicate author
        ];

        var action = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.ApprovedCiGreen);

        Assert.NotNull(action.Question);
        Assert.Contains("3 unresolved conversation(s)", action.Question);
        Assert.Contains("bob", action.Question);
        Assert.Contains("carol", action.Question);
    }

    #endregion

    #region Re-Request Review After Comment Resolution

    [Fact]
    public void ShouldReRequestReview_LastCommentFromReviewer_ReturnsTrue()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.UnresolvedComments = [MakeComment("c1", "reviewer1")];
        state.CurrentCommentIndex = 0;

        Assert.True(MonitorTransitions.ShouldReRequestReview(state, "reviewer1"));
    }

    [Fact]
    public void ShouldReRequestReview_MoreCommentsAhead_ReturnsFalse()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.UnresolvedComments =
        [
            MakeComment("c1", "reviewer1"),
            MakeComment("c2", "reviewer1")
        ];
        state.CurrentCommentIndex = 0;

        Assert.False(MonitorTransitions.ShouldReRequestReview(state, "reviewer1"));
    }

    [Fact]
    public void ShouldReRequestReview_CommentsBehindCurrentIndex_ReturnsFalse()
    {
        // Out-of-order addressing: user picks comment at index 2, but index 0 is same reviewer
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.UnresolvedComments =
        [
            MakeComment("c1", "reviewer1"),
            MakeComment("c2", "reviewer2"),
            MakeComment("c3", "reviewer1")
        ];
        state.CurrentCommentIndex = 2; // addressing c3, but c1 (reviewer1) is at index 0

        Assert.False(MonitorTransitions.ShouldReRequestReview(state, "reviewer1"));
    }

    [Fact]
    public void ShouldReRequestReview_PrAuthor_ReturnsFalse()
    {
        var state = CreateState();
        state.PrAuthor = "reviewer1";
        state.CurrentUser = "current-user";
        state.UnresolvedComments = [MakeComment("c1", "reviewer1")];
        state.CurrentCommentIndex = 0;

        Assert.False(MonitorTransitions.ShouldReRequestReview(state, "reviewer1"));
    }

    [Fact]
    public void ShouldReRequestReview_CurrentUser_ReturnsFalse()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "reviewer1";
        state.UnresolvedComments = [MakeComment("c1", "reviewer1")];
        state.CurrentCommentIndex = 0;

        Assert.False(MonitorTransitions.ShouldReRequestReview(state, "reviewer1"));
    }

    [Fact]
    public void ShouldReRequestReview_BotReviewer_ReturnsTrue()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.UnresolvedComments = [MakeComment("c1", "copilot-pull-request-reviewer[bot]")];
        state.CurrentCommentIndex = 0;

        // Bots like Copilot should be re-requested
        Assert.True(MonitorTransitions.ShouldReRequestReview(state, "copilot-pull-request-reviewer[bot]"));
    }

    [Fact]
    public void ShouldReRequestReview_AlreadyReRequested_ReturnsFalse()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.UnresolvedComments = [MakeComment("c1", "reviewer1")];
        state.CurrentCommentIndex = 0;
        state.ReviewsReRequested = ["reviewer1"];

        Assert.False(MonitorTransitions.ShouldReRequestReview(state, "reviewer1"));
    }

    [Fact]
    public void ShouldReRequestReview_CaseInsensitive()
    {
        var state = CreateState();
        state.PrAuthor = "PR-Author";
        state.CurrentUser = "current-user";
        state.UnresolvedComments = [MakeComment("c1", "pr-author")];
        state.CurrentCommentIndex = 0;

        // Should match PrAuthor case-insensitively
        Assert.False(MonitorTransitions.ShouldReRequestReview(state, "pr-author"));
    }

    [Fact]
    public void ProcessTaskComplete_AfterResolve_LastComment_QueuesReRequestReview()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1", "reviewer1")];
        state.CurrentCommentIndex = 0;

        // Simulate: comment_addressed → resolve_thread → task_complete
        state.PendingReplyText = "test reply";
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        // Should queue a re-request review auto_execute
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("request_review", action.Task);
        Assert.Equal("reviewer1", state.PendingReRequestReviewer);
        Assert.Contains("reviewer1", state.ReviewsReRequested);
    }

    [Fact]
    public void ProcessTaskComplete_AfterResolve_MoreComments_NoReRequest()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments =
        [
            MakeComment("c1", "reviewer1"),
            MakeComment("c2", "reviewer1")
        ];
        state.CurrentCommentIndex = 0;

        // Simulate: comment_addressed → resolve_thread → task_complete
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        // Should NOT queue re-request — reviewer1 has another comment ahead
        Assert.NotEqual("request_review", action.Task);
        Assert.Null(state.PendingReRequestReviewer);
    }

    [Fact]
    public void ShouldReRequestReview_AllCommentsFromSameReviewer_Addressed_ReturnsTrue()
    {
        // BUG REPRO: When a reviewer has 2 comments and both are addressed sequentially,
        // the re-request should fire after the LAST one. Previously, ShouldReRequestReview
        // always returned false because it saw the already-addressed comment at the earlier
        // index as still "unresolved" (the UnresolvedComments list is a static snapshot).
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments =
        [
            MakeComment("c1", "reviewer1"),
            MakeComment("c2", "reviewer1")
        ];
        state.CurrentCommentIndex = 0;

        // === Address comment 0 ===
        // comment_addressed → sets PendingResolveAfterAddress, returns resolve_thread
        state.PendingReplyText = "test reply";
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        // resolve completes → should NOT re-request yet (comment 1 still pending)
        var action1 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.NotEqual("request_review", action1.Task);

        // State machine advances: ask_user for next comment, user chooses "Address this comment"
        Assert.Equal("ask_user", action1.Action);
        Assert.Equal(1, state.CurrentCommentIndex);
        state.CurrentState = MonitorStateId.ExecutingTask;

        // === Address comment 1 ===
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        // resolve completes → SHOULD re-request now (last comment from reviewer1)
        var action2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("auto_execute", action2.Action);
        Assert.Equal("request_review", action2.Task);
        Assert.Equal("reviewer1", state.PendingReRequestReviewer);
    }

    [Fact]
    public void ProcessTaskComplete_AfterReRequest_AdvancesToNextComment()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments =
        [
            MakeComment("c1", "reviewer1"),
            MakeComment("c2", "reviewer2")
        ];
        state.CurrentCommentIndex = 0;

        // Simulate full flow: address → resolve → re-request → task_complete
        state.PendingReplyText = "test reply";
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        // resolve completes → queues re-request
        var reRequestAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("request_review", reRequestAction.Task);

        // re-request completes → should advance to next comment
        var nextAction = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", nextAction.Action);
        Assert.Equal(1, state.CurrentCommentIndex);
    }

    [Fact]
    public void ProcessTaskComplete_MultipleReviewers_ReRequestsEach()
    {
        var state = CreateState();
        state.PrAuthor = "pr-author";
        state.CurrentUser = "current-user";
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.AddressAllIterating;
        state.UnresolvedComments =
        [
            MakeComment("c1", "reviewer1"),
            MakeComment("c2", "reviewer2")
        ];
        state.CurrentCommentIndex = 0;

        // Address first comment (reviewer1 — last from them)
        state.PendingReplyText = "test reply";
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        var action1 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("request_review", action1.Task);
        Assert.Equal("reviewer1", state.PendingReRequestReviewer);

        // Re-request completes → advance to reviewer2's comment
        MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        // Address second comment (reviewer2 — last from them)
        state.CurrentState = MonitorStateId.ExecutingTask;
        MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        var action2 = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);
        Assert.Equal("request_review", action2.Task);
        Assert.Equal("reviewer2", state.PendingReRequestReviewer);
    }

    [Fact]
    public void TransitionToPolling_ClearsReRequestState()
    {
        var state = CreateState();
        state.PendingReRequestReviewer = "reviewer1";
        state.ReviewsReRequested = ["reviewer1", "reviewer2"];

        // Force a transition to polling
        MonitorTransitions.ProcessEvent(state, "ready", null, null);

        Assert.Null(state.PendingReRequestReviewer);
        Assert.Empty(state.ReviewsReRequested);
    }

    [Fact]
    public void ShouldReRequestReview_ReviewerAlreadyInRequestedReviewers_ReturnsFalse()
    {
        // Simulates a reviewer who is in requested_reviewers (awaiting initial review).
        // Even if they had a waiting-for-reply comment, we should NOT re-request.
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "reviewer-pending" };
        var comments = new List<CommentInfo>
        {
            new() { Id = "c1", Author = "reviewer-pending", IsWaitingForReply = true }
        };

        var result = MonitorTransitions.ShouldReRequestReview(
            "reviewer-pending", "pr-author", "current-user", alreadyRequested, comments);

        Assert.False(result);
    }

    [Fact]
    public void ShouldReRequestReview_ReviewerNotInRequestedReviewers_AllReplied_ReturnsTrue()
    {
        // Simulates a reviewer who finished review (APPROVED), NOT in requested_reviewers.
        // All their comments are waiting-for-reply → should re-request.
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "other-reviewer-a", "other-reviewer-b", "other-reviewer-c" };
        var comments = new List<CommentInfo>
        {
            new() { Id = "c1", Author = "finished-reviewer", IsWaitingForReply = true },
            new() { Id = "c2", Author = "finished-reviewer", IsWaitingForReply = true }
        };

        var result = MonitorTransitions.ShouldReRequestReview(
            "finished-reviewer", "pr-author", "current-user", alreadyRequested, comments);

        Assert.True(result);
    }

    [Fact]
    public void ShouldReRequestReview_ReviewerNotRequested_StillHasNeedsAction_ReturnsFalse()
    {
        // Reviewer finished review but still has a needs-action comment → don't re-request yet
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<CommentInfo>
        {
            new() { Id = "c1", Author = "reviewer1", IsWaitingForReply = true },
            new() { Id = "c2", Author = "reviewer1", IsWaitingForReply = false }
        };

        var result = MonitorTransitions.ShouldReRequestReview(
            "reviewer1", "pr-author", "current-user", alreadyRequested, comments);

        Assert.False(result);
    }

    [Fact]
    public void ShouldReRequestReview_CopilotBotReviewer_NotInRequestedReviewers_ReturnsTrue()
    {
        // Bot reviewer left a COMMENTED review, we resolved its thread, and it's NOT
        // in requested_reviewers. ShouldReRequestReview should return true to re-request
        // the bot so it reviews the updated code.
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "other-reviewer-a", "other-reviewer-b", "other-reviewer-c" };

        // After resolve, the bot's comment is gone from unresolved list.
        // In the comment flow, ShouldReRequestReview is called with the current
        // comment index excluded — simulated here with an empty list (no remaining).
        var comments = new List<CommentInfo>();

        var result = MonitorTransitions.ShouldReRequestReview(
            "copilot-pull-request-reviewer[bot]", "pr-author", "current-user",
            alreadyRequested, comments);

        Assert.True(result);
    }

    [Fact]
    public void ShouldReRequestReview_CopilotBotReviewer_AlreadyInRequestedReviewers_ReturnsFalse()
    {
        // Bot reviewer already has a pending review request → don't re-request
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "copilot-pull-request-reviewer[bot]" };
        var comments = new List<CommentInfo>();

        var result = MonitorTransitions.ShouldReRequestReview(
            "copilot-pull-request-reviewer[bot]", "pr-author", "current-user",
            alreadyRequested, comments);

        Assert.False(result);
    }

    [Fact]
    public void ShouldReRequestReview_ReviewerSubmittedNewReview_UnrepliedComments_ReturnsFalse()
    {
        // Reviewer submitted a new review after being re-requested, adding new comments.
        // They are no longer in requested_reviewers (GitHub removes them on review submit),
        // but they still have unresolved comments that we haven't replied to yet.
        // ShouldReRequestReview must return false — we should not re-request until
        // we've replied to ALL their comments.
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<CommentInfo>
        {
            // Previously replied-to comment
            new() { Id = "c1", Author = "copilot-pull-request-reviewer[bot]", IsWaitingForReply = true },
            // New comment from their latest review — not yet replied to
            new() { Id = "c2", Author = "copilot-pull-request-reviewer[bot]", IsWaitingForReply = false }
        };

        var result = MonitorTransitions.ShouldReRequestReview(
            "copilot-pull-request-reviewer[bot]", "pr-author", "current-user",
            alreadyRequested, comments);

        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldReRequestReview_NullOrEmptyReviewer_ReturnsFalse(string? reviewer)
    {
        var alreadyRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<CommentInfo>();

        var result = MonitorTransitions.ShouldReRequestReview(
            reviewer!, "pr-author", "current-user", alreadyRequested, comments);

        Assert.False(result);
    }

    #endregion

    #region ReplyInstruction Tests

    [Fact]
    public void ReplyDataInstruction_TellsAgentToPassReplyTextInData()
    {
        var instruction = MonitorTransitions.ReplyDataInstruction();

        Assert.Contains("reply_text", instruction);
        Assert.Contains("Do NOT post the reply yourself", instruction);
        Assert.Contains("do NOT use `gh api`", instruction);
        Assert.Contains("do NOT use", instruction);
    }

    [Fact]
    public void AddressComment_Instructions_TellAgentToPassReplyInData()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        var comment = MakeComment();
        comment.RestCommentId = 99999;
        state.UnresolvedComments = [comment];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "address", null);

        Assert.Contains("reply_text", action.Instructions);
        Assert.Contains("Do NOT post the reply yourself", action.Instructions);
        Assert.DoesNotContain("gh api repos/", action.Instructions);
    }

    [Fact]
    public void CommentReplied_HumanReviewer_ReturnsPostReplyAutoExecute()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        var comment = MakeComment("c1", "human-reviewer");
        state.UnresolvedComments = [comment];
        state.CurrentCommentIndex = 0;

        state.PendingReplyText = "test reply";
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("post_thread_reply", action.Task);
        Assert.Equal("Replied to comment", state.PendingAdvanceAfterReply);
    }

    [Fact]
    public void CommentAddressed_ReturnsResolveThread_WithPendingReplyPreserved()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;
        state.PendingReplyText = "Fixed — test-owner/test-repo@abc1234";

        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);

        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("resolve_thread", action.Task);
        // PendingReplyText should still be set — ExecuteAutoAction will post it before resolving
        Assert.Equal("Fixed — test-owner/test-repo@abc1234", state.PendingReplyText);
    }

    [Fact]
    public void CommentAddressed_WithoutReplyText_EmitsComposeReply()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;
        // PendingReplyText is null — agent forgot to include it

        var action = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("compose_reply", action.Task);
        Assert.Contains("reply_text", action.Instructions!);
        Assert.Contains("comment_addressed", action.Instructions!);
    }

    [Fact]
    public void CommentAddressed_ComposeReplyThenRetry_ProceedsToResolve()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        // First call: no reply text → compose_reply
        var action1 = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.Equal("compose_reply", action1.Task);

        // Agent composes reply, calls back with reply_text
        state.PendingReplyText = "Sorted packages — test-owner/test-repo@abc1234";
        var action2 = MonitorTransitions.ProcessEvent(state, "comment_addressed", null, null);
        Assert.Equal("auto_execute", action2.Action);
        Assert.Equal("resolve_thread", action2.Task);
    }

    [Fact]
    public void CommentReplied_WithoutReplyText_EmitsComposeReply()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment("c1", "human-reviewer")];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        Assert.Equal("execute", action.Action);
        Assert.Equal("compose_reply", action.Task);
        Assert.Contains("reply_text", action.Instructions!);
        Assert.Contains("comment_replied", action.Instructions!);
    }

    #endregion

    #region StripSuggestionBlocks

    [Fact]
    public void StripSuggestionBlocks_RemovesSuggestionBlock()
    {
        var body = "Consider adding a null check here.\n```suggestion\nif (x == null) throw new ArgumentNullException();\n```\nThis would prevent crashes.";
        var result = MonitorTransitions.StripSuggestionBlocks(body);

        Assert.DoesNotContain("```suggestion", result);
        Assert.Contains("Consider adding a null check here.", result);
        Assert.Contains("[code suggestion — see full comment for details]", result);
        Assert.Contains("This would prevent crashes.", result);
    }

    [Fact]
    public void StripSuggestionBlocks_HandlesMultipleSuggestions()
    {
        var body = "Fix A:\n```suggestion\ncode A\n```\nFix B:\n```suggestion\ncode B\n```";
        var result = MonitorTransitions.StripSuggestionBlocks(body);

        Assert.DoesNotContain("code A", result);
        Assert.DoesNotContain("code B", result);
        Assert.Equal(2, result.Split("[code suggestion — see full comment for details]").Length - 1);
    }

    [Fact]
    public void StripSuggestionBlocks_PreservesNonSuggestionCodeBlocks()
    {
        var body = "Example:\n```csharp\nvar x = 1;\n```\nDone.";
        var result = MonitorTransitions.StripSuggestionBlocks(body);

        Assert.Contains("```csharp", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void StripSuggestionBlocks_NoSuggestions_ReturnsOriginal()
    {
        var body = "Please fix this variable name.";
        var result = MonitorTransitions.StripSuggestionBlocks(body);

        Assert.Equal(body, result);
    }

    #endregion

    #region Recommendation Display and Clearing

    [Fact]
    public void PostExplain_SingleComment_QuestionIncludesRecommendation()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.PendingExplainResult = true;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;
        state.LastRecommendation = "Add a null check for options in ProcessAsync()";

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("AGENT RECOMMENDATION", action.Question!);
        Assert.Contains("Add a null check for options in ProcessAsync()", action.Question!);
    }

    [Fact]
    public void PostExplain_SingleComment_NullRecommendation_ShowsFallback()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.PendingExplainResult = true;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;
        state.LastRecommendation = null;

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Equal("ask_user", action.Action);
        Assert.Contains("AGENT RECOMMENDATION", action.Question!);
        Assert.Contains("See analysis above", action.Question!);
    }

    [Fact]
    public void PostExplain_QuestionIncludesReviewerCommentHeader()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.PendingExplainResult = true;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;

        var action = MonitorTransitions.ProcessEvent(state, "task_complete", null, null);

        Assert.Contains("REVIEWER COMMENT", action.Question!);
        Assert.Contains("reviewer1", action.Question!);
        Assert.Contains("src/File.cs", action.Question!);
    }

    [Fact]
    public void BeginExplain_ClearsLastRecommendation()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [MakeComment()];
        state.CurrentCommentIndex = 0;
        state.LastRecommendation = "stale recommendation from previous comment";

        MonitorTransitions.ProcessEvent(state, "user_chose", "explain", null);

        Assert.Null(state.LastRecommendation);
    }

    [Fact]
    public void ExplainAll_ClearsLastRecommendation_BetweenComments()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CommentFlow = CommentFlowState.ExplainAllIterating;
        state.UnresolvedComments = [MakeComment("c1"), MakeComment("c2", "reviewer2", "src/Other.cs")];
        state.CurrentCommentIndex = 0;
        state.LastRecommendation = "stale recommendation";

        // Skip first comment → advances to explain next, should clear recommendation
        var action = MonitorTransitions.ProcessEvent(state, "user_chose", "skip", null);

        Assert.Equal("explain_comment", action.Task);
        Assert.Null(state.LastRecommendation);
    }

    [Fact]
    public void NormalizeWhitespace_CollapsesNewlinesAndSpaces()
    {
        Assert.Equal("hello world foo", MonitorTransitions.NormalizeWhitespace("hello\n  world\r\n  foo"));
        Assert.Equal("single line", MonitorTransitions.NormalizeWhitespace("  single   line  "));
        Assert.Equal("", MonitorTransitions.NormalizeWhitespace(""));
    }

    #endregion
}
