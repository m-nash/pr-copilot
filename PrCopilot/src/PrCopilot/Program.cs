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

// Startup cleanup: try to delete PrCopilot.old*.exe left by install/update rename pattern
// Don't fail — other CLI sessions may still be using older versions.
foreach (var old in Directory.GetFiles(AppContext.BaseDirectory, "PrCopilot.old*.exe"))
{
    try { File.Delete(old); } catch { }
}

// --version: print version and exit
if (args.Contains("--version"))
{
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"pr-copilot {version}");
    return;
}

// --update: self-update from latest GitHub release
if (args.Contains("--update"))
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "pr-copilot-updater");
    http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

    Console.WriteLine("🔍 Checking for latest release...");
    var releaseJson = await http.GetStringAsync("https://api.github.com/repos/m-nash/pr-copilot/releases/latest");
    var release = JsonNode.Parse(releaseJson)!;
    var tagName = release["tag_name"]!.GetValue<string>();
    Console.WriteLine($"📦 Latest release: {tagName}");

    var asset = release["assets"]!.AsArray()
        .FirstOrDefault(a => a!["name"]!.GetValue<string>().Contains("win-x64") &&
                             a!["name"]!.GetValue<string>().EndsWith(".zip"));
    if (asset == null)
    {
        Console.Error.WriteLine("❌ No win-x64 zip asset found in latest release.");
        return;
    }

    var downloadUrl = asset["browser_download_url"]!.GetValue<string>();
    var zipPath = Path.Combine(Path.GetTempPath(), $"pr-copilot-{tagName}.zip");

    Console.WriteLine($"⬇️  Downloading {asset["name"]!.GetValue<string>()}...");
    var zipBytes = await http.GetByteArrayAsync(downloadUrl);
    await File.WriteAllBytesAsync(zipPath, zipBytes);

    var installDir = AppContext.BaseDirectory;
    var currentExe = Path.Combine(installDir, "PrCopilot.exe");

    // Rename current exe with next available .old.N.exe suffix
    if (File.Exists(currentExe))
    {
        var n = 0;
        string backupPath;
        do
        {
            backupPath = n == 0
                ? Path.Combine(installDir, "PrCopilot.old.exe")
                : Path.Combine(installDir, $"PrCopilot.old.{n}.exe");
            n++;
        } while (File.Exists(backupPath));
        File.Move(currentExe, backupPath);
    }

    Console.WriteLine($"📂 Extracting to {installDir}...");
    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, installDir, overwriteFiles: true);
    File.Delete(zipPath);

    // Write version sidecar for viewer update detection
    var releaseVersion = tagName.TrimStart('v');
    File.WriteAllText(Path.Combine(installDir, "version.txt"), releaseVersion);

    Console.WriteLine("⚙️  Running setup...");
    var newExe = Path.Combine(installDir, "PrCopilot.exe");
    var setupProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = newExe,
        Arguments = "--setup",
        UseShellExecute = false
    });
    setupProcess?.WaitForExit();

    Console.WriteLine();
    Console.WriteLine($"✅ Updated to {tagName}! Restart your Copilot CLI session.");
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

    // Write PID file so the MCP server can detect this viewer is running
    var pidFile = logFile + ".viewer.pid";
    File.WriteAllText(pidFile, Environment.ProcessId.ToString());

    var debugIdx = Array.IndexOf(args, "--debug");
    var debugFile = (debugIdx >= 0 && debugIdx + 1 < args.Length) ? args[debugIdx + 1] : null;

    try
    {
        MonitorViewer.Run(prNumber, logFile, triggerFile, debugFile);
    }
    finally
    {
        try { File.Delete(pidFile); } catch { }
    }
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
