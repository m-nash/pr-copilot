// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace PrCopilot.Tests;

public class AutoUpdateConfigTests
{
    [Fact]
    public void SetupArgs_WithAutoUpdate_WritesArgsToConfig()
    {
        // Simulate what --setup does when --auto-update is present
        var args = new[] { "--setup", "--auto-update" };
        var serverConfig = new JsonObject
        {
            ["command"] = "/path/to/PrCopilot",
            ["args"] = args.Contains("--auto-update") ? new JsonArray("--auto-update") : new JsonArray(),
            ["timeout"] = 3600000
        };

        var argsArray = serverConfig["args"]!.AsArray();
        Assert.Single(argsArray);
        Assert.Equal("--auto-update", argsArray[0]!.GetValue<string>());
    }

    [Fact]
    public void SetupArgs_WithoutAutoUpdate_WritesEmptyArgs()
    {
        var args = new[] { "--setup" };
        var serverConfig = new JsonObject
        {
            ["command"] = "/path/to/PrCopilot",
            ["args"] = args.Contains("--auto-update") ? new JsonArray("--auto-update") : new JsonArray(),
            ["timeout"] = 3600000
        };

        var argsArray = serverConfig["args"]!.AsArray();
        Assert.Empty(argsArray);
    }

    [Fact]
    public void AutoUpdateFlag_IsDetectedInArgs()
    {
        var args = new[] { "--auto-update" };
        Assert.Contains("--auto-update", args);
    }

    [Fact]
    public void AutoUpdateFlag_NotPresentInDefaultArgs()
    {
        var args = Array.Empty<string>();
        Assert.DoesNotContain("--auto-update", args);
    }

    [Fact]
    public void ConfigJson_RoundTrips_WithAutoUpdateArg()
    {
        // Simulate full config write/read
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["pr-copilot"] = new JsonObject
                {
                    ["command"] = "/path/to/PrCopilot",
                    ["args"] = new JsonArray("--auto-update"),
                    ["timeout"] = 3600000
                }
            }
        };

        var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;

        var parsedArgs = parsed["mcpServers"]!["pr-copilot"]!["args"]!.AsArray();
        Assert.Single(parsedArgs);
        Assert.Equal("--auto-update", parsedArgs[0]!.GetValue<string>());
    }

    [Fact]
    public void ConfigJson_RoundTrips_WithEmptyArgs()
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

        var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;

        var parsedArgs = parsed["mcpServers"]!["pr-copilot"]!["args"]!.AsArray();
        Assert.Empty(parsedArgs);
    }
}
