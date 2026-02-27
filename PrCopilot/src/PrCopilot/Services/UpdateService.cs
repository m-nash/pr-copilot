// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PrCopilot.Services;

/// <summary>
/// Handles checking for and applying updates from GitHub releases.
/// Used by both --update (CLI) and --auto-update (background on startup).
/// </summary>
public static class UpdateService
{
    private const string ReleaseUrl = "https://api.github.com/repos/m-nash/pr-copilot/releases/latest";

    /// <summary>
    /// Checks for a newer release and installs it if available.
    /// Returns the new version tag if updated, or null if already up to date.
    /// </summary>
    public static async Task<string?> CheckAndApplyUpdate(Action<string>? log = null)
    {
        log ??= _ => { };

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exeName = isWindows ? "PrCopilot.exe" : "PrCopilot";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "pr-copilot-updater");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        log("Checking for latest release...");
        var releaseJson = await http.GetStringAsync(ReleaseUrl);
        var release = JsonNode.Parse(releaseJson)!;
        var tagName = release["tag_name"]!.GetValue<string>();
        var releaseVersion = tagName.TrimStart('v');

        // Compare with current running version
        var currentVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        if (!VersionComparer.IsNewer(currentVersion, releaseVersion))
        {
            log($"Already up to date ({currentVersion}).");
            return null;
        }

        log($"New version available: {tagName} (current: {currentVersion})");

        var rid = RuntimeInformation.RuntimeIdentifier;
        if (rid.StartsWith("osx") || rid.StartsWith("macos"))
            rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        else if (rid.StartsWith("win"))
            rid = "win-x64";

        var asset = release["assets"]!.AsArray()
            .FirstOrDefault(a => a!["name"]!.GetValue<string>().Contains(rid) &&
                                 a!["name"]!.GetValue<string>().EndsWith(".zip"));
        if (asset == null)
        {
            log($"No {rid} zip asset found in release {tagName}.");
            return null;
        }

        var downloadUrl = asset["browser_download_url"]!.GetValue<string>();
        var zipPath = Path.Combine(Path.GetTempPath(), $"pr-copilot-{tagName}.zip");

        log($"Downloading {asset["name"]!.GetValue<string>()}...");
        var zipBytes = await http.GetByteArrayAsync(downloadUrl);
        await File.WriteAllBytesAsync(zipPath, zipBytes);

        var installDir = AppContext.BaseDirectory;
        var currentExe = Path.Combine(installDir, exeName);

        // Rename current exe with next available .old.N suffix
        if (File.Exists(currentExe))
        {
            var n = 0;
            string backupPath;
            var ext = isWindows ? ".exe" : "";
            do
            {
                backupPath = n == 0
                    ? Path.Combine(installDir, $"PrCopilot.old{ext}")
                    : Path.Combine(installDir, $"PrCopilot.old.{n}{ext}");
                n++;
            } while (File.Exists(backupPath));
            File.Move(currentExe, backupPath);
        }

        log($"Extracting to {installDir}...");
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, installDir, overwriteFiles: true);
        File.Delete(zipPath);

        // Write version sidecar for viewer update detection
        File.WriteAllText(Path.Combine(installDir, "version.txt"), releaseVersion);

        // Make binary executable on Unix
        if (!isWindows)
        {
            var newBin = Path.Combine(installDir, exeName);
            System.Diagnostics.Process.Start("chmod", $"+x \"{newBin}\"")?.WaitForExit();
        }

        log($"Updated to {tagName}.");
        return tagName;
    }
}
