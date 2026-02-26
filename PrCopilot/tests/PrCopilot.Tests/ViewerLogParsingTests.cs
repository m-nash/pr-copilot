// Licensed under the MIT License.

using System.Text.Json;
using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Tests the viewer's log line processing logic from MonitorViewer.LoadAndUpdateStatus.
/// Since the viewer is tightly coupled to Terminal.Gui, we reimplement the core parsing
/// logic inline and validate it produces the correct results.
/// </summary>
public class ViewerLogParsingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    #region Helpers — mirrors the viewer's parsing logic (MonitorViewer.cs ~line 611-998)

    /// <summary>
    /// Simulates the viewer's line processing loop. Returns the final state after processing all lines.
    /// </summary>
    private static ViewerParseResult ProcessLines(List<string> lines, int lastLineCount = 0)
    {
        var result = new ViewerParseResult();

        // Truncation detection: if line count dropped, reset (MonitorViewer.cs ~line 600)
        if (lines.Count < lastLineCount)
        {
            lastLineCount = 0;
            result.IsTerminal = false;
            result.TerminalState = "";
            result.TerminalDescription = "";
            result.IsCountingDown = false;
            result.WasTruncated = true;
        }

        if (lines.Count <= lastLineCount)
        {
            result.HadStatusUpdate = false;
            return result;
        }

        for (var i = lastLineCount; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Skip rules from MonitorViewer.cs line 615
            if (line.StartsWith("🔗") || line.StartsWith("PR #") || line.StartsWith("#") || line[0] == '\uFEFF') continue;

            // STATUS| parsing (MonitorViewer.cs ~line 618)
            if (line.StartsWith("STATUS|"))
            {
                try
                {
                    var json = line.Substring(7);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("checks", out var checks))
                    {
                        result.ChecksPassed = checks.GetProperty("passed").GetInt32();
                        result.ChecksFailed = checks.GetProperty("failed").GetInt32();
                        result.ChecksPending = checks.GetProperty("pending").GetInt32();
                        result.ChecksTotal = checks.GetProperty("total").GetInt32();
                    }

                    if (root.TryGetProperty("next_check_seconds", out var ncs))
                        result.NextCheckSeconds = ncs.GetInt32();

                    if (root.TryGetProperty("after_hours", out var ah))
                        result.AfterHours = ah.GetBoolean();

                    result.HadStatusUpdate = true;
                }
                catch { /* skip on parse error, matching viewer behavior */ }
                continue;
            }

            // TERMINAL| parsing (MonitorViewer.cs ~line 962)
            if (line.StartsWith("TERMINAL|"))
            {
                try
                {
                    var json = line.Substring(9);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    result.TerminalState = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                    result.TerminalDescription = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                    result.IsTerminal = true;
                    result.IsCountingDown = false;
                }
                catch { }
                continue;
            }

            // RESUMING| parsing (MonitorViewer.cs ~line 979)
            if (line.StartsWith("RESUMING|"))
            {
                result.IsTerminal = false;
                result.TerminalState = "";
                result.TerminalDescription = "";
                result.IsCountingDown = false;
                result.HadStatusUpdate = true;
                continue;
            }

            // STOPPED| parsing (MonitorViewer.cs ~line 990)
            if (line.StartsWith("STOPPED|"))
            {
                result.IsTerminal = true;
                result.TerminalState = "stopped";
                result.TerminalDescription = line.Contains('|') ? line[(line.LastIndexOf('|') + 1)..].Trim() : "Monitoring stopped.";
                result.IsCountingDown = false;
                result.HadStatusUpdate = true;
                continue;
            }
        }

        return result;
    }

    private class ViewerParseResult
    {
        public bool IsTerminal { get; set; }
        public string TerminalState { get; set; } = "";
        public string TerminalDescription { get; set; } = "";
        public bool IsCountingDown { get; set; }
        public bool HadStatusUpdate { get; set; }
        public bool WasTruncated { get; set; }
        public int ChecksPassed { get; set; }
        public int ChecksFailed { get; set; }
        public int ChecksPending { get; set; }
        public int ChecksTotal { get; set; }
        public int NextCheckSeconds { get; set; }
        public bool AfterHours { get; set; }
    }

    private static string MakeStatusLine(int passed = 0, int failed = 0, int pending = 0, int total = 0, bool afterHours = false)
    {
        var obj = new
        {
            checks = new { passed, failed, pending, queued = 0, cancelled = 0, total, failures = Array.Empty<object>() },
            approvals = Array.Empty<object>(),
            stale_approvals = Array.Empty<object>(),
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 60,
            after_hours = afterHours,
            timestamp = "12:00 PM"
        };
        return $"STATUS|{JsonSerializer.Serialize(obj, JsonOptions)}";
    }

    private static string MakeTerminalLine(string state, string description)
    {
        var obj = new { state, description };
        return $"TERMINAL|{JsonSerializer.Serialize(obj, JsonOptions)}";
    }

    #endregion

    #region TERMINAL line parsing

    [Fact]
    public void Terminal_CiFailure_SetsStateCorrectly()
    {
        var lines = new List<string> { MakeTerminalLine("ci_failure", "❌ CI has 2 failures") };
        var result = ProcessLines(lines);

        Assert.True(result.IsTerminal);
        Assert.Equal("ci_failure", result.TerminalState);
        Assert.Equal("❌ CI has 2 failures", result.TerminalDescription);
        Assert.False(result.IsCountingDown);
    }

    [Fact]
    public void Terminal_NewComment_SetsStateCorrectly()
    {
        var lines = new List<string> { MakeTerminalLine("new_comment", "New review comment from alice") };
        var result = ProcessLines(lines);

        Assert.True(result.IsTerminal);
        Assert.Equal("new_comment", result.TerminalState);
    }

    [Fact]
    public void Terminal_ApprovedAndCiGreen_SetsStateCorrectly()
    {
        var lines = new List<string> { MakeTerminalLine("approved_and_ci_green", "PR is ready to merge!") };
        var result = ProcessLines(lines);

        Assert.True(result.IsTerminal);
        Assert.Equal("approved_and_ci_green", result.TerminalState);
        Assert.Equal("PR is ready to merge!", result.TerminalDescription);
    }

    [Fact]
    public void Terminal_EmptyDescription_SetsEmptyString()
    {
        var lines = new List<string> { MakeTerminalLine("ci_failure", "") };
        var result = ProcessLines(lines);

        Assert.True(result.IsTerminal);
        Assert.Equal("", result.TerminalDescription);
    }

    [Fact]
    public void Terminal_InvalidJson_SkipsLine()
    {
        var lines = new List<string> { "TERMINAL|{not valid json" };
        var result = ProcessLines(lines);

        // Invalid JSON should be skipped — isTerminal stays false
        Assert.False(result.IsTerminal);
    }

    #endregion

    #region RESUMING line parsing

    [Fact]
    public void Resuming_ClearsTerminalState()
    {
        var lines = new List<string>
        {
            MakeTerminalLine("ci_failure", "CI failed"),
            "RESUMING|01:30 PM|Resuming monitoring..."
        };
        var result = ProcessLines(lines);

        Assert.False(result.IsTerminal);
        Assert.Equal("", result.TerminalState);
        Assert.Equal("", result.TerminalDescription);
        Assert.False(result.IsCountingDown);
        Assert.True(result.HadStatusUpdate);
    }

    [Fact]
    public void Resuming_AfterStopped_ClearsState()
    {
        var lines = new List<string>
        {
            "STOPPED|01:00 PM|Monitoring stopped.",
            "RESUMING|01:05 PM|Resuming..."
        };
        var result = ProcessLines(lines);

        Assert.False(result.IsTerminal);
        Assert.Equal("", result.TerminalState);
    }

    #endregion

    #region STOPPED line parsing

    [Fact]
    public void Stopped_SetsTerminalStopped()
    {
        var lines = new List<string> { "STOPPED|02:00 PM|PR merged successfully." };
        var result = ProcessLines(lines);

        Assert.True(result.IsTerminal);
        Assert.Equal("stopped", result.TerminalState);
        Assert.Equal("PR merged successfully.", result.TerminalDescription);
    }

    [Fact]
    public void Stopped_MessageExtraction_UsesLastPipe()
    {
        var lines = new List<string> { "STOPPED|02:00 PM|Done" };
        var result = ProcessLines(lines);

        Assert.Equal("Done", result.TerminalDescription);
    }

    [Fact]
    public void Stopped_EmptyMessage_ReturnsEmpty()
    {
        var lines = new List<string> { "STOPPED|02:00 PM|" };
        var result = ProcessLines(lines);

        Assert.Equal("", result.TerminalDescription);
    }

    #endregion

    #region File truncation detection

    [Fact]
    public void Truncation_WhenLineCountDrops_ResetsState()
    {
        // Simulate: previously read 10 lines, now file has only 3
        var lines = new List<string>
        {
            MakeStatusLine(passed: 1, total: 1)
        };
        var result = ProcessLines(lines, lastLineCount: 10);

        Assert.True(result.WasTruncated);
        Assert.True(result.HadStatusUpdate);
        Assert.Equal(1, result.ChecksPassed);
    }

    [Fact]
    public void Truncation_ResetsTerminalFlags()
    {
        var lines = new List<string> { MakeStatusLine(passed: 2, total: 2) };
        var result = ProcessLines(lines, lastLineCount: 50);

        Assert.True(result.WasTruncated);
        Assert.False(result.IsTerminal);
        Assert.Equal("", result.TerminalState);
    }

    [Fact]
    public void NoNewLines_ReturnsFalse()
    {
        var lines = new List<string> { "STATUS|{}" };
        // lastLineCount = 1, same as lines.Count => no new data
        var result = ProcessLines(lines, lastLineCount: 1);

        Assert.False(result.HadStatusUpdate);
    }

    #endregion

    #region STATUS JSON parsing

    [Fact]
    public void Status_AllChecksPassed_ParsesCorrectly()
    {
        var lines = new List<string> { MakeStatusLine(passed: 10, total: 10) };
        var result = ProcessLines(lines);

        Assert.True(result.HadStatusUpdate);
        Assert.Equal(10, result.ChecksPassed);
        Assert.Equal(0, result.ChecksFailed);
        Assert.Equal(10, result.ChecksTotal);
    }

    [Fact]
    public void Status_MixedCheckStates_ParsesCorrectly()
    {
        var lines = new List<string> { MakeStatusLine(passed: 3, failed: 1, pending: 2, total: 6) };
        var result = ProcessLines(lines);

        Assert.Equal(3, result.ChecksPassed);
        Assert.Equal(1, result.ChecksFailed);
        Assert.Equal(2, result.ChecksPending);
        Assert.Equal(6, result.ChecksTotal);
    }

    [Fact]
    public void Status_AfterHoursTrue_ParsesCorrectly()
    {
        var lines = new List<string> { MakeStatusLine(passed: 1, total: 1, afterHours: true) };
        var result = ProcessLines(lines);

        Assert.True(result.AfterHours);
    }

    [Fact]
    public void Status_InvalidJson_SkipsLine()
    {
        var lines = new List<string> { "STATUS|not-json-at-all" };
        var result = ProcessLines(lines);

        Assert.False(result.HadStatusUpdate);
    }

    #endregion

    #region Line skip rules

    [Theory]
    [InlineData("🔗 https://github.com/owner/repo/pull/123")]
    [InlineData("PR #123 — My pull request title")]
    [InlineData("# Header line")]
    [InlineData("\uFEFF")]
    [InlineData("\uFEFF" + "STATUS|{}")]
    public void SkippedLines_AreIgnored(string line)
    {
        var lines = new List<string> { line };
        var result = ProcessLines(lines);

        Assert.False(result.HadStatusUpdate);
        Assert.False(result.IsTerminal);
    }

    [Fact]
    public void EmptyAndWhitespaceLines_AreIgnored()
    {
        var lines = new List<string> { "", "   ", "\t" };
        var result = ProcessLines(lines);

        Assert.False(result.HadStatusUpdate);
    }

    [Fact]
    public void MixedLines_OnlyProcessesValidPrefixes()
    {
        var lines = new List<string>
        {
            "🔗 https://github.com/owner/repo/pull/123",
            "PR #123 — Title",
            MakeStatusLine(passed: 5, total: 5),
            "Some unknown line that is not a prefix",
            MakeTerminalLine("approved_and_ci_green", "Ready to merge!")
        };
        var result = ProcessLines(lines);

        // The TERMINAL line is last, so it should be the final state
        Assert.True(result.IsTerminal);
        Assert.Equal("approved_and_ci_green", result.TerminalState);
        Assert.True(result.HadStatusUpdate);
    }

    [Fact]
    public void MultipleStatusLines_LastOneWins()
    {
        var lines = new List<string>
        {
            MakeStatusLine(passed: 2, failed: 1, total: 3),
            MakeStatusLine(passed: 3, failed: 0, total: 3)
        };
        var result = ProcessLines(lines);

        Assert.Equal(3, result.ChecksPassed);
        Assert.Equal(0, result.ChecksFailed);
    }

    #endregion
}
