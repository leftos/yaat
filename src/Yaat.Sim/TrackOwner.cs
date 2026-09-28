using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public record TrackOwner(string Callsign, string? FacilityId, int? Subset, string? SectorId, TrackOwnerType OwnerType)
{
    public bool IsNasPosition => OwnerType is TrackOwnerType.Eram or TrackOwnerType.Stars;

    public bool IsTcp(Tcp tcp) => Subset == tcp.Subset && SectorId == tcp.SectorId;

    /// <summary>
    /// Returns true if this <see cref="TrackOwner"/> represents the same position as <paramref name="other"/>.
    /// Matches by callsign first, vetoed when both sides name a facility and the facilities differ (one callsign
    /// staffed in two facilities is two positions), then falls back to TCP identity (facility + subset + sector).
    /// This mirrors the STARS scope matching where different callsigns can share a TCP
    /// (e.g. OAK_TWR and OAK_GND both use TCP 3O).
    /// </summary>
    public bool MatchesPosition(TrackOwner other) =>
        (
            (Callsign == other.Callsign)
            && ((FacilityId is null) || (other.FacilityId is null) || string.Equals(FacilityId, other.FacilityId, StringComparison.OrdinalIgnoreCase))
        )
        || (
            FacilityId is not null
            && Subset is not null
            && SectorId is not null
            && FacilityId == other.FacilityId
            && Subset == other.Subset
            && string.Equals(SectorId, other.SectorId, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// True when <paramref name="other"/> is this position by callsign alone: the callsigns are equal and the facilities
    /// agree (or either side names none). Unlike <see cref="MatchesPosition"/> there is no TCP fallback, because
    /// tower-cab positions share a TCP (OAK_GND / OAK_TWR / OAK_DEL are all NCT 3O) and are still different positions.
    /// </summary>
    public bool IsSamePositionByCallsign(TrackOwner other) =>
        string.Equals(Callsign, other.Callsign, StringComparison.OrdinalIgnoreCase)
        && ((FacilityId is null) || (other.FacilityId is null) || string.Equals(FacilityId, other.FacilityId, StringComparison.OrdinalIgnoreCase));

    public static TrackOwner CreateStars(string callsign, string facilityId, int subset, string sectorId) =>
        new(callsign, facilityId, subset, sectorId, TrackOwnerType.Stars);

    public static TrackOwner CreateEram(string callsign, string facilityId, string sectorId) =>
        new(callsign, facilityId, null, sectorId, TrackOwnerType.Eram);

    public static TrackOwner CreateNonNas(string callsign) => new(callsign, null, null, null, TrackOwnerType.Other);

    public TrackOwnerDto ToSnapshot() =>
        new()
        {
            Callsign = Callsign,
            FacilityId = FacilityId,
            Subset = Subset,
            SectorId = SectorId,
            OwnerType = (int)OwnerType,
        };

    public static TrackOwner FromSnapshot(TrackOwnerDto dto) =>
        new(dto.Callsign, dto.FacilityId, dto.Subset, dto.SectorId, (TrackOwnerType)dto.OwnerType);
}
