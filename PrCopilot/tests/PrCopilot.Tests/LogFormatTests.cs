// Licensed under the MIT License.

using System.Text.Json;
using PrCopilot.StateMachine;

namespace PrCopilot.Tests;

/// <summary>
/// Tests the log line format produced by MonitorFlowTools.WriteLogEntryAsync / BuildStatusLine / BuildTerminalLogLine.
/// Since those are private methods, we test the FORMAT contract by constructing log lines manually
/// and verifying they parse correctly — the same way the viewer will consume them.
/// </summary>
public class LogFormatTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    #region STATUS line parsing

    [Fact]
    public void StatusLine_ValidJson_ParsesCorrectly()
    {
        var statusObj = new
        {
            checks = new { passed = 5, failed = 0, pending = 2, queued = 1, cancelled = 0, total = 8, failures = Array.Empty<object>() },
            approvals = new[] { new { name = "alice" } },
            stale_approvals = Array.Empty<object>(),
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 60,
            after_hours = false,
            timestamp = "01:22 PM"
        };
        var line = $"STATUS|{JsonSerializer.Serialize(statusObj, JsonOptions)}";

        Assert.StartsWith("STATUS|", line);
        var json = line.Substring(7);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("checks", out var checks));
        Assert.Equal(5, checks.GetProperty("passed").GetInt32());
        Assert.Equal(0, checks.GetProperty("failed").GetInt32());
        Assert.Equal(2, checks.GetProperty("pending").GetInt32());
        Assert.Equal(8, checks.GetProperty("total").GetInt32());
    }

    [Fact]
    public void StatusLine_HasAllExpectedFields()
    {
        var statusObj = new
        {
            checks = new { passed = 0, failed = 0, pending = 0, queued = 0, cancelled = 0, total = 0, failures = Array.Empty<object>() },
            approvals = Array.Empty<object>(),
            stale_approvals = Array.Empty<object>(),
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 30,
            after_hours = false,
            timestamp = "09:00 AM"
        };
        var line = $"STATUS|{JsonSerializer.Serialize(statusObj, JsonOptions)}";
        var json = line.Substring(7);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("checks", out _));
        Assert.True(root.TryGetProperty("approvals", out _));
        Assert.True(root.TryGetProperty("stale_approvals", out _));
        Assert.True(root.TryGetProperty("next_check_seconds", out _));
        Assert.True(root.TryGetProperty("after_hours", out _));
        Assert.True(root.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public void StatusLine_WithAfterHoursTrue_ParsesCorrectly()
    {
        var statusObj = new
        {
            checks = new { passed = 3, failed = 0, pending = 0, queued = 0, cancelled = 0, total = 3, failures = Array.Empty<object>() },
            approvals = Array.Empty<object>(),
            stale_approvals = Array.Empty<object>(),
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 300,
            after_hours = true,
            timestamp = "11:30 PM"
        };
        var line = $"STATUS|{JsonSerializer.Serialize(statusObj, JsonOptions)}";
        var json = line.Substring(7);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("after_hours").GetBoolean());
    }

    [Fact]
    public void StatusLine_WithMultipleApprovals_ParsesArray()
    {
        var statusObj = new
        {
            checks = new { passed = 1, failed = 0, pending = 0, queued = 0, cancelled = 0, total = 1, failures = Array.Empty<object>() },
            approvals = new[] { new { name = "alice" }, new { name = "bob" } },
            stale_approvals = new[] { new { name = "charlie" } },
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 60,
            after_hours = false,
            timestamp = "02:00 PM"
        };
        var line = $"STATUS|{JsonSerializer.Serialize(statusObj, JsonOptions)}";
        var json = line.Substring(7);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("approvals").GetArrayLength());
        Assert.Equal("alice", root.GetProperty("approvals")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("stale_approvals").GetArrayLength());
    }

    [Fact]
    public void StatusLine_ChecksWithFailures_IncludesFailureDetails()
    {
        var statusObj = new
        {
            checks = new
            {
                passed = 2,
                failed = 1,
                pending = 0,
                queued = 0,
                cancelled = 0,
                total = 3,
                failures = new[] { new { name = "build", reason = "Compile error", url = "https://github.com/run/1" } }
            },
            approvals = Array.Empty<object>(),
            stale_approvals = Array.Empty<object>(),
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 60,
            after_hours = false,
            timestamp = "03:00 PM"
        };
        var line = $"STATUS|{JsonSerializer.Serialize(statusObj, JsonOptions)}";
        var json = line.Substring(7);
        using var doc = JsonDocument.Parse(json);
        var checks = doc.RootElement.GetProperty("checks");

        Assert.Equal(1, checks.GetProperty("failed").GetInt32());
        var failures = checks.GetProperty("failures");
        Assert.Equal(1, failures.GetArrayLength());
        Assert.Equal("build", failures[0].GetProperty("name").GetString());
    }

    [Fact]
    public void StatusLine_WithCancelledChecks_ParsesCorrectly()
    {
        var statusObj = new
        {
            checks = new { passed = 1, failed = 0, pending = 0, queued = 0, cancelled = 2, total = 3, failures = Array.Empty<object>() },
            approvals = Array.Empty<object>(),
            stale_approvals = Array.Empty<object>(),
            unresolved = Array.Empty<object>(),
            waiting_for_reply = Array.Empty<object>(),
            next_check_seconds = 60,
            after_hours = false,
            timestamp = "04:00 PM"
        };
        var line = $"STATUS|{JsonSerializer.Serialize(statusObj, JsonOptions)}";
        var json = line.Substring(7);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("checks").GetProperty("cancelled").GetInt32());
    }

    #endregion

    #region TERMINAL line parsing

    [Theory]
    [InlineData("new_comment")]
    [InlineData("merge_conflict")]
    [InlineData("ci_failure")]
    [InlineData("ci_cancelled")]
    [InlineData("approved_and_ci_green")]
    [InlineData("ci_passed_comments_pending")]
    public void TerminalLine_AllStates_ParseCorrectly(string state)
    {
        var terminalObj = new { state, description = $"Test description for {state}" };
        var line = $"TERMINAL|{JsonSerializer.Serialize(terminalObj, JsonOptions)}";

        Assert.StartsWith("TERMINAL|", line);
        var json = line.Substring(9);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(state, root.GetProperty("state").GetString());
        Assert.Contains(state, root.GetProperty("description").GetString());
    }

    [Fact]
    public void TerminalLine_EmptyDescription_ParsesCorrectly()
    {
        var terminalObj = new { state = "ci_failure", description = "" };
        var line = $"TERMINAL|{JsonSerializer.Serialize(terminalObj, JsonOptions)}";
        var json = line.Substring(9);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("", doc.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public void TerminalLine_DescriptionWithSpecialCharacters_ParsesCorrectly()
    {
        var terminalObj = new { state = "new_comment", description = "Comment says: \"fix the bug\" & resubmit <PR #123>" };
        var line = $"TERMINAL|{JsonSerializer.Serialize(terminalObj, JsonOptions)}";
        var json = line.Substring(9);
        using var doc = JsonDocument.Parse(json);

        Assert.Contains("\"fix the bug\"", doc.RootElement.GetProperty("description").GetString());
        Assert.Contains("<PR #123>", doc.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public void TerminalLine_DescriptionWithNewlines_ParsesCorrectly()
    {
        var terminalObj = new { state = "ci_failure", description = "Line 1\nLine 2\nLine 3" };
        var line = $"TERMINAL|{JsonSerializer.Serialize(terminalObj, JsonOptions)}";
        var json = line.Substring(9);
        using var doc = JsonDocument.Parse(json);

        Assert.Contains("\n", doc.RootElement.GetProperty("description").GetString());
    }

    #endregion

    #region RESUMING line parsing

    [Fact]
    public void ResumingLine_HasCorrectFormat()
    {
        var timestamp = "01:30 PM";
        var line = $"RESUMING|{timestamp}|Resuming monitoring...";

        Assert.StartsWith("RESUMING|", line);
        var parts = line.Split('|', 3);
        Assert.Equal(3, parts.Length);
        Assert.Equal("RESUMING", parts[0]);
        Assert.Equal(timestamp, parts[1]);
        Assert.Equal("Resuming monitoring...", parts[2]);
    }

    [Fact]
    public void ResumingLine_TimestampExtraction()
    {
        var line = "RESUMING|02:45 PM|Resuming after user action";
        var parts = line.Split('|', 3);

        Assert.Matches(@"\d{2}:\d{2} [AP]M", parts[1]);
    }

    #endregion

    #region STOPPED line parsing

    [Fact]
    public void StoppedLine_HasCorrectFormat()
    {
        var timestamp = "03:00 PM";
        var line = $"STOPPED|{timestamp}|Monitoring stopped for monitor-123.";

        Assert.StartsWith("STOPPED|", line);
        var parts = line.Split('|', 3);
        Assert.Equal(3, parts.Length);
        Assert.Equal("STOPPED", parts[0]);
        Assert.Equal(timestamp, parts[1]);
        Assert.Equal("Monitoring stopped for monitor-123.", parts[2]);
    }

    [Fact]
    public void StoppedLine_MessageExtraction_UsesLastPipe()
    {
        // The viewer uses line[(line.LastIndexOf('|') + 1)..] to extract the message
        var line = "STOPPED|04:00 PM|PR merged successfully.";
        var message = line[(line.LastIndexOf('|') + 1)..].Trim();

        Assert.Equal("PR merged successfully.", message);
    }

    [Fact]
    public void StoppedLine_MessageWithPipeCharacter_ExtractsLastSegment()
    {
        // Edge case: if the message itself contained a pipe, LastIndexOf would take the last segment
        var line = "STOPPED|04:00 PM|Status: done | all checks passed";
        var message = line[(line.LastIndexOf('|') + 1)..].Trim();

        // The viewer takes text after the LAST pipe
        Assert.Equal("all checks passed", message);
    }

    [Fact]
    public void StoppedLine_EmptyMessage_ReturnsEmpty()
    {
        var line = "STOPPED|05:00 PM|";
        var message = line[(line.LastIndexOf('|') + 1)..].Trim();

        Assert.Equal("", message);
    }

    #endregion
}
