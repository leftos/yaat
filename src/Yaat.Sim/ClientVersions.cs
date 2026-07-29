using System.Globalization;

namespace Yaat.Sim;

/// <summary>
/// Compares YAAT client version strings for the server's client-version gate.
/// </summary>
/// <remarks>
/// Only the numeric <c>major.minor.patch</c> core is compared; any prerelease or build-metadata
/// suffix is ignored. Every YAAT release carries the same <c>-beta</c> marker, so the suffix
/// conveys no ordering, and SemVer's own rule — that <c>0.9.18-beta</c> precedes <c>0.9.18</c> —
/// would make a release look older than a version that does not exist.
/// <para>
/// Comparison <b>fails open</b>: a version string that cannot be parsed on either side is never
/// treated as too old. A gate exists to stop a client the server knows is incompatible, not to
/// lock out a developer whose build lacks version metadata.
/// </para>
/// </remarks>
public static class ClientVersions
{
    /// <summary>
    /// Parses the numeric core of a version string such as <c>0.9.18-beta</c> or
    /// <c>0.9.18-beta+1a2b3c4</c>. Missing components default to zero, so <c>1</c> and
    /// <c>1.0.0</c> compare equal.
    /// </summary>
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            core = core[..cut];
        }

        var parts = core.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate" /> is strictly older than <paramref name="required" />.
    /// Returns false when either string is absent or unparseable, so an unreadable version is
    /// never gated out.
    /// </summary>
    public static bool IsOlderThan(string? candidate, string? required)
    {
        if (!TryParse(candidate, out var have) || !TryParse(required, out var need))
        {
            return false;
        }

        return have < need;
    }
}
