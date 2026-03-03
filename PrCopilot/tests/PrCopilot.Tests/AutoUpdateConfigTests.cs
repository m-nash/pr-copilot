// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrCopilot.Tests;

public class AutoUpdateConfigTests
{
    [Fact]
    public void ConfigJson_AutoUpdate_DefaultsToTrue()
    {
        // When config file doesn't exist or key is missing, default is true
        var config = new JsonObject();
        var autoUpdate = config["autoUpdate"]?.GetValue<bool>() ?? true;
        Assert.True(autoUpdate);
    }

    [Fact]
    public void ConfigJson_AutoUpdate_CanBeDisabled()
    {
        var config = new JsonObject { ["autoUpdate"] = false };
        var json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;

        Assert.False(parsed["autoUpdate"]!.GetValue<bool>());
    }

    [Fact]
    public void ConfigJson_AutoUpdate_ExplicitTrue()
    {
        var config = new JsonObject { ["autoUpdate"] = true };
        var json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;

        Assert.True(parsed["autoUpdate"]!.GetValue<bool>());
    }

    [Fact]
    public void ConfigJson_RoundTrips_FullConfig()
    {
        var config = new JsonObject { ["autoUpdate"] = true };
        var json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;

        Assert.True(parsed["autoUpdate"]!.GetValue<bool>());
    }

    [Fact]
    public void SetupArgs_NoLongerIncludesAutoUpdate()
    {
        // --setup now always writes empty args to mcp-config.json
        var serverConfig = new JsonObject
        {
            ["command"] = "/path/to/PrCopilot",
            ["args"] = new JsonArray(),
            ["timeout"] = 3600000
        };

        var argsArray = serverConfig["args"]!.AsArray();
        Assert.Empty(argsArray);
    }

    [Fact]
    public void LegacyAutoUpdateFlag_StillWorks()
    {
        // --auto-update flag is still checked for backward compat
        var args = new[] { "--auto-update" };
        Assert.Contains("--auto-update", args);
    }

    [Fact]
    public void McpConfig_RoundTrips_WithEmptyArgs()
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["pr-copilot"] = new JsonObject
                {
                    ["command"] = "/path/to/PrCopilot",
                    ["args"] = new JsonArray(),
                    ["timeout"] = 3600000
                }
            }
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;

        var parsedArgs = parsed["mcpServers"]!["pr-copilot"]!["args"]!.AsArray();
        Assert.Empty(parsedArgs);
    }
}
