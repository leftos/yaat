namespace Yaat.Sim.Pilot;

/// <summary>
/// Maps a vNAS position callsign (e.g. "OAK_TWR", "NCT_F_APP") to the spoken short form a pilot
/// uses on the radio ("Tower", "Approach", "Departure", "Center", "Ground", "Ramp", "Clearance").
/// Falls back to the verbatim callsign when the suffix is unmapped so pilot speech stays
/// intelligible for unconventional positions.
/// </summary>
public static class FacilityShortname
{
    public static string From(string positionCallsign)
    {
        if (string.IsNullOrWhiteSpace(positionCallsign))
        {
            return positionCallsign;
        }

        var trimmed = positionCallsign.Trim();
        var lastUnderscore = trimmed.LastIndexOf('_');
        var suffix = lastUnderscore >= 0 ? trimmed[(lastUnderscore + 1)..] : trimmed;
        return suffix.ToUpperInvariant() switch
        {
            "TWR" or "ATCT" => "Tower",
            "APP" => "Approach",
            "DEP" => "Departure",
            "GND" => "Ground",
            "CTR" => "Center",
            "RMP" => "Ramp",
            "DEL" or "CD" => "Clearance",
            "FSS" => "Radio",
            _ => trimmed,
        };
    }
}
