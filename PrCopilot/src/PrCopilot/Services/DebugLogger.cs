// Licensed under the MIT License.

using System.Text;

namespace PrCopilot.Services;

/// <summary>
/// Lightweight static logger that writes to the PR-scoped debug log file.
/// The viewer's debug panel tails this file. Format: DEBUG|timestamp|[source] message
/// All I/O is fire-and-forget with try/catch — never crashes the server.
/// </summary>
public static class DebugLogger
{
    private static string? _debugLogPath;
    private static readonly object _lock = new();
    private static string? _fallbackLogPath;

    /// <summary>
    /// Initialize with the PR-scoped debug log path.
    /// Called from pr_monitor_start when we know the session folder.
    /// </summary>
    public static void Init(string debugLogPath)
    {
        _debugLogPath = debugLogPath;
    }

    /// <summary>
    /// Set a fallback log path for crashes before Init() is called.
    /// Typically ~/.copilot/pr-copilot-server.log
    /// </summary>
    public static void SetFallbackPath(string fallbackPath)
    {
        _fallbackLogPath = fallbackPath;
        try
        {
            var dir = Path.GetDirectoryName(fallbackPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
        catch { }
    }

    public static void Log(string source, string message)
    {
        Write("DEBUG", source, message);
    }

    public static void Error(string source, string message)
    {
        Write("ERROR", source, message);
    }

    public static void Error(string source, Exception ex)
    {
        Write("ERROR", source, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    private static void Write(string level, string source, string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("hh:mm:ss tt");
            var line = $"{level}|{timestamp}|[{source}] {message}{Environment.NewLine}";

            lock (_lock)
            {
                var path = _debugLogPath ?? _fallbackLogPath;
                if (path == null) return;

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                writer.Write(line);
            }
        }
        catch { /* never crash the server */ }
    }
}
