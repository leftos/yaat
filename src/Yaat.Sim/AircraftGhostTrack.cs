using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Unsupported / ghost track display state. <see cref="IsUnsupported"/> = stationary,
/// no surveillance data; ghost overlay positions override the real aircraft's lat/lon
/// for STARS rendering. <see cref="IsOverlay"/> distinguishes a ghost attached to a
/// real scenario aircraft (AID+slew) from a pure phantom data block (DA/VP typing
/// for a callsign with no aircraft body) — the operator-facing Aircraft List uses
/// this to keep overlaid scenario aircraft visible while hiding phantoms.
/// <see cref="IsVehicle"/> tags ground vehicles (tug, follow-me) which are excluded
/// from STARS regardless of altitude.
/// </summary>
public class AircraftGhostTrack
{
    public bool IsUnsupported { get; set; }
    public bool IsOverlay { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? AirportId { get; set; }
    public string? RunwayId { get; set; }
    public bool IsVehicle { get; set; }

    public AircraftGhostTrackDto ToSnapshot() =>
        new()
        {
            IsUnsupported = IsUnsupported,
            IsOverlay = IsOverlay,
            Latitude = Latitude,
            Longitude = Longitude,
            AirportId = AirportId,
            RunwayId = RunwayId,
            IsVehicle = IsVehicle,
        };

    public static AircraftGhostTrack FromSnapshot(AircraftGhostTrackDto dto) =>
        new()
        {
            IsUnsupported = dto.IsUnsupported,
            IsOverlay = dto.IsOverlay,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            AirportId = dto.AirportId,
            RunwayId = dto.RunwayId,
            IsVehicle = dto.IsVehicle,
        };
}
