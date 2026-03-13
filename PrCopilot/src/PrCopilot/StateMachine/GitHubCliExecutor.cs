// Licensed under the MIT License.

using System.Diagnostics;
using PrCopilot.Services;

namespace PrCopilot.StateMachine;

/// <summary>
/// Executes deterministic GitHub CLI commands that don't require LLM involvement.
/// Used by MonitorFlowTools when the state machine returns action="auto_execute".
/// </summary>
public static class GitHubCliExecutor
{
    /// <summary>
    /// Resolve a review thread via GraphQL mutation.
    /// </summary>
    public static async Task<(bool success, string output)> ResolveThreadAsync(string threadId)
    {
        var query = $"mutation {{ resolveReviewThread(input: {{threadId: \\\"{threadId}\\\"}}) {{ thread {{ isResolved }} }} }}";
        return await RunGhAsync($"api graphql -f query=\"{query}\"");
    }

    /// <summary>
    /// Re-request a review from a specific user via the GitHub API.
    /// </summary>
    public static async Task<(bool success, string output)> RequestReviewAsync(string owner, string repo, int prNumber, string reviewer)
    {
        return await RunGhAsync($"api repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers -X POST -f \"reviewers[]={reviewer}\"");
    }

    /// <summary>
    /// Merge a PR using squash merge.
    /// </summary>
    public static async Task<(bool success, string output)> MergePrAsync(string owner, string repo, int prNumber)
    {
        return await RunGhAsync($"pr merge {prNumber} --squash --repo {owner}/{repo}");
    }

    /// <summary>
    /// Merge a PR using squash merge with --admin flag to bypass branch protection.
    /// </summary>
    public static async Task<(bool success, string output)> MergePrAdminAsync(string owner, string repo, int prNumber)
    {
        return await RunGhAsync($"pr merge {prNumber} --squash --admin --repo {owner}/{repo}");
    }

    /// <summary>
    /// Push an empty commit to the PR's head branch via Git Data API.
    /// This triggers a fresh CI run without any code changes.
    /// </summary>
    public static async Task<(bool success, string output)> PushEmptyCommitAsync(string owner, string repo, string branch, string headSha)
    {
        // 1. Get the tree SHA from the current HEAD commit
        var (treeSuccess, treeJson) = await RunGhAsync(
            $"api repos/{owner}/{repo}/git/commits/{headSha} --jq .tree.sha");
        if (!treeSuccess)
            return (false, $"Failed to get tree SHA: {treeJson}");

        var treeSha = treeJson.Trim();

        // 2. Create a new commit with the same tree (empty commit)
        var (commitSuccess, commitJson) = await RunGhAsync(
            $"api repos/{owner}/{repo}/git/commits -X POST " +
            $"-f \"message=Trigger new CI run\" " +
            $"-f \"tree={treeSha}\" " +
            $"-f \"parents[]={headSha}\"");
        if (!commitSuccess)
            return (false, $"Failed to create commit: {commitJson}");

        // Parse new commit SHA
        string newSha;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(commitJson);
            newSha = doc.RootElement.GetProperty("sha").GetString() ?? "";
        }
        catch
        {
            return (false, $"Failed to parse commit response: {commitJson}");
        }

        // 3. Update the branch ref to point to the new commit
        var (updateSuccess, updateJson) = await RunGhAsync(
            $"api repos/{owner}/{repo}/git/refs/heads/{branch} -X PATCH -f \"sha={newSha}\"");
        if (!updateSuccess)
            return (false, $"Failed to update branch ref: {updateJson}");

        return (true, $"Empty commit {newSha[..7]} pushed to {branch}");
    }

    private static async Task<(bool success, string output)> RunGhAsync(string arguments)
    {
        DebugLogger.Log("GhCli", $"Running: gh {arguments}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // isolate child from parent's MCP stdin pipe
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["GH_PROMPT_DISABLED"] = "1";
            psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start gh process");

            process.StandardInput.Close(); // signal EOF so gh doesn't wait on stdin

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Error("GhCli", $"TIMEOUT after 30s: gh {arguments}");
                try { process.Kill(); } catch { }
                return (false, $"gh CLI timed out after 30s");
            }

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            var success = process.ExitCode == 0;
            var output = success ? stdout.Trim() : $"{stderr.Trim()} {stdout.Trim()}".Trim();
            DebugLogger.Log("GhCli", $"Exit={process.ExitCode}, output={Truncate(output, 200)}");
            return (success, output);
        }
        catch (Exception ex)
        {
            DebugLogger.Error("GhCli", ex);
            return (false, ex.Message);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
