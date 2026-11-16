using System.Globalization;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The text a click fills into the Scope's preview. A track: when the entry reads as a pilot command
/// (<see cref="ScopeEntryClassifier"/>), its callsign goes first (after a leading <c>PILOT</c> word), in place of a first word
/// that is already a known aircraft's callsign; any other entry gets the callsign appended. A point: its FRD appended, else its
/// ERAM lat/long (<c>//DDMMN/DDDMMW</c>).
/// </summary>
internal static class ScopeEntryFill
{
    private const string PilotWord = "PILOT";

    private const string OverridePrefix = "** ";

    /// <summary>
    /// <paramref name="entry"/> with <paramref name="callsign"/> filled in, read on a <paramref name="profile"/> profile;
    /// <paramref name="isKnownCallsign"/> says whether a word is a known aircraft's callsign.
    /// </summary>
    public static string Track(string entry, string callsign, ScopeProfileKind profile, Func<string, bool> isKnownCallsign)
    {
        string trimmed = entry.Trim();
        if (trimmed.Length == 0)
        {
            return callsign;
        }

        if (ScopeEntryClassifier.Classify(trimmed, profile).Kind != ScopeEntryKind.Pilot)
        {
            return EndsWithCallsign(trimmed, callsign) ? trimmed : $"{trimmed} {callsign}";
        }

        (string prefix, string rest) = SplitPrefixes(trimmed);
        string first = FirstWord(rest);
        if ((first.Length > 0) && isKnownCallsign(first))
        {
            rest = rest[first.Length..].TrimStart();
        }

        return (rest.Length == 0) ? $"{prefix}{callsign}" : $"{prefix}{callsign} {rest}";
    }

    /// <summary>
    /// Whether filling <paramref name="callsign"/> would change <paramref name="entry"/> (case aside): false when its pilot entry
    /// already names the track first, or its last word is the track's callsign, where a click acts as with an empty preview.
    /// </summary>
    public static bool ChangesTrack(string entry, string callsign, ScopeProfileKind profile, Func<string, bool> isKnownCallsign) =>
        !EndsWithCallsign(entry.Trim(), callsign)
        && !string.Equals(Track(entry, callsign, profile, isKnownCallsign), entry.Trim(), StringComparison.OrdinalIgnoreCase);

    // A leading PILOT word, then a leading ** override, each kept in front of the filled callsign; the rest after them.
    private static (string Prefix, string Remainder) SplitPrefixes(string trimmed)
    {
        string prefix = "";
        string rest = trimmed;
        if (string.Equals(FirstWord(rest), PilotWord, StringComparison.OrdinalIgnoreCase))
        {
            prefix = $"{FirstWord(rest)} ";
            rest = rest[PilotWord.Length..].TrimStart();
        }

        if (rest.StartsWith(OverridePrefix, StringComparison.Ordinal))
        {
            prefix += OverridePrefix;
            rest = rest[OverridePrefix.Length..].TrimStart();
        }

        return (prefix, rest);
    }

    private static bool EndsWithCallsign(string trimmed, string callsign)
    {
        int space = trimmed.LastIndexOf(' ');
        return string.Equals((space < 0) ? trimmed : trimmed[(space + 1)..], callsign, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><paramref name="entry"/> with the point appended: <paramref name="frd"/> when known, else its ERAM lat/long.</summary>
    public static string Point(string entry, LatLon point, string? frd)
    {
        string text = frd ?? EramLatLon(point);
        string trimmed = entry.Trim();
        return (trimmed.Length == 0) ? text : $"{trimmed} {text}";
    }

    /// <summary>The point as an ERAM lat/long location, <c>//DDMMN/DDDMMW</c>, rounded to the minute.</summary>
    public static string EramLatLon(LatLon point) =>
        $"//{DegreesMinutes(Math.Abs(point.Lat), 2)}{((point.Lat >= 0) ? 'N' : 'S')}/"
        + $"{DegreesMinutes(Math.Abs(point.Lon), 3)}{((point.Lon < 0) ? 'W' : 'E')}";

    private static string DegreesMinutes(double degrees, int degreeDigits)
    {
        int totalMinutes = (int)Math.Round(degrees * 60, MidpointRounding.AwayFromZero);
        string whole = (totalMinutes / 60).ToString(CultureInfo.InvariantCulture).PadLeft(degreeDigits, '0');
        return whole + (totalMinutes % 60).ToString("D2", CultureInfo.InvariantCulture);
    }

    private static string FirstWord(string text)
    {
        int space = text.IndexOf(' ');
        return (space < 0) ? text : text[..space];
    }
}
