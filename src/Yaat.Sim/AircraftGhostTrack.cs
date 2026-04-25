using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Unsupported / ghost track display state. <see cref="IsUnsupported"/> = stationary,
/// no surveillance data; ghost overlay positions override the real aircraft's lat/lon
/// for STARS rendering. <see cref="IsVehicle"/> tags ground vehicles (tug, follow-me)
/// which are excluded from STARS regardless of altitude.
/// </summary>
public class AircraftGhostTrack
{
    public bool IsUnsupported { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? AirportId { get; set; }
    public string? RunwayId { get; set; }
    public bool IsVehicle { get; set; }

    public AircraftGhostTrackDto ToSnapshot() =>
        new()
        {
            IsUnsupported = IsUnsupported,
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
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            AirportId = dto.AirportId,
            RunwayId = dto.RunwayId,
            IsVehicle = dto.IsVehicle,
        };
}
