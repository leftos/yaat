namespace Yaat.Sim.ControllerAi;

/// <summary>
/// One AI-staffed position: the real vNAS identity it acts as (used for TRACK/HO/ACCEPT/HFR/REL exactly like a human
/// staffing that position), its role, the radio name pilots address it by, and the airports it answers for
/// (tower-cab roles: the facility's airport; approach: the STARS area's underlying airports; center: none).
/// </summary>
public sealed record AiPositionConfig(
    ControlRole Role,
    TrackOwner Identity,
    Tcp? Tcp,
    string PositionId,
    string Callsign,
    string? RadioName,
    string FacilityId,
    IReadOnlyList<string> AirportIds
)
{
    /// <summary>The GND / TWR / APP / CTR code pilots resolve the position by.</summary>
    public string PositionType => ControlRoles.PositionType(Role);

    /// <summary>
    /// Value equality over every field including the airport list (the synthesized record equality would compare the
    /// list by reference), so a staffing publisher can tell "same positions, one radio name changed" from "unchanged".
    /// </summary>
    public bool Equals(AiPositionConfig? other) =>
        other is not null
        && Role == other.Role
        && Identity == other.Identity
        && Tcp == other.Tcp
        && string.Equals(PositionId, other.PositionId, StringComparison.Ordinal)
        && string.Equals(Callsign, other.Callsign, StringComparison.Ordinal)
        && string.Equals(RadioName, other.RadioName, StringComparison.Ordinal)
        && string.Equals(FacilityId, other.FacilityId, StringComparison.Ordinal)
        && AirportIds.SequenceEqual(other.AirportIds, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role);
        hash.Add(Identity);
        hash.Add(Tcp);
        hash.Add(PositionId, StringComparer.Ordinal);
        hash.Add(Callsign, StringComparer.Ordinal);
        hash.Add(RadioName, StringComparer.Ordinal);
        hash.Add(FacilityId, StringComparer.Ordinal);
        foreach (var airportId in AirportIds)
        {
            hash.Add(airportId, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
