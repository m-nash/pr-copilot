// Licensed under the MIT License.

namespace PrCopilot.StateMachine;

public class PrInfo
{
    public string Title { get; set; } = "";
    public string HeadSha { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string Url { get; set; } = "";
    public string Author { get; set; } = "";
    public bool Mergeable { get; set; }
    public string MergeableState { get; set; } = "";
    public bool IsMerged { get; set; }
    public string State { get; set; } = ""; // open, closed, merged
}
