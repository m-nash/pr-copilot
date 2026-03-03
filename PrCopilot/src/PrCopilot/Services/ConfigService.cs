// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrCopilot.Services;

/// <summary>
/// Reads and manages pr-copilot-config.json in ~/.copilot/.
/// All settings default to true-by-default for auto-update.
/// </summary>
public static class ConfigService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "pr-copilot-config.json");

    /// <summary>
    /// Returns whether auto-update is enabled. Defaults to true if the
    /// config file is missing or the key is absent.
    /// </summary>
    public static bool AutoUpdate
    {
        get
        {
            if (!File.Exists(ConfigPath))
                return true;

            try
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonNode.Parse(json);
                return config?["autoUpdate"]?.GetValue<bool>() ?? true;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Creates pr-copilot-config.json with defaults if it doesn't already exist.
    /// </summary>
    public static void EnsureConfigExists()
    {
        if (File.Exists(ConfigPath))
            return;

        var config = new JsonObject
        {
            ["autoUpdate"] = true
        };

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Returns the path to the config file, for display in setup output.
    /// </summary>
    public static string GetConfigPath() => ConfigPath;
}
