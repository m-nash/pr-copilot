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
    Console.Error.WriteLine("fixture startup failure");
    return 1;
}

File.AppendAllText(captureFile, JsonSerializer.Serialize(args) + Environment.NewLine);

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
