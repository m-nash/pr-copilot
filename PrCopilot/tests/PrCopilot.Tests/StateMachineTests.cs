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
    [InlineData(TerminalStateType.CiFailure)]
    [InlineData(TerminalStateType.CiCancelled)]
    [InlineData(TerminalStateType.MergeConflict)]
    public void BuildTerminalAction_AllStates_AllChoicesExistInValueMap(TerminalStateType terminal)
    {
        var state = CreateState();
        SetChecksAllGreen(state);
        state.Approvals = [new ReviewInfo { Author = "alice", State = "APPROVED" }];
        if (terminal == TerminalStateType.CiFailure)
        {
            SetChecksFailed(state);
        }
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
            if (terminal == TerminalStateType.CiFailure) SetChecksFailed(state);
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

        // Single comment flow
        ResetState(state);
        state.UnresolvedComments = [MakeComment()];
        state.CurrentState = MonitorStateId.AwaitingUser;
        var commentAction = MonitorTransitions.ProcessEvent(state, "ready", null, null);
        // Simulate terminal detection → new comment → BuildCommentAction (1 comment)
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.CurrentCommentIndex = 0;
        // The BuildCommentAction for 1 comment gives: Address/Explain/Handle
        if (commentAction.Choices != null) allChoices.UnionWith(commentAction.Choices);

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

        // CI failure flow
        ResetState(state);
        SetChecksFailed(state);
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
        state.CurrentState = MonitorStateId.AwaitingUser;
        // BuildCiFailureAction is private, but it's triggered through BuildTerminalAction for CiFailure
        var ciAction = MonitorTransitions.BuildTerminalAction(state, TerminalStateType.CiFailure);
        if (ciAction.Choices != null) allChoices.UnionWith(ciAction.Choices);

        // Investigation results (with suggested fix)
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

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Human reviewer → track as waiting-for-reply, don't auto-resolve
        Assert.Single(state.WaitingForReplyComments);
        Assert.True(state.WaitingForReplyComments[0].IsWaitingForReply);
        Assert.False(state.PendingResolveAfterAddress);
        // Single comment, last from this reviewer → re-request review
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("request_review", action.Task);
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

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Should track first comment as waiting-for-reply
        Assert.Single(state.WaitingForReplyComments);
        Assert.Equal("c1", state.WaitingForReplyComments[0].Id);
        // Last comment from this reviewer → re-request review first
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("request_review", action.Task);
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

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Last comment from this reviewer → re-request review first
        Assert.Equal("auto_execute", action.Action);
        Assert.Equal("request_review", action.Task);
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

        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // Should track as waiting-for-reply
        Assert.Single(state.WaitingForReplyComments);
        // Reviewer has another comment (c2) → no re-request yet, advance to next
        Assert.Equal("ask_user", action.Action);
        Assert.Null(state.PendingReRequestReviewer);
    }

    [Fact]
    public void CommentReplied_HumanReviewer_PostReRequest_AdvancesCorrectly()
    {
        // After re-request completes for human, should advance with correct summary
        var state = CreateState();
        state.CurrentState = MonitorStateId.ExecutingTask;
        state.CommentFlow = CommentFlowState.SingleCommentPrompt;
        state.UnresolvedComments = [
            MakeComment("c1", "human-reviewer"),
            MakeComment("c2", "different-reviewer", "src/Other.cs")
        ];
        state.CurrentCommentIndex = 0;

        // comment_replied → re-request (last from this reviewer)
        var action = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Equal("request_review", action.Task);
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

        // Reply to c1 — c2 still needs action → no re-request
        var action1 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);
        Assert.Single(state.WaitingForReplyComments);
        Assert.Equal("ask_user", action1.Action);
        Assert.Null(state.PendingReRequestReviewer);

        // Simulate advancing to c2 and replying
        state.CurrentCommentIndex = 1;
        state.CurrentState = MonitorStateId.ExecutingTask;
        var action2 = MonitorTransitions.ProcessEvent(state, "comment_replied", null, null);

        // c1 is already waiting-for-reply → no remaining work → re-request
        Assert.Equal(2, state.WaitingForReplyComments.Count);
        Assert.Equal("auto_execute", action2.Action);
        Assert.Equal("request_review", action2.Task);
        Assert.Equal("human-reviewer", state.PendingReRequestReviewer);
    }

    #endregion

    #region Deferred Rerun (pending checks)

    [Fact]
    public void BuildRerunAction_WithInProgressChecks_DefersToPolling()
    {
        var state = CreateState();
        state.CurrentState = MonitorStateId.AwaitingUser;
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
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
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
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
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
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
        state.CiFailureFlow = CiFailureFlowState.CiFailurePrompt;
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

    #endregion
}
