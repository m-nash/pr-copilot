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
    WaitingForManualHandling
}
