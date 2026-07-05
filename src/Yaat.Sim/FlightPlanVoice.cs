using System.Text.RegularExpressions;

namespace Yaat.Sim;

/// <summary>
/// Couples the flight-plan remarks to the CRC voice-type field (<see cref="AircraftVoice.Type"/>:
/// 1=Full, 2=ReceiveOnly, 3=TextOnly). The <b>remarks are canonical</b>: a <c>/v/</c>, <c>/r/</c>, or
/// <c>/t/</c> marker declares the pilot's voice capability, and full voice is implied when no marker is
/// present. This is a VATSIM operational convention (originating from legacy client remarks prefixes) with
/// no FAA 7110.65/AIM equivalent — every NAS aircraft carries two-way voice radio by regulation, so the
/// real system has no voice/text flight-plan field. The remarks field itself is the flight-data "remarks"
/// block (7110.65 §2-3-2 block 26). vNAS parses the same convention into its common <c>VoiceType</c>
/// {Unknown, Full, ReceiveOnly, TextOnly}; YAAT stores those values as <see cref="AircraftVoice.Type"/>.
/// </summary>
public static class FlightPlanVoice
{
    public const int Full = 1;
    public const int ReceiveOnly = 2;
    public const int TextOnly = 3;

    // A voice marker is a slash-delimited single letter so free-text remarks (STS/…, SEL/…, PBN/…) cannot
    // false-positive. Case-insensitive: /v/ /r/ /t/ or /V/ /R/ /T/.
    private static readonly Regex MarkerRegex = new(@"/[vrt]/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Derives the voice type from the flight-plan remarks. The leftmost <c>/v/</c>·<c>/r/</c>·<c>/t/</c>
    /// marker wins; full voice is implied when none is present.
    /// </summary>
    public static int ParseVoiceType(string? remarks)
    {
        if (string.IsNullOrEmpty(remarks))
        {
            return Full;
        }

        var match = MarkerRegex.Match(remarks);
        if (!match.Success)
        {
            return Full;
        }

        return char.ToLowerInvariant(match.Value[1]) switch
        {
            'r' => ReceiveOnly,
            't' => TextOnly,
            _ => Full,
        };
    }

    /// <summary>
    /// Rewrites the remarks so they carry exactly the marker for <paramref name="voiceType"/>: strips any
    /// existing <c>/v/</c>·<c>/r/</c>·<c>/t/</c> first, then prepends the new marker, preserving the
    /// remaining free text. Idempotent.
    /// </summary>
    public static string ApplyVoiceMarker(string? remarks, int voiceType)
    {
        var stripped = StripMarkers(remarks ?? "");
        var marker = voiceType switch
        {
            ReceiveOnly => "/r/",
            TextOnly => "/t/",
            _ => "/v/",
        };
        return string.IsNullOrEmpty(stripped) ? marker : $"{marker} {stripped}";
    }

    private static string StripMarkers(string remarks) =>
        string.Join(' ', MarkerRegex.Replace(remarks, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
