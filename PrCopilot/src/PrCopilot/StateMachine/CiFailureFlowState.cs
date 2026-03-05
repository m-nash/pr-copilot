// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Sub-states for the CI failure investigation flow.
/// </summary>
public enum CiFailureFlowState
{
    None,
    CiFailurePrompt,
    Investigating,
    InvestigationResults
}
