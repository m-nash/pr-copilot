// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

var captureFile = Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_CAPTURE")
    ?? throw new InvalidOperationException("Missing test argument capture file.");
var startupDirectory = Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_START_BARRIER");
if (!string.IsNullOrEmpty(startupDirectory) &&
    args is ["api", var startupEndpoint, "--jq", _] &&
    startupEndpoint.EndsWith("/pulls/42", StringComparison.Ordinal))
{
    var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var watcher = new FileSystemWatcher(startupDirectory, "release");
    watcher.Created += (_, _) => released.TrySetResult();
    watcher.EnableRaisingEvents = true;
    var entered = Path.Combine(startupDirectory, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".entered");
    File.WriteAllText(entered, JsonSerializer.Serialize(args));
    if (File.Exists(Path.Combine(startupDirectory, "release")))
        released.TrySetResult();
    await released.Task.WaitAsync(TimeSpan.FromSeconds(20));
    if (Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_START_SUCCESS") == "1")
    {
        Console.Write("""{"title":"Fixture PR","body":"","sha":"fixture-sha","head_branch":"fixture","base_branch":"","url":"https://github.com/owner/repo/pull/42","author":"fixture-author","mergeable":true,"mergeable_state":"clean","state":"open","merged":false}""");
        return 0;
    }
    Console.Error.WriteLine("fixture startup failure");
    return 1;
}

File.AppendAllText(captureFile, JsonSerializer.Serialize(args) + Environment.NewLine);

var resolveDirectory = Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_RESOLVE_BARRIER");
if (!string.IsNullOrEmpty(resolveDirectory))
{
    if (args is ["api", "graphql", "-f", var query] && query.Contains("resolveReviewThread", StringComparison.Ordinal))
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(resolveDirectory, "release");
        watcher.Created += (_, _) => released.TrySetResult();
        watcher.EnableRaisingEvents = true;
        File.WriteAllText(Path.Combine(resolveDirectory, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".entered"),
            JsonSerializer.Serialize(args));
        if (File.Exists(Path.Combine(resolveDirectory, "release")))
            released.TrySetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.Write("""{"data":{"resolveReviewThread":{"thread":{"isResolved":true}}}}""");
        return 0;
    }

    if (args is ["api", var replyEndpoint, "-X", "POST", "--input", _] &&
        replyEndpoint.EndsWith("/replies", StringComparison.Ordinal))
    {
        Console.Write("{}");
        return 0;
    }
}

if (Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_START_SUCCESS") == "1" &&
    args.Length >= 2 && args[0] == "api")
{
    var response = args[1] switch
    {
        var queryEndpoint when queryEndpoint.EndsWith("/check-runs?per_page=100", StringComparison.Ordinal) => """{"check_runs":[]}""",
        var queryEndpoint when queryEndpoint.EndsWith("/status", StringComparison.Ordinal) => """{"statuses":[]}""",
        var queryEndpoint when queryEndpoint.EndsWith("/reviews?per_page=100", StringComparison.Ordinal) => """[{"user":"Copilot","state":"COMMENTED","commit_id":"fixture-sha"}]""",
        "graphql" => """{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}""",
        "user" => "fixture-author",
        _ => null
    };
    if (response != null)
    {
        Console.Write(response);
        return 0;
    }
}

if (args is ["api", var endpoint, "--paginate", "--slurp"] &&
    endpoint.EndsWith("/files?per_page=100", StringComparison.Ordinal))
{
    var responseFile = Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_RESPONSE")
        ?? throw new InvalidOperationException("Missing test response file.");
    var exitCode = int.Parse(Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_EXIT_CODE") ?? "0", CultureInfo.InvariantCulture);
    var writer = exitCode == 0 ? Console.Out : Console.Error;
    writer.Write(File.ReadAllText(responseFile));
    return exitCode;
}

Console.Error.WriteLine(args is ["pr", "diff", ..]
    ? "accepts at most 1 arg(s), received 2"
    : "Unexpected GitHub CLI command in offline test.");
return 1;
