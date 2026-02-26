// Licensed under the MIT License.

using PrCopilot.Services;

namespace PrCopilot.Tests;

public class VersionComparerTests
{
    [Theory]
    // Same version — not newer
    [InlineData("0.1.4", "0.1.4", false)]
    [InlineData("0.1.5-dev.20260226.36000", "0.1.5-dev.20260226.36000", false)]

    // Higher GA version wins
    [InlineData("0.1.4", "0.1.5", true)]
    [InlineData("0.1.4", "0.2.0", true)]
    [InlineData("0.1.4", "1.0.0", true)]

    // Lower GA version loses
    [InlineData("0.1.5", "0.1.4", false)]
    [InlineData("1.0.0", "0.9.9", false)]

    // Dev of next patch beats current GA: 0.1.5-dev > 0.1.4
    [InlineData("0.1.4", "0.1.5-dev.20260226.36000", true)]

    // GA beats dev of same prefix: 0.1.5 > 0.1.5-dev.xxx
    [InlineData("0.1.5-dev.20260226.36000", "0.1.5", true)]

    // Dev of same prefix — later timestamp wins
    [InlineData("0.1.5-dev.20260226.34000", "0.1.5-dev.20260226.36000", true)]
    [InlineData("0.1.5-dev.20260226.36000", "0.1.5-dev.20260226.34000", false)]

    // Dev of same prefix — later date wins
    [InlineData("0.1.5-dev.20260225.80000", "0.1.5-dev.20260226.1000", true)]

    // Higher GA beats any dev of lower prefix
    [InlineData("0.1.5-dev.20260226.36000", "0.1.6", true)]
    [InlineData("0.1.5-dev.20260226.36000", "0.2.0", true)]

    // Dev of lower prefix loses to current GA
    [InlineData("0.1.5", "0.1.4-dev.20260226.99999", false)]

    // Null/empty — not newer
    [InlineData("0.1.4", "", false)]
    [InlineData("", "0.1.5", false)]

    // Version with +metadata suffix (stripped)
    [InlineData("0.1.4+abc123", "0.1.5", true)]
    [InlineData("0.1.4", "0.1.5+def456", true)]
    public void IsNewer_ReturnsExpected(string current, string disk, bool expected)
    {
        Assert.Equal(expected, VersionComparer.IsNewer(current, disk));
    }
}
