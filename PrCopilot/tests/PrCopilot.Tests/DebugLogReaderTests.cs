// Licensed under the MIT License.

using System.Text;
using PrCopilot.Viewer;

namespace PrCopilot.Tests;

/// <summary>
/// Tests for MonitorViewer.ReadDebugLogIncremental — the byte-offset incremental debug log reader
/// that filters heartbeat noise and only processes complete lines.
/// </summary>
public class DebugLogReaderTests : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _tempFile;

    public DebugLogReaderTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    #region Incremental reads

    [Fact]
    public void IncrementalRead_OnlyReturnsNewLines()
    {
        File.WriteAllText(_tempFile, "DEBUG|12:00:00 PM|[GhCli] First line\n", Utf8NoBom);

        var (lines1, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Single(lines1);
        Assert.Contains("First line", lines1[0]);

        File.AppendAllText(_tempFile, "DEBUG|12:00:01 PM|[GhCli] Second line\n", Utf8NoBom);

        var (lines2, offset2, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);
        Assert.Single(lines2);
        Assert.Contains("Second line", lines2[0]);
        Assert.True(offset2 > offset1);
    }

    [Fact]
    public void IncrementalRead_NoNewContent_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "DEBUG|12:00:00 PM|[GhCli] Only line\n", Utf8NoBom);

        var (_, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        var (lines2, offset2, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);

        Assert.Empty(lines2);
        Assert.Equal(offset1, offset2);
    }

    [Fact]
    public void IncrementalRead_MultipleNewLines_ReturnsAll()
    {
        File.WriteAllText(_tempFile, "DEBUG|12:00:00 PM|[GhCli] Line 1\n", Utf8NoBom);
        var (_, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);

        File.AppendAllText(_tempFile,
            "DEBUG|12:00:01 PM|[GhCli] Line 2\n" +
            "DEBUG|12:00:02 PM|[GhCli] Line 3\n" +
            "DEBUG|12:00:03 PM|[GhCli] Line 4\n", Utf8NoBom);

        var (lines2, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);
        Assert.Equal(3, lines2.Length);
    }

    #endregion

    #region Complete lines only

    [Fact]
    public void PartialLine_NotProcessedUntilNewline()
    {
        // Write a line without trailing newline
        File.WriteAllBytes(_tempFile, Utf8NoBom.GetBytes("DEBUG|12:00:00 PM|[GhCli] Partial"));

        var (lines1, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Empty(lines1);
        Assert.Equal(0, offset1); // Offset stays at 0 — nothing was consumed

        // Now complete the line
        File.WriteAllBytes(_tempFile, Utf8NoBom.GetBytes("DEBUG|12:00:00 PM|[GhCli] Partial line completed\n"));

        var (lines2, offset2, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);
        Assert.Single(lines2);
        Assert.Contains("Partial line completed", lines2[0]);
        Assert.True(offset2 > 0);
    }

    [Fact]
    public void MixedCompleteAndPartial_OnlyCompleteProcessed()
    {
        File.WriteAllBytes(_tempFile, Utf8NoBom.GetBytes(
            "DEBUG|12:00:00 PM|[GhCli] Complete line\nDEBUG|12:00:01 PM|[GhCli] Partial"));

        var (lines, offset, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Single(lines);
        Assert.Contains("Complete line", lines[0]);

        // The offset should be after the first complete line, not the whole file
        var expectedOffset = Utf8NoBom.GetByteCount("DEBUG|12:00:00 PM|[GhCli] Complete line\n");
        Assert.Equal(expectedOffset, offset);
    }

    #endregion

    #region Heartbeat filtering

    [Fact]
    public void HeartbeatLines_AreFiltered()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[GhCli] Normal line\n" +
            "DEBUG|12:00:05 PM|[Heartbeat] Sent notifications/progress (token=123): ⠋ 5m · Monitoring PR #1\n" +
            "DEBUG|12:00:10 PM|[Heartbeat] Sent notifications/message: ⠙ 5m · Monitoring PR #1\n" +
            "DEBUG|12:00:15 PM|[GhCli] Another normal line\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Normal line", lines[0]);
        Assert.Contains("Another normal line", lines[1]);
    }

    [Fact]
    public void HeartbeatFiltering_IsCaseInsensitive()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[heartbeat] lowercase\n" +
            "DEBUG|12:00:00 PM|[HEARTBEAT] UPPERCASE\n" +
            "DEBUG|12:00:00 PM|[GhCli] Keep me\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Single(lines);
        Assert.Contains("Keep me", lines[0]);
    }

    [Fact]
    public void AllHeartbeatFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[Heartbeat] Beat 1\n" +
            "DEBUG|12:00:05 PM|[Heartbeat] Beat 2\n" +
            "DEBUG|12:00:10 PM|[Heartbeat] Beat 3\n", Utf8NoBom);

        var (lines, offset, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Empty(lines);
        // Offset should still advance past the heartbeat lines
        Assert.True(offset > 0);
    }

    #endregion

    #region Version check filtering

    [Fact]
    public void VersionCheckLines_AreFiltered()
    {
        File.WriteAllText(_tempFile,
            "[VIEWER 12:00:00] Version check: running=0.1.29 disk=0.1.29\n" +
            "DEBUG|12:00:01 PM|[GhCli] Normal line\n" +
            "[VIEWER 12:00:30] Version check: running=0.1.29 disk=0.1.30\n" +
            "[VIEWER 12:00:30] Version upgrade available: 0.1.29 → 0.1.30\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Normal line", lines[0]);
        Assert.Contains("Version upgrade", lines[1]);
    }

    #endregion

    #region DEBUG| prefix stripping

    [Fact]
    public void DebugPrefix_IsStripped()
    {
        File.WriteAllText(_tempFile, "DEBUG|12:00:00 PM|[GhCli] Running: gh pr view\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Single(lines);
        Assert.Equal("12:00:00 PM|[GhCli] Running: gh pr view", lines[0]);
        Assert.DoesNotContain("DEBUG|", lines[0]);
    }

    [Fact]
    public void NonDebugPrefixed_LinesPassedThrough()
    {
        File.WriteAllText(_tempFile,
            "ERROR|12:00:00 PM|[GhCli] Something failed\n" +
            "[VIEWER 12:00:01] Viewer started\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("ERROR|", lines[0]);
        Assert.StartsWith("[VIEWER", lines[1]);
    }

    #endregion

    #region Truncation detection

    [Fact]
    public void Truncation_Detected_WhenFileShrinks()
    {
        // Write a large file
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
            sb.Append($"DEBUG|12:00:00 PM|[GhCli] Line {i}\n");
        File.WriteAllText(_tempFile, sb.ToString(), Utf8NoBom);

        var (_, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.True(offset1 > 0);

        // Truncate: replace with a much smaller file
        File.WriteAllText(_tempFile, "DEBUG|12:00:00 PM|[GhCli] After truncation\n", Utf8NoBom);

        var (lines2, offset2, truncated) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);
        Assert.True(truncated);
        Assert.Single(lines2);
        Assert.Contains("After truncation", lines2[0]);
        Assert.True(offset2 > 0);
    }

    [Fact]
    public void Truncation_ReloadsFromBeginning()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[GhCli] Original line 1\n" +
            "DEBUG|12:00:01 PM|[GhCli] Original line 2\n", Utf8NoBom);

        var (_, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);

        // Truncate and write new content shorter than original
        File.WriteAllText(_tempFile, "DEBUG|12:00:02 PM|[GhCli] New\n", Utf8NoBom);

        var (lines, _, truncated) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);
        Assert.True(truncated);
        Assert.Single(lines);
        Assert.Contains("New", lines[0]);
    }

    [Fact]
    public void Truncation_ToEmptyFile_StillSignalsTruncated()
    {
        File.WriteAllText(_tempFile, "DEBUG|12:00:00 PM|[GhCli] Some content\n", Utf8NoBom);
        var (_, offset1, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.True(offset1 > 0);

        // Truncate to 0 bytes
        File.WriteAllText(_tempFile, "", Utf8NoBom);

        var (lines, offset2, truncated) = MonitorViewer.ReadDebugLogIncremental(_tempFile, offset1);
        Assert.True(truncated, "Should signal truncation when file shrinks to 0");
        Assert.Empty(lines);
        Assert.Equal(0, offset2);
    }

    #endregion

    #region Edge cases

    [Fact]
    public void FileDoesNotExist_ReturnsEmpty()
    {
        var (lines, offset, truncated) = MonitorViewer.ReadDebugLogIncremental(@"C:\nonexistent\file.log", 0);
        Assert.Empty(lines);
        Assert.Equal(0, offset);
        Assert.False(truncated);
    }

    [Fact]
    public void EmptyFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "", Utf8NoBom);

        var (lines, offset, truncated) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Empty(lines);
        Assert.Equal(0, offset);
        Assert.False(truncated);
    }

    [Fact]
    public void BlankLines_AreSkipped()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[GhCli] Line 1\n" +
            "\n" +
            "   \n" +
            "DEBUG|12:00:01 PM|[GhCli] Line 2\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void WindowsLineEndings_HandledCorrectly()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[GhCli] Line 1\r\n" +
            "DEBUG|12:00:01 PM|[GhCli] Line 2\r\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("\r", lines[0]);
        Assert.DoesNotContain("\r", lines[1]);
    }

    [Fact]
    public void MixedContent_HeartbeatsFilteredOthersKept()
    {
        File.WriteAllText(_tempFile,
            "DEBUG|12:00:00 PM|[GhCli] Running: gh pr view\n" +
            "DEBUG|12:00:05 PM|[Heartbeat] Sent notifications/progress: ⠋ 5m\n" +
            "ERROR|12:00:06 PM|[GhCli] TIMEOUT after 30s\n" +
            "[VIEWER 12:00:07] Status updated\n" +
            "DEBUG|12:00:10 PM|[Heartbeat] Sent notifications/message: ⠙ 10m\n" +
            "DEBUG|12:00:15 PM|[StateMachine] Transition: polling → terminal\n", Utf8NoBom);

        var (lines, _, _) = MonitorViewer.ReadDebugLogIncremental(_tempFile, 0);
        Assert.Equal(4, lines.Length);
        Assert.Contains("Running: gh pr view", lines[0]);
        Assert.StartsWith("ERROR|", lines[1]);
        Assert.StartsWith("[VIEWER", lines[2]);
        Assert.Contains("Transition:", lines[3]);
    }

    #endregion
}
