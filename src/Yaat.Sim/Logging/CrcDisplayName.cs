using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Yaat.Sim.Logging;

/// <summary>
/// A CRC client's display name, as the server shows it in the CRC lobby and room lists and in terminal lines:
/// <c>"{callsign} ({realName})"</c>, else whichever of the two it has, else <c>"unknown"</c>.
/// </summary>
public static partial class CrcDisplayName
{
    public const string Unknown = "unknown";

    public static string Build(string? callsign, string? realName)
    {
        bool hasCallsign = !string.IsNullOrEmpty(callsign);
        bool hasName = !string.IsNullOrEmpty(realName);

        if (hasCallsign && hasName)
        {
            return $"{callsign} ({realName})";
        }

        if (hasCallsign)
        {
            return callsign!;
        }

        return hasName ? realName! : Unknown;
    }

    /// <summary>
    /// Reads the real name out of a <c>"{callsign} ({realName})"</c> display name. False for a display name with no
    /// callsign-and-name pair (a bare callsign, a bare name, or <c>"unknown"</c>), which cannot be told apart.
    /// </summary>
    public static bool TryGetRealName(string? displayName, [NotNullWhen(true)] out string? realName)
    {
        Match match = CallsignAndNameRegex().Match(displayName ?? string.Empty);
        realName = match.Success ? match.Groups["name"].Value : null;
        return match.Success;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is shaped as a CRC position callsign (<c>OAK_TWR</c>, <c>NCT_A_APP</c>): upper-case
    /// letters and digits around at least one underscore, which a person's name or initials never hold.
    /// </summary>
    public static bool IsPositionCallsign(string? name) => (name is not null) && PositionCallsignRegex().IsMatch(name);

    [GeneratedRegex(@"^\S+ \((?<name>.+)\)$")]
    private static partial Regex CallsignAndNameRegex();

    [GeneratedRegex(@"^[A-Z0-9]+_[A-Z0-9_]+$")]
    private static partial Regex PositionCallsignRegex();
}
