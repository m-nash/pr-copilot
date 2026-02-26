// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using PrCopilot.Services;
using PrCopilot.Tools;
using PrCopilot.Viewer;

// --version: print version and exit
if (args.Contains("--version"))
{
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"pr-copilot {version}");
    return;
}

// --setup: write SKILL.md + mcp-config.json and exit
if (args.Contains("--setup"))
{
    var exePath = Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "PrCopilot.exe");
    var copilotHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    // 1. Extract embedded SKILL.md
    var skillDir = Path.Combine(copilotHome, "skills", "pr-monitor");
    Directory.CreateDirectory(skillDir);
    var skillPath = Path.Combine(skillDir, "SKILL.md");
    var assembly = Assembly.GetExecutingAssembly();
    var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("SKILL.md"));
    if (resourceName != null)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var fs = File.Create(skillPath);
        stream.CopyTo(fs);
        Console.WriteLine($"✅ SKILL.md → {skillPath}");
    }
    else
    {
        Console.Error.WriteLine("⚠️ SKILL.md resource not found in assembly");
    }

    // 2. Update mcp-config.json
    var configPath = Path.Combine(copilotHome, "mcp-config.json");
    JsonObject root;
    if (File.Exists(configPath))
    {
        root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject();
    }
    else
    {
        root = new JsonObject();
    }

    var servers = root["mcpServers"]?.AsObject() ?? new JsonObject();
    root["mcpServers"] = servers;

    servers["pr-copilot"] = new JsonObject
    {
        ["command"] = exePath.Replace("/", "\\"),
        ["args"] = new JsonArray(),
        ["timeout"] = 3600000
    };

    File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"✅ mcp-config.json → {configPath}");
    Console.WriteLine($"   command: {exePath}");
    Console.WriteLine();
    Console.WriteLine("💡 Optional: Install Playwright MCP for full CI rerun automation:");
    Console.WriteLine("   copilot mcp add playwright -- npx -y @playwright/mcp@latest --browser msedge");
    return;
}

// Check for --viewer mode
if (args.Contains("--viewer"))
{
    var prIdx = Array.IndexOf(args, "--pr");
    var logIdx = Array.IndexOf(args, "--log");
    var triggerIdx = Array.IndexOf(args, "--trigger");

    if (prIdx < 0 || logIdx < 0 || triggerIdx < 0 ||
        prIdx + 1 >= args.Length || logIdx + 1 >= args.Length || triggerIdx + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: PrCopilot.exe --viewer --pr <number> --log <path> --trigger <path>");
        return;
    }

    var prNumber = int.Parse(args[prIdx + 1]);
    var logFile = args[logIdx + 1];
    var triggerFile = args[triggerIdx + 1];
    Console.Title = $"PrCopilot Viewer #{prNumber}";

    var debugIdx = Array.IndexOf(args, "--debug");
    var debugFile = (debugIdx >= 0 && debugIdx + 1 < args.Length) ? args[debugIdx + 1] : null;

    MonitorViewer.Run(prNumber, logFile, triggerFile, debugFile);
    return;
}

// Normal MCP server mode
Console.Title = "PrCopilot MCP Server";

// Set up fallback crash log before anything else
var copilotDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
DebugLogger.SetFallbackPath(Path.Combine(copilotDir, "pr-copilot-server.log"));
DebugLogger.Log("Startup", "MCP server starting");

// Global exception handlers — write to log before process dies
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    DebugLogger.Error("FATAL", (Exception)e.ExceptionObject);
};
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    DebugLogger.Error("UnobservedTask", e.Exception);
    e.SetObserved();
};

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
        options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    // On shutdown, write STOPPED to all active monitor logs so viewers close gracefully
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        DebugLogger.Log("Shutdown", "MCP server stopping, notifying viewers");
        MonitorFlowTools.NotifyShutdown();
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    DebugLogger.Error("Startup", ex);
    throw;
}
