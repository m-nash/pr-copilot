// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

/// <summary>
/// Info about a PR found via search (for "monitor all my PRs" feature).
/// </summary>
public class UserPrInfo
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}
