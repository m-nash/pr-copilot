// Licensed under the MIT License.

using System.Text.Json;

namespace PrCopilot.Tests;

public class BuildDependencyTests
{
    [Fact]
    public void RestoredGitBuildTasks_UsePatchedVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PrCopilot", "PrCopilot.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var assetsPath = Path.Combine(directory.FullName, "artifacts", "obj", "PrCopilot", "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        const string prefix = "Microsoft.Build.Tasks.Git/";
        var dependency = Assert.Single(assets.RootElement.GetProperty("libraries").EnumerateObject(),
            package => package.Name.StartsWith(prefix, StringComparison.Ordinal));
        var version = Version.Parse(dependency.Name[prefix.Length..]);

        Assert.True(version >= new Version(10, 0, 401),
            $"Restored {dependency.Name}; use patched build tasks instead of a vulnerable dependency.");
    }
}
