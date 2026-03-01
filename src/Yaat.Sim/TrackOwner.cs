namespace Yaat.Sim;

public record TrackOwner(
    string Callsign,
    string? FacilityId,
    int? Subset,
    string? SectorId,
    TrackOwnerType OwnerType)
{
    public bool IsNasPosition =>
        OwnerType is TrackOwnerType.Eram or TrackOwnerType.Stars;

    public static TrackOwner CreateStars(
        string callsign, string facilityId, int subset, string sectorId) =>
        new(callsign, facilityId, subset, sectorId, TrackOwnerType.Stars);

    public static TrackOwner CreateNonNas(string callsign) =>
        new(callsign, null, null, null, TrackOwnerType.Other);
}
