// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Sub-states for the comment addressing flow.
/// </summary>
public enum CommentFlowState
{
    None,
    SingleCommentPrompt,
    MultiCommentPrompt,
    AddressAllIterating,
    ExplainAllIterating,
    PickComment,
    PickRemaining,
    WaitingForManualHandling,

    /// <summary>Auto mode: the agent is handling a batch of comments and will report comments_processed.</summary>
    AutoAddressing,

    /// <summary>Auto mode was turned off before the batch results were posted; waiting for the user to confirm them.</summary>
    AutoResultsConfirmation
}
