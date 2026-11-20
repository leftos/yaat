using System.Text.RegularExpressions;

namespace Yaat.Sim;

/// <summary>
/// Where a flight-plan remark says the aircraft parks: an area name (<see cref="Area"/>, <c>SIGNATURE</c> or <c>PLOT 9</c>),
/// a stand id (<see cref="Stand"/>, <c>F14</c> or <c>9-5A</c>), or both when the remark gives the stand in parentheses after
/// the area. Upper-cased; the area's inner whitespace is collapsed to one space.
/// </summary>
public sealed record ParkingRemark(string? Area, string? Stand);

/// <summary>
/// Reads where an arrival parks from its flight-plan remarks. Training scenarios write it as
/// <c>/V/ GATE F14, 28R</c>, <c>/V/ PARKING SIGNATURE, 28R</c> or <c>/v/ Parking Plot 9 (9-5A), 28R</c>: the keyword
/// <c>GATE</c>, <c>PARKING</c> or <c>PARK</c>, then an area name or a stand id. The trailing runway names the arrival runway,
/// not the spot, and is not read. A VATSIM scenario convention, not an FAA flight-plan field.
/// </summary>
public static partial class FlightPlanParking
{
    // A stand id: up to three letters, one to three digits, an optional letter, then any hyphenated parts (F14, SIG4, 9-5A).
    // It must contain a digit, so "Gate UNKN" (no spot) and "GOLDEN GATE" (callsign text) name nothing.
    private const string StandId = @"[A-Z]{0,3}\d{1,3}[A-Z]?(?:-[A-Z0-9]+)*";

    // GATE takes only a stand id. PARKING (or PARK, optionally "AT") takes a stand id, or else area words up to a comma, an
    // opening parenthesis or the end, optionally followed by the stand in parentheses; the airport's parkingAreas decide what
    // an area covers. A bare id with no keyword ("/V/ C3") is never read.
    [GeneratedRegex(
        @"(?<![A-Z0-9])(?:"
            + $@"GATE\s+@?(?<stand>{StandId})(?![A-Z0-9-])"
            + @"|PARK(?:ING)?\s+(?:AT\s+)?@?(?:"
            + $@"(?<stand>{StandId})(?![A-Z0-9-])"
            + @"|(?<area>[A-Z0-9][^,()]*?)\s*(?:\(\s*(?<stand>[A-Z0-9]+(?:-[A-Z0-9]+)*)\s*\))?\s*(?=[,(]|$)"
            + @"))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ParkingRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// The parking the leftmost <c>GATE</c>/<c>PARKING</c> remark names, or null when <paramref name="remarks"/> names none.
    /// </summary>
    public static ParkingRemark? Parse(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks))
        {
            return null;
        }

        Match match = ParkingRegex().Match(remarks);
        if (!match.Success)
        {
            return null;
        }

        Group area = match.Groups["area"];
        Group stand = match.Groups["stand"];
        return new ParkingRemark(
            area.Success ? WhitespaceRegex().Replace(area.Value, " ").Trim().ToUpperInvariant() : null,
            stand.Success ? stand.Value.ToUpperInvariant() : null
        );
    }
}
