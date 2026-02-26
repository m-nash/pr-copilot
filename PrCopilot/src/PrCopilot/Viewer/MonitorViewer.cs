// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace PrCopilot.Viewer;

/// <summary>
/// Terminal.Gui-based TUI dashboard for PR monitor status.
/// Launched via: PrCopilot.exe --viewer --pr [N] --log [path] --trigger [path]
/// </summary>
public static class MonitorViewer
{
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] UserEmojis =
    [
        "🦊", "🐙", "🦉", "🐬", "🦁", "🐝", "🦋", "🐢", "🐳", "🦅",
        "🐺", "🦈", "🐘", "🦜", "🐧", "🦎", "🐌", "🦩", "🐸", "🦇"
    ];
    private static readonly Dictionary<string, string> _userEmojiCache = new();

    private static string GetUserEmoji(string username)
    {
        if (_userEmojiCache.TryGetValue(username, out var cached))
            return cached;
        // Stable hash: sum of char codes mod pool size
        var hash = username.Aggregate(0, (acc, c) => acc + c);
        var emoji = UserEmojis[Math.Abs(hash) % UserEmojis.Length];
        _userEmojiCache[username] = emoji;
        return emoji;
    }

    public static void Run(int prNumber, string logFile, string triggerFile, string? debugFile = null)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Application.Init(driverName: "NetDriver");

        var darkScheme = new ColorScheme
        {
            Normal = new Attribute(Color.White, Color.Black),
            Focus = new Attribute(Color.BrightGreen, Color.Black),
            HotNormal = new Attribute(Color.BrightYellow, Color.Black),
            HotFocus = new Attribute(Color.BrightCyan, Color.Black),
            Disabled = new Attribute(Color.DarkGray, Color.Black),
        };

        var cyanScheme = new ColorScheme
        {
            Normal = new Attribute(Color.BrightCyan, Color.Black),
            Focus = new Attribute(Color.BrightCyan, Color.Black),
            HotNormal = new Attribute(Color.BrightCyan, Color.Black),
            HotFocus = new Attribute(Color.BrightCyan, Color.Black),
            Disabled = new Attribute(Color.DarkGray, Color.Black),
        };

        var greenScheme = new ColorScheme
        {
            Normal = new Attribute(Color.BrightGreen, Color.Black),
        };

        var dimScheme = new ColorScheme
        {
            Normal = new Attribute(Color.DarkGray, Color.Black),
        };

        Console.Title = $"PR Monitor for #{prNumber}";

        var window = new Window
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = darkScheme
        };

        // --- Header: PR link ---
        var headerText = $"PR #{prNumber}";
        string? prUrl = null;
        if (File.Exists(logFile))
        {
            try
            {
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var firstLine = sr.ReadLine() ?? "";
                if (firstLine.Contains("http"))
                {
                    var urlMatch = Regex.Match(firstLine, @"(https?://\S+)");
                    if (urlMatch.Success) prUrl = urlMatch.Groups[1].Value;
                    // Display text before the | separator (e.g., "#56411 - Fix token refresh")
                    var pipeIdx = firstLine.IndexOf('|');
                    headerText = pipeIdx > 0
                        ? firstLine[..pipeIdx].TrimStart('\uFEFF').Trim()
                        : firstLine.TrimStart('\uFEFF');
                }
            }
            catch { }
        }

        var linkScheme = new ColorScheme
        {
            Normal = new Attribute(Color.BrightCyan, Color.Black),
            Focus = new Attribute(Color.White, Color.BrightBlue),
            HotNormal = new Attribute(Color.BrightCyan, Color.Black),
            HotFocus = new Attribute(Color.White, Color.BrightBlue),
            Disabled = new Attribute(Color.DarkGray, Color.Black),
        };

        var headerButton = new Button
        {
            Text = $"🔗 {headerText}",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            ColorScheme = linkScheme,
            NoPadding = true,
            NoDecorations = true,
            CanFocus = false
        };
        var headerLoaded = prUrl != null; // track whether we got the enriched header

        headerButton.Accepting += (s, e) =>
        {
            e.Cancel = true; // prevent event bubbling (avoids multiple opens)
            if (prUrl != null)
            {
                try { Process.Start(new ProcessStartInfo { FileName = prUrl, UseShellExecute = true }); }
                catch { }
            }
        };

        // --- CI section ---
        var ciFrame = new FrameView
        {
            Title = "CI: waiting for data...",
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = 3,
            ColorScheme = dimScheme,
            CanFocus = false
        };
        ciFrame.Border.TextAlignment = Alignment.Center;
        var ciSummaryLabel = new Label
        {
            Text = "",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            ColorScheme = dimScheme
        };
        var ciFailuresContainer = new View
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = 0,
            ColorScheme = darkScheme
        };
        ciFrame.Add(ciSummaryLabel, ciFailuresContainer);
        var ciFailureButtons = new List<Button>();

        // --- Approvals section ---
        var approvalsFrame = new FrameView
        {
            Title = "Approvals: waiting for data...",
            X = 0,
            Y = Pos.Bottom(ciFrame),
            Width = Dim.Fill(),
            Height = 3,
            ColorScheme = dimScheme,
            CanFocus = false
        };
        approvalsFrame.Border.TextAlignment = Alignment.Center;
        var approvalsLabel = new Label
        {
            Text = "",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            ColorScheme = dimScheme
        };
        var staleApprovalsLabel = new Label
        {
            Text = "",
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            ColorScheme = dimScheme,
            Visible = false
        };
        approvalsFrame.Add(approvalsLabel);
        approvalsFrame.Add(staleApprovalsLabel);

        // --- Comments needing action section ---
        var commentsFrame = new FrameView
        {
            Title = "Comments: waiting for data...",
            X = 0,
            Y = Pos.Bottom(approvalsFrame),
            Width = Dim.Fill(),
            Height = 3,
            ColorScheme = dimScheme,
            CanFocus = false
        };
        commentsFrame.Border.TextAlignment = Alignment.Center;

        var commentsListView = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = darkScheme
        };
        commentsFrame.Add(commentsListView);
        var commentButtons = new List<Button>();

        // --- Waiting for reply section ---
        var waitingFrame = new FrameView
        {
            Title = "Waiting for reply: waiting for data...",
            X = 0,
            Y = Pos.Bottom(commentsFrame),
            Width = Dim.Fill(),
            Height = 3,
            ColorScheme = dimScheme,
            CanFocus = false
        };
        waitingFrame.Border.TextAlignment = Alignment.Center;

        var waitingListView = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = darkScheme
        };
        waitingFrame.Add(waitingListView);
        var waitingButtons = new List<Button>();

        var updatedLabel = new Label
        {
            Text = "",
            X = 0,
            Y = Pos.Bottom(waitingFrame),
            Width = Dim.Fill(),
            ColorScheme = dimScheme
        };

        // --- Separator ---
        var separator = new LineView(Orientation.Horizontal)
        {
            X = 0,
            Y = Pos.Bottom(updatedLabel),
            Width = Dim.Fill(),
            ColorScheme = new ColorScheme { Normal = new Attribute(Color.DarkGray, Color.Black) }
        };

        // --- Bottom: progress bar + status + button ---
        var progressBar = new ProgressBar
        {
            X = 0,
            Y = Pos.Bottom(separator),
            Width = Dim.Fill(),
            Height = 1,
            Fraction = 0f,
            ProgressBarStyle = ProgressBarStyle.Continuous,
            ColorScheme = cyanScheme
        };

        var statusLabel = new Label
        {
            Text = "Waiting...",
            X = 0,
            Y = Pos.Bottom(progressBar),
            Width = Dim.Fill(15)
        };

        var checkButton = new Button
        {
            Text = "Check Now",
            X = Pos.AnchorEnd(14),
            Y = Pos.Bottom(progressBar),
            Enabled = false,
            ColorScheme = darkScheme
        };

        var extendButton = new Button
        {
            Text = "Add 2h",
            X = Pos.AnchorEnd(24),
            Y = Pos.Bottom(progressBar),
            Visible = false, // only shown when after-hours paused
            ColorScheme = new ColorScheme
            {
                Normal = new Attribute(Color.Black, Color.BrightMagenta),
                Focus = new Attribute(Color.Black, Color.White),
                HotNormal = new Attribute(Color.Black, Color.BrightMagenta),
                HotFocus = new Attribute(Color.Black, Color.White),
            }
        };

        // --- Debug panel (collapsible, toggled by button) — bottommost ---
        var hasDebugFile = !string.IsNullOrEmpty(debugFile);
        var debugExpanded = false;
        var debugLines = new ObservableCollection<string>();
        var debugLastLineCount = 0;

        var debugToggleButton = new Button
        {
            Text = "See Debug Output",
            X = 0,
            Y = Pos.Bottom(statusLabel) + 1,
            Visible = hasDebugFile,
            CanFocus = false,
            ColorScheme = dimScheme
        };

        var debugFrame = new FrameView
        {
            Title = "Debug Log",
            X = 0,
            Y = Pos.Bottom(debugToggleButton),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = dimScheme,
            Visible = false
        };
        debugFrame.Border.TextAlignment = Alignment.Center;

        var debugListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = new ColorScheme { Normal = new Attribute(Color.DarkGray, Color.Black) }
        };
        debugListView.SetSource(debugLines);
        debugListView.VerticalScrollBar.AutoShow = true;
        debugFrame.Add(debugListView);

        // --- State ---
        var checkRequested = false;
        var countdownTotal = 0;
        var countdownStart = DateTime.Now;
        var isCountingDown = false;
        var isChecking = false;
        var isTerminal = false;
        var actionState = new bool[] { false }; // [0] = waitingOnAction, capturable in lambdas
        var isAfterHoursPaused = false; // true when sleeping until morning
        var terminalDescription = "";
        var terminalState = "";
        var spinnerIdx = 0;
        var lastLineCount = 0;

        checkButton.Accepting += (s, e) =>
        {
            e.Cancel = true;
            try
            {
                var dir = Path.GetDirectoryName(triggerFile);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(triggerFile, DateTime.Now.ToString("o"));
                checkRequested = true;
                isCountingDown = false;
                statusLabel.Text = "⚡ Check requested — waiting for worker...";
                statusLabel.ColorScheme = cyanScheme;
                checkButton.Enabled = false;
                progressBar.Fraction = 1f;
            }
            catch (Exception ex) { statusLabel.Text = $"Error: {ex.Message}"; }
        };

        extendButton.Accepting += (s, e) =>
        {
            e.Cancel = true;
            try
            {
                var dir = Path.GetDirectoryName(triggerFile);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(triggerFile, $"EXTEND|{DateTime.Now:o}");
                // Unpause — go back to normal polling mode
                isAfterHoursPaused = false;
                extendButton.Visible = false;
                statusLabel.Text = "⚡ Resuming monitoring for 2 hours...";
                statusLabel.ColorScheme = cyanScheme;
            }
            catch (Exception ex) { statusLabel.Text = $"Error: {ex.Message}"; }
        };

        window.Add(headerButton, ciFrame, approvalsFrame, commentsFrame, waitingFrame,
                    updatedLabel, separator, debugToggleButton, debugFrame, progressBar, statusLabel, extendButton, checkButton);

        window.Loaded += (s, e) => checkButton.SetFocus();

        // Debug toggle button handler
        debugToggleButton.Accepting += (s, e) =>
        {
            e.Cancel = true;
            debugExpanded = !debugExpanded;
            debugFrame.Visible = debugExpanded;
            debugToggleButton.Text = debugExpanded ? "Hide Debug Output" : "See Debug Output";
            window.SetNeedsLayout();
            if (debugExpanded)
            {
                LoadDebugLines(debugFile!, debugLines, ref debugLastLineCount, debugListView);
            }
        };

        // Initial load
        LoadAndUpdateStatus(logFile, triggerFile, ref lastLineCount, ref countdownTotal, ref countdownStart, ref isCountingDown,
                            ref isAfterHoursPaused, ref isTerminal, ref terminalState, ref terminalDescription,
                            ciFrame, ciSummaryLabel, ciFailuresContainer, ciFailureButtons,
                            approvalsFrame, approvalsLabel, staleApprovalsLabel,
                            commentsFrame, commentsListView, commentButtons,
                            waitingFrame, waitingListView, waitingButtons,
                            updatedLabel, statusLabel, progressBar, checkButton, extendButton, actionState, greenScheme, dimScheme, linkScheme);
        if (isCountingDown) checkButton.Enabled = true;

        // --- Timer: poll file + update progress ---
        Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            // Re-read header from log file if not yet loaded (log file may appear after viewer starts)
            if (!headerLoaded && File.Exists(logFile))
            {
                try
                {
                    using var hfs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var hsr = new StreamReader(hfs, Encoding.UTF8);
                    var firstLine = hsr.ReadLine() ?? "";
                    if (firstLine.Contains("http"))
                    {
                        var urlMatch = Regex.Match(firstLine, @"(https?://\S+)");
                        if (urlMatch.Success) prUrl = urlMatch.Groups[1].Value;
                        var pipeIdx = firstLine.IndexOf('|');
                        var titleText = pipeIdx > 0
                            ? firstLine[..pipeIdx].TrimStart('\uFEFF').Trim()
                            : firstLine.TrimStart('\uFEFF');
                        headerButton.Text = $"🔗 {titleText}";
                        headerLoaded = true;
                    }
                }
                catch { }
            }

            var wasCountingDown = isCountingDown;

            var hadUpdate = LoadAndUpdateStatus(logFile, triggerFile, ref lastLineCount, ref countdownTotal, ref countdownStart, ref isCountingDown,
                                                ref isAfterHoursPaused, ref isTerminal, ref terminalState, ref terminalDescription,
                                                ciFrame, ciSummaryLabel, ciFailuresContainer, ciFailureButtons,
                                                approvalsFrame, approvalsLabel, staleApprovalsLabel,
                                                commentsFrame, commentsListView, commentButtons,
                                                waitingFrame, waitingListView, waitingButtons,
                                                updatedLabel, statusLabel, progressBar, checkButton, extendButton, actionState, greenScheme, dimScheme, linkScheme);

            // New STATUS arrived — reset state
            if (hadUpdate && !isTerminal)
            {
                isChecking = false;
                checkRequested = false;
                actionState[0] = false;
                if (isCountingDown) checkButton.Enabled = true;
                // Restore from terminal state if resuming
                checkButton.Visible = true;
                statusLabel.Width = Dim.Percent(70);
                statusLabel.Height = 1;
            }

            // Terminal state reached — freeze the UI
            if (isTerminal)
            {
                var emoji = terminalState switch
                {
                    "approved" or "approved_and_ci_green" => "✅",
                    "ci_passed_comments_pending" => "✅",
                    "ci_failure" => "❌",
                    "ci_cancelled" => "🚫",
                    "unresolved_comments" or "new_comment" => "💬",
                    "merge_conflict" => "⚠️",
                    "stopped" => "⏹️",
                    _ => "⚡"
                };
                var stateColor = terminalState switch
                {
                    "approved" or "approved_and_ci_green" => new ColorScheme { Normal = new Attribute(Color.BrightGreen, Color.Black) },
                    "ci_passed_comments_pending" => new ColorScheme { Normal = new Attribute(Color.BrightGreen, Color.Black) },
                    "ci_failure" => new ColorScheme { Normal = new Attribute(Color.BrightRed, Color.Black) },
                    "merge_conflict" => new ColorScheme { Normal = new Attribute(Color.BrightRed, Color.Black) },
                    "stopped" => dimScheme,
                    _ => new ColorScheme { Normal = new Attribute(Color.BrightYellow, Color.Black) }
                };
                if (terminalState == "stopped")
                {
                    statusLabel.Text = $"{emoji} {terminalDescription} — closing in 5 seconds...";
                }
                else
                {
                    statusLabel.Text = $"{emoji} {terminalDescription} — check your Copilot CLI window";
                }
                statusLabel.ColorScheme = stateColor;
                statusLabel.Width = Dim.Fill();
                statusLabel.Height = 2;
                progressBar.Fraction = 0f;
                progressBar.ColorScheme = stateColor;
                checkButton.Visible = false;

                // Auto-close viewer when monitoring is stopped
                if (terminalState == "stopped")
                {
                    Application.AddTimeout(TimeSpan.FromSeconds(5), () =>
                    {
                        Application.RequestStop();
                        return false;
                    });
                }

                return true;
            }

            // Skip status bar updates while waiting on a viewer-initiated action
            if (actionState[0])
            {
                return true;
            }

            if (checkRequested && !hadUpdate)
            {
                var spinner = SpinnerFrames[spinnerIdx++ % SpinnerFrames.Length];
                statusLabel.Text = $"{spinner} Check requested — waiting for worker...";
                statusLabel.ColorScheme = cyanScheme;
                return true;
            }

            if (isChecking)
            {
                var spinner = SpinnerFrames[spinnerIdx++ % SpinnerFrames.Length];
                statusLabel.Text = $"{spinner} Checking status now...";
                statusLabel.ColorScheme = cyanScheme;
                progressBar.Fraction = 1f;
                checkButton.Enabled = false;
                return true;
            }

            if (isCountingDown)
            {
                var elapsed = (DateTime.Now - countdownStart).TotalSeconds;
                var remaining = Math.Max(0, countdownTotal - elapsed);
                if (remaining <= 0)
                {
                    // Viewer countdown hit zero — write trigger file to wake worker
                    isChecking = true;
                    isCountingDown = false;
                    checkButton.Enabled = false;
                    try
                    {
                        var dir = Path.GetDirectoryName(triggerFile);
                        if (dir != null && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(triggerFile, DateTime.Now.ToString("o"));
                    }
                    catch { }

                    var spinner = SpinnerFrames[spinnerIdx++ % SpinnerFrames.Length];
                    statusLabel.Text = $"{spinner} Checking status now...";
                    statusLabel.ColorScheme = cyanScheme;
                    progressBar.Fraction = 1f;
                }
                else
                {
                    progressBar.Fraction = Math.Min(1f, (float)(elapsed / Math.Max(1, countdownTotal)));
                    statusLabel.Text = $"💤 Next check in {(int)remaining}s";
                    statusLabel.ColorScheme = new ColorScheme { Normal = new Attribute(Color.White, Color.Black) };
                }
            }
            else if (!isChecking && !isAfterHoursPaused && !actionState[0])
            {
                var spinner = SpinnerFrames[spinnerIdx++ % SpinnerFrames.Length];
                statusLabel.Text = $"{spinner} Waiting for status...";
                statusLabel.ColorScheme = cyanScheme;
                progressBar.Fraction = 0f;
                checkButton.Enabled = false;
            }

            // Tail debug file if panel is expanded
            if (hasDebugFile && debugExpanded)
            {
                LoadDebugLines(debugFile!, debugLines, ref debugLastLineCount, debugListView);
            }

            return true;
        });

        Application.Run(window);
        Application.Shutdown();
    }

    /// <summary>
    /// Reads new lines from the log file, parses STATUS| JSON lines,
    /// updates the dashboard labels. Returns true if a STATUS line was found (new data).
    /// </summary>
    private static bool LoadAndUpdateStatus(
        string logFile,
        string triggerFile,
        ref int lastLineCount,
        ref int countdownTotal,
        ref DateTime countdownStart,
        ref bool isCountingDown,
        ref bool isAfterHoursPaused,
        ref bool isTerminal,
        ref string terminalState,
        ref string terminalDescription,
        FrameView ciFrame,
        Label ciSummaryLabel,
        View ciFailuresContainer,
        List<Button> ciFailureButtons,
        FrameView approvalsFrame,
        Label approvalsLabel,
        Label staleApprovalsLabel,
        FrameView commentsFrame,
        View commentsContainer,
        List<Button> commentButtons,
        FrameView waitingFrame,
        View waitingContainer,
        List<Button> waitingButtons,
        Label updatedLabel,
        Label statusLabel,
        ProgressBar progressBar,
        Button checkButton,
        Button extendButton,
        bool[] actionState,
        ColorScheme greenScheme,
        ColorScheme dimScheme,
        ColorScheme linkScheme)
    {
        var hadStatusUpdate = false;
        try
        {
            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var allLines = new List<string>();
            while (reader.ReadLine() is { } line)
                allLines.Add(line);

            // If the file was truncated (e.g., pr_monitor_start overwrote it), reset and re-read from the beginning
            // This clears terminal state from a previous monitoring run
            if (allLines.Count < lastLineCount)
            {
                lastLineCount = 0;
                isTerminal = false;
                terminalState = "";
                terminalDescription = "";
                isCountingDown = false;
            }

            if (allLines.Count <= lastLineCount) return false;

            for (var i = lastLineCount; i < allLines.Count; i++)
            {
                var line = allLines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("🔗") || line.StartsWith("PR #") || line.StartsWith("#") || line[0] == '\uFEFF') continue;

                // Parse STATUS| JSON lines
                if (line.StartsWith("STATUS|"))
                {
                    try
                    {
                        var json = line.Substring(7);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // CI
                        try
                        {
                            if (root.TryGetProperty("checks", out var checks))
                            {
                                var passed = checks.GetProperty("passed").GetInt32();
                                var failed = checks.GetProperty("failed").GetInt32();
                                var pending = checks.GetProperty("pending").GetInt32();
                                var total = checks.GetProperty("total").GetInt32();

                                var parts = new List<string>();
                                if (passed > 0) parts.Add($"✅ {passed} passed");
                                if (pending > 0) parts.Add($"⏳ {pending} in progress");
                                if (failed > 0) parts.Add($"❌ {failed} failed");
                                var queued = total - passed - failed - pending;
                                if (queued > 0) parts.Add($"⏸️ {queued} queued");

                                ciFrame.Title = $"CI ({passed}/{total})";
                                ciSummaryLabel.Text = string.Join("  ", parts);
                                var ciScheme = failed > 0
                                    ? new ColorScheme { Normal = new Attribute(Color.BrightRed, Color.Black) }
                                    : pending > 0
                                        ? new ColorScheme { Normal = new Attribute(Color.White, Color.Black) }
                                        : greenScheme;
                                ciFrame.ColorScheme = ciScheme;
                                ciSummaryLabel.ColorScheme = ciScheme;

                                // Clear old failure buttons
                                foreach (var btn in ciFailureButtons)
                                    ciFailuresContainer.Remove(btn);
                                ciFailureButtons.Clear();

                                // Parse failure details
                                if (failed > 0 && checks.TryGetProperty("failures", out var failures))
                                {
                                    var row = 0;
                                    foreach (var f in failures.EnumerateArray())
                                    {
                                        var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                        var reason = f.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                                        var failUrl = f.TryGetProperty("url", out var u) ? u.GetString() : null;

                                        if (name.Length > 40) name = name[..37] + "...";
                                        if (reason.Length > 40) reason = reason[..37] + "...";

                                        var btnText = $"  ❌ {name}: {reason}";
                                        var capturedUrl = failUrl;
                                        var btn = new Button
                                        {
                                            Text = btnText,
                                            X = 0,
                                            Y = row,
                                            Width = Dim.Fill(),
                                            NoPadding = true,
                                            NoDecorations = true,
                                            TextAlignment = Alignment.Start,
                                            ColorScheme = new ColorScheme { Normal = new Attribute(Color.BrightRed, Color.Black) }
                                        };
                                        btn.Accepting += (bs, be) =>
                                        {
                                            be.Cancel = true;
                                            if (capturedUrl != null)
                                            {
                                                try { Process.Start(new ProcessStartInfo { FileName = capturedUrl, UseShellExecute = true }); }
                                                catch { }
                                            }
                                        };
                                        ciFailureButtons.Add(btn);
                                        ciFailuresContainer.Add(btn);
                                        row++;
                                    }
                                    ciFailuresContainer.Height = row;
                                    // Resize frame: 1 summary + failures + 2 border
                                    ciFrame.Height = row + 3;
                                }
                                else
                                {
                                    ciFailuresContainer.Height = 0;
                                    ciFrame.Height = 3;
                                }
                            }
                        }
                        catch { /* CI section parse error — continue with other sections */ }

                        // Approvals
                        try
                        {
                            if (root.TryGetProperty("approvals", out var approvals))
                            {
                                var names = new List<string>();
                                foreach (var a in approvals.EnumerateArray())
                                {
                                    var name = (a.TryGetProperty("name", out var n) ? n.GetString() : null)
                                        ?? "unknown";
                                    names.Add($"{GetUserEmoji(name)} {name}");
                                }

                                // Stale approvals
                                var staleNames = new List<string>();
                                if (root.TryGetProperty("stale_approvals", out var staleApprovals))
                                {
                                    foreach (var a in staleApprovals.EnumerateArray())
                                    {
                                        var name = (a.TryGetProperty("name", out var n) ? n.GetString() : null)
                                            ?? "unknown";
                                        staleNames.Add(name);
                                    }
                                }

                                approvalsFrame.Title = names.Count > 0
                                    ? $"Approvals ({names.Count})"
                                    : "Approvals";
                                approvalsLabel.Text = names.Count > 0
                                    ? string.Join("  ", names)
                                    : "none";

                                if (staleNames.Count > 0)
                                {
                                    staleApprovalsLabel.Text = $"Stale ({staleNames.Count}): {string.Join(", ", staleNames)}";
                                    staleApprovalsLabel.Visible = true;
                                    approvalsFrame.Height = 4; // 2 rows + border
                                }
                                else
                                {
                                    staleApprovalsLabel.Visible = false;
                                    approvalsFrame.Height = 3;
                                }

                                var whiteScheme = new ColorScheme { Normal = new Attribute(Color.White, Color.Black) };
                                var appScheme = names.Count > 0 ? greenScheme
                                             : staleNames.Count > 0 ? whiteScheme
                                             : dimScheme;
                                approvalsFrame.ColorScheme = appScheme;
                                approvalsLabel.ColorScheme = names.Count > 0 ? greenScheme : dimScheme;
                                staleApprovalsLabel.ColorScheme = dimScheme;
                            }
                        }
                        catch { /* Approvals section parse error — continue */ }

                        // Unresolved comments
                        try
                        {
                            if (root.TryGetProperty("unresolved", out var unresolved))
                            {
                                // Clear old buttons
                                foreach (var btn in commentButtons)
                                    commentsContainer.Remove(btn);
                                commentButtons.Clear();

                                var count = 0;
                                var row = 0;
                                foreach (var c in unresolved.EnumerateArray())
                                {
                                    count++;
                                    var author = c.GetProperty("author").GetString() ?? "unknown";
                                    var summary = (c.TryGetProperty("summary", out var s) ? s.GetString() : null) ?? "";
                                    var commentUrl = c.TryGetProperty("url", out var u) ? u.GetString() : null;

                                    // Truncate summary to fit
                                    if (summary.Length > 60) summary = summary[..57] + "...";

                                    var emoji = GetUserEmoji(author);
                                    var btnText = $"{emoji} {author}: {summary}";
                                    var capturedUrl = commentUrl; // capture for closure
                                    var btn = new Button
                                    {
                                        Text = btnText,
                                        X = 0,
                                        Y = row,
                                        Width = Dim.Fill(),
                                        NoPadding = true,
                                        NoDecorations = true,
                                        TextAlignment = Alignment.Start,
                                        ColorScheme = linkScheme
                                    };
                                    btn.Accepting += (bs, be) =>
                                    {
                                        be.Cancel = true;
                                        if (capturedUrl != null)
                                        {
                                            try { Process.Start(new ProcessStartInfo { FileName = capturedUrl, UseShellExecute = true }); }
                                            catch { }
                                        }
                                    };
                                    commentButtons.Add(btn);
                                    commentsContainer.Add(btn);
                                    row++;
                                }

                                // Frame height: border(2) + content rows, min 3 so it's visible
                                var maxRows = 8; // cap visible rows, scroll for more
                                var visibleRows = Math.Min(count, maxRows);
                                commentsFrame.Height = count > 0 ? visibleRows + 2 : 3;

                                // Enable scrolling if more comments than visible area
                                commentsContainer.Height = count;

                                commentsFrame.Title = count > 0
                                    ? $"⚠️ {count} need action"
                                    : "✅ no comments need action";
                                commentsFrame.ColorScheme = count > 0
                                    ? new ColorScheme { Normal = new Attribute(Color.BrightYellow, Color.Black) }
                                    : greenScheme;
                            }
                        }
                        catch { /* Comments section parse error — continue */ }

                        // Waiting-for-reply comments
                        try
                        {
                            if (root.TryGetProperty("waiting_for_reply", out var waiting))
                            {
                                foreach (var btn in waitingButtons)
                                    waitingContainer.Remove(btn);
                                waitingButtons.Clear();

                                var wCount = 0;
                                var wRow = 0;
                                foreach (var c in waiting.EnumerateArray())
                                {
                                    wCount++;
                                    var author = c.GetProperty("author").GetString() ?? "unknown";
                                    var summary = (c.TryGetProperty("summary", out var ws) ? ws.GetString() : null) ?? "";
                                    var commentUrl = c.TryGetProperty("url", out var wu) ? wu.GetString() : null;
                                    var threadId = c.TryGetProperty("id", out var tid) ? tid.GetString() : null;

                                    if (summary.Length > 50) summary = summary[..47] + "...";

                                    // Action button (writes trigger file to wake the agent)
                                    var capturedThreadId = threadId;
                                    var actionBtn = new Button
                                    {
                                        Text = "Action",
                                        X = 0,
                                        Y = wRow,
                                        ColorScheme = new ColorScheme
                                        {
                                            Normal = new Attribute(Color.Black, Color.BrightYellow),
                                            Focus = new Attribute(Color.Black, Color.White),
                                            HotNormal = new Attribute(Color.Black, Color.BrightYellow),
                                            HotFocus = new Attribute(Color.Black, Color.White),
                                        }
                                    };
                                    var capturedAuthor = author;
                                    var capturedSummary = summary;
                                    actionBtn.Accepting += (bs, be) =>
                                    {
                                        be.Cancel = true;
                                        if (capturedThreadId != null)
                                        {
                                            try
                                            {
                                                var dir = Path.GetDirectoryName(triggerFile);
                                                if (dir != null && !Directory.Exists(dir))
                                                    Directory.CreateDirectory(dir);
                                                File.WriteAllText(triggerFile, $"ACTION|{capturedThreadId}");
                                                actionState[0] = true;
                                                statusLabel.Text = $"⏳ Waiting on user action for {capturedAuthor}'s comment...";
                                                statusLabel.ColorScheme = new ColorScheme { Normal = new Attribute(Color.BrightYellow, Color.Black) };
                                                progressBar.Fraction = 1f;
                                                checkButton.Enabled = false;
                                            }
                                            catch { }
                                        }
                                    };
                                    waitingButtons.Add(actionBtn);
                                    waitingContainer.Add(actionBtn);

                                    // Comment text button (opens in browser)
                                    var emoji = GetUserEmoji(author);
                                    var btnText = $" {emoji} {author}: {summary}";
                                    var capturedUrl = commentUrl;
                                    var btn = new Button
                                    {
                                        Text = btnText,
                                        X = 15,
                                        Y = wRow,
                                        Width = Dim.Fill(),
                                        NoPadding = true,
                                        NoDecorations = true,
                                        TextAlignment = Alignment.Start,
                                        ColorScheme = linkScheme
                                    };
                                    btn.Accepting += (bs, be) =>
                                    {
                                        be.Cancel = true;
                                        if (capturedUrl != null)
                                        {
                                            try { Process.Start(new ProcessStartInfo { FileName = capturedUrl, UseShellExecute = true }); }
                                            catch { }
                                        }
                                    };
                                    waitingButtons.Add(btn);
                                    waitingContainer.Add(btn);
                                    wRow++;
                                }

                                var wMaxRows = 6;
                                var wVisibleRows = Math.Min(wCount, wMaxRows);
                                waitingFrame.Height = wCount > 0 ? wVisibleRows + 2 : 3;
                                waitingContainer.Height = wCount;

                                waitingFrame.Title = wCount > 0
                                    ? $"⏳ {wCount} waiting for reply"
                                    : "⏳ no threads awaiting reply";
                                waitingFrame.ColorScheme = wCount > 0
                                    ? dimScheme
                                    : greenScheme;
                            }
                        }
                        catch { /* Waiting section parse error — continue */ }

                        // Timestamp
                        if (root.TryGetProperty("timestamp", out var ts))
                        {
                            updatedLabel.Text = $"Last updated: {ts.GetString()}";
                        }

                        // Viewer-owned timer: read next_check_seconds and start countdown
                        if (root.TryGetProperty("next_check_seconds", out var ncs))
                        {
                            countdownTotal = ncs.GetInt32();
                            countdownStart = DateTime.Now;
                            isCountingDown = true;
                        }

                        // After-hours: show pause status + extend button
                        var isAfterHours = root.TryGetProperty("after_hours", out var ah) && ah.GetBoolean();
                        if (isAfterHours && !actionState[0])
                        {
                            var wakeTime = DateTime.Now.AddSeconds(countdownTotal);
                            statusLabel.Text = $"🌙 Paused until {wakeTime:ddd h:mm tt}";
                            statusLabel.ColorScheme = new ColorScheme { Normal = new Attribute(Color.DarkGray, Color.Black) };
                            isCountingDown = false;
                            isAfterHoursPaused = true;
                            extendButton.Visible = true;
                        }
                        else
                        {
                            isAfterHoursPaused = false;
                            extendButton.Visible = false;
                        }

                        hadStatusUpdate = true;
                    }
                    catch { /* JSON parse error — skip entire line */ }
                    continue;
                }

                // Parse TERMINAL| JSON lines
                if (line.StartsWith("TERMINAL|"))
                {
                    try
                    {
                        var json = line.Substring(9);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        terminalState = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                        terminalDescription = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                        isTerminal = true;
                        isCountingDown = false;
                    }
                    catch { }
                    continue;
                }

                // Parse RESUMING| lines — clears terminal state, viewer goes back to polling mode
                if (line.StartsWith("RESUMING|"))
                {
                    isTerminal = false;
                    terminalState = "";
                    terminalDescription = "";
                    isCountingDown = false;
                    hadStatusUpdate = true;
                    continue;
                }

                // Parse STOPPED| lines — monitoring has ended, close the viewer
                if (line.StartsWith("STOPPED|"))
                {
                    isTerminal = true;
                    terminalState = "stopped";
                    terminalDescription = line.Contains('|') ? line[(line.LastIndexOf('|') + 1)..].Trim() : "Monitoring stopped.";
                    isCountingDown = false;
                    hadStatusUpdate = true;
                    continue;
                }
            }

            lastLineCount = allLines.Count;
        }
        catch { }

        return hadStatusUpdate;
    }

    /// <summary>
    /// Reads new lines from the debug log file and appends them to the ListView.
    /// </summary>
    private static void LoadDebugLines(string debugFile, ObservableCollection<string> debugLines, ref int lastLineCount, ListView listView)
    {
        try
        {
            if (!File.Exists(debugFile)) return;
            var allLines = File.ReadAllLines(debugFile, Encoding.UTF8);
            if (allLines.Length <= lastLineCount) return;

            for (var i = lastLineCount; i < allLines.Length; i++)
            {
                var line = allLines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                // Strip DEBUG| prefix if present
                if (line.StartsWith("DEBUG|", StringComparison.OrdinalIgnoreCase))
                    line = line[6..];
                debugLines.Add(line);
            }
            lastLineCount = allLines.Length;
            listView.SetSource(debugLines);
            // Auto-scroll to bottom
            if (debugLines.Count > 0)
                listView.SelectedItem = debugLines.Count - 1;
        }
        catch { }
    }
}
