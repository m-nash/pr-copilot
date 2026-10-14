// Licensed under the MIT License.

using System.Text.Json;

namespace PrCopilot.Tests;

internal sealed class FakeGitHubCliFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PrCopilot.Tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _originalEnvironment = new();
    private readonly string _responseFile;
    private readonly string _captureFile;
    private readonly string _startupDirectory;
    private bool _disposed;

    public FakeGitHubCliFixture()
    {
        var executableDirectory = AppContext.BaseDirectory;
        var executable = Path.Combine(executableDirectory, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        Assert.True(File.Exists(executable), $"Fake GitHub CLI was not built: {executable}");

        Directory.CreateDirectory(_directory);
        _responseFile = Path.Combine(_directory, "response.json");
        _captureFile = Path.Combine(_directory, "arguments.jsonl");
        _startupDirectory = Path.Combine(_directory, "startup");
        File.WriteAllText(_responseFile, "[]");

        SetEnvironment("PATH", executableDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        SetEnvironment("PR_COPILOT_TEST_GH_RESPONSE", _responseFile);
        SetEnvironment("PR_COPILOT_TEST_GH_CAPTURE", _captureFile);
        SetEnvironment("PR_COPILOT_TEST_GH_EXIT_CODE", "0");
        SetEnvironment("PR_COPILOT_TEST_GH_START_BARRIER", "");
        SetEnvironment("PR_COPILOT_TEST_GH_START_SUCCESS", "");
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

    public void PauseStartupRequests()
    {
        Directory.CreateDirectory(_startupDirectory);
        Environment.SetEnvironmentVariable("PR_COPILOT_TEST_GH_START_BARRIER", _startupDirectory);
    }

    public string DirectoryPath => _directory;

    public void CompleteStartupSuccessfully() =>
        Environment.SetEnvironmentVariable("PR_COPILOT_TEST_GH_START_SUCCESS", "1");

    public IReadOnlyList<string[]> ReadStartupRequests() =>
        Directory.GetFiles(_startupDirectory, "*.entered")
            .Select(file => JsonSerializer.Deserialize<string[]>(File.ReadAllText(file))!).ToArray();

    public async Task WaitForStartupRequestsAsync(int count, CancellationToken cancellationToken = default)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_startupDirectory, "*.entered");
        watcher.Created += (_, _) =>
        {
            if (Directory.GetFiles(_startupDirectory, "*.entered").Length >= count)
                arrived.TrySetResult();
        };
        watcher.EnableRaisingEvents = true;
        if (Directory.GetFiles(_startupDirectory, "*.entered").Length >= count)
            arrived.TrySetResult();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public void ReleaseStartupRequests() =>
        File.WriteAllText(Path.Combine(_startupDirectory, "release"), "");

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
