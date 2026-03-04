// Licensed under the MIT License.

using System.Runtime.InteropServices;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

public class QuoteForShellTests
{
    [Fact]
    public void SimplePath_IsWrappedInQuotes()
    {
        var result = MonitorFlowTools.QuoteForShell("/usr/local/bin/PrCopilot");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal("\"/usr/local/bin/PrCopilot\"", result);
        else
            Assert.Equal("'/usr/local/bin/PrCopilot'", result);
    }

    [Fact]
    public void PathWithSpaces_IsWrappedInQuotes()
    {
        var result = MonitorFlowTools.QuoteForShell("/path/with spaces/PrCopilot");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal("\"/path/with spaces/PrCopilot\"", result);
        else
            Assert.Equal("'/path/with spaces/PrCopilot'", result);
    }

    [Fact]
    public void PathWithEmbeddedQuote_IsEscaped()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: double-quote wrapping, embedded double-quotes are backslash-escaped
            var result = MonitorFlowTools.QuoteForShell("C:\\path\\with\"quote");
            Assert.Equal("\"C:\\path\\with\\\"quote\"", result);
        }
        else
        {
            // Unix: single-quote wrapping, embedded single-quotes use '\'' escape
            var result = MonitorFlowTools.QuoteForShell("/path/it's/here");
            Assert.Equal("'/path/it'\\''s/here'", result);
        }
    }

    [Fact]
    public void TypicalInstallPath_QuotedCorrectly()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = MonitorFlowTools.QuoteForShell(@"C:\Users\user\.copilot\mcp-servers\pr-copilot\PrCopilot.exe");
            Assert.StartsWith("\"", result);
            Assert.EndsWith("\"", result);
            Assert.DoesNotContain("'", result);
        }
        else
        {
            var result = MonitorFlowTools.QuoteForShell("/home/user/.copilot/mcp-servers/pr-copilot/PrCopilot");
            Assert.StartsWith("'", result);
            Assert.EndsWith("'", result);
            Assert.DoesNotContain("\"", result);
        }
    }

    [Fact]
    public void EmptyString_ReturnsQuotedEmpty()
    {
        var result = MonitorFlowTools.QuoteForShell("");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal("\"\"", result);
        else
            Assert.Equal("''", result);
    }
}
