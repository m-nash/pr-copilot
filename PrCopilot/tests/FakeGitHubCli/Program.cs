// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

var captureFile = Environment.GetEnvironmentVariable("PR_COPILOT_TEST_GH_CAPTURE")
    ?? throw new InvalidOperationException("Missing test argument capture file.");
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
