// Licensed under the MIT License.

namespace PrCopilot.Services;

/// <summary>
/// Compares version strings using SemVer-like rules.
/// Handles: "0.1.4", "0.1.5-dev.20260226.36000"
/// </summary>
public static class VersionComparer
{
    /// <summary>
    /// Returns true if <paramref name="diskVersion"/> is newer than <paramref name="currentVersion"/>.
    /// </summary>
    public static bool IsNewer(string currentVersion, string diskVersion)
    {
        if (string.IsNullOrEmpty(currentVersion) || string.IsNullOrEmpty(diskVersion))
            return false;

        if (currentVersion == diskVersion)
            return false;

        var (curPrefix, curPre) = Split(currentVersion);
        var (diskPrefix, diskPre) = Split(diskVersion);

        var prefixCmp = ComparePrefix(curPrefix, diskPrefix);
        if (prefixCmp != 0)
            return prefixCmp < 0; // disk has higher prefix

        // Same prefix — compare prerelease
        // No prerelease (GA) beats prerelease of same prefix: 0.1.5 > 0.1.5-dev.xxx
        if (curPre == null && diskPre == null)
            return false;
        if (curPre == null)
            return false; // current is GA, disk is prerelease of same version — current wins
        if (diskPre == null)
            return true; // disk is GA, current is prerelease — disk wins

        // Both prerelease — compare lexically by dot-separated segments
        return ComparePrerelease(curPre, diskPre) < 0;
    }

    private static (int[] Prefix, string? Prerelease) Split(string version)
    {
        // Strip +metadata (e.g., +abc123)
        var plusIdx = version.IndexOf('+');
        if (plusIdx >= 0)
            version = version[..plusIdx];

        var dashIdx = version.IndexOf('-');
        string prefixStr;
        string? pre;
        if (dashIdx >= 0)
        {
            prefixStr = version[..dashIdx];
            pre = version[(dashIdx + 1)..];
        }
        else
        {
            prefixStr = version;
            pre = null;
        }

        var parts = prefixStr.Split('.');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            nums[i] = int.TryParse(parts[i], out var n) ? n : 0;

        return (nums, pre);
    }

    private static int ComparePrefix(int[] a, int[] b)
    {
        var len = Math.Max(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }

        return 0;
    }

    private static int ComparePrerelease(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var len = Math.Max(aParts.Length, bParts.Length);

        for (var i = 0; i < len; i++)
        {
            if (i >= aParts.Length) return -1; // a has fewer parts, sorts earlier
            if (i >= bParts.Length) return 1;

            var aIsNum = int.TryParse(aParts[i], out var aNum);
            var bIsNum = int.TryParse(bParts[i], out var bNum);

            if (aIsNum && bIsNum)
            {
                if (aNum != bNum) return aNum.CompareTo(bNum);
            }
            else
            {
                var cmp = string.Compare(aParts[i], bParts[i], StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }
}
