// Licensed under the MIT License.

using System.Text.Json;

namespace PrCopilot.Tests;

internal sealed class FakeGitHubCliFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PrCopilot.Tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _originalEnvironment = new();
    private readonly string _responseFile;
    private readonly string _captureFile;
    private bool _disposed;

    public FakeGitHubCliFixture()
    {
        var executableDirectory = AppContext.BaseDirectory;
        var executable = Path.Combine(executableDirectory, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        Assert.True(File.Exists(executable), $"Fake GitHub CLI was not built: {executable}");

        Directory.CreateDirectory(_directory);
        _responseFile = Path.Combine(_directory, "response.json");
        _captureFile = Path.Combine(_directory, "arguments.jsonl");
        File.WriteAllText(_responseFile, "[]");

        SetEnvironment("PATH", executableDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        SetEnvironment("PR_COPILOT_TEST_GH_RESPONSE", _responseFile);
        SetEnvironment("PR_COPILOT_TEST_GH_CAPTURE", _captureFile);
        SetEnvironment("PR_COPILOT_TEST_GH_EXIT_CODE", "0");
    }

    public void Respond(string response, int exitCode = 0)
    {
        File.WriteAllText(_responseFile, response);
        Environment.SetEnvironmentVariable("PR_COPILOT_TEST_GH_EXIT_CODE", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<string[]> ReadRequests() =>
        File.Exists(_captureFile)
            ? File.ReadAllLines(_captureFile).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray()
            : [];

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var (name, value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_directory, recursive: true);
        _disposed = true;
    }

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
}
