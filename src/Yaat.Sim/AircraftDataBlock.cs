using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Binding between an aircraft's surveillance track and its flight-plan datablock, for STARS
/// Track Reposition (<c>&lt;TRK RPOS&gt;</c>).
/// </summary>
public enum DataBlockBinding
{
    /// <summary>Datablock rides the surveillance track — the normal 1:1 case (no reposition).</summary>
    Bound,

    /// <summary>
    /// Datablock detached to a fixed location (an unsupported datablock). The surveillance track
    /// stays visible as a bare unassociated LDB at its real position; a second STARS track carries
    /// the flight plan at the parked location.
    /// </summary>
    Parked,
}

/// <summary>
/// Surveillance/datablock decoupling state for STARS Track Reposition (<c>&lt;TRK RPOS&gt;</c>).
/// When <see cref="Binding"/> is <see cref="DataBlockBinding.Parked"/>, the broadcast layer emits a
/// second STARS track with id <see cref="DetachedId"/> carrying the flight plan at
/// <see cref="Latitude"/>/<see cref="Longitude"/>, owned by <see cref="CreatedBy"/>, while the
/// aircraft's own track renders as a bare unassociated LDB. <see cref="DataBlockBinding.Bound"/>
/// (the default) leaves every existing single-track behavior unchanged.
/// </summary>
public class AircraftDataBlock
{
    public DataBlockBinding Binding { get; set; } = DataBlockBinding.Bound;

    /// <summary>Fixed latitude of the parked datablock; null when <see cref="Binding"/> is Bound.</summary>
    public double? Latitude { get; set; }

    /// <summary>Fixed longitude of the parked datablock; null when <see cref="Binding"/> is Bound.</summary>
    public double? Longitude { get; set; }

    /// <summary>STARS track id of the parked datablock (e.g. <c>RPOS{callsign}</c>); null when Bound.</summary>
    public string? DetachedId { get; set; }

    /// <summary>Controller that created (owns) the unsupported datablock; null when Bound.</summary>
    public TrackOwner? CreatedBy { get; set; }

    public AircraftDataBlockDto ToSnapshot() =>
        new()
        {
            Binding = (int)Binding,
            Latitude = Latitude,
            Longitude = Longitude,
            DetachedId = DetachedId,
            CreatedBy = CreatedBy?.ToSnapshot(),
        };

    public static AircraftDataBlock FromSnapshot(AircraftDataBlockDto dto) =>
        new()
        {
            Binding = (DataBlockBinding)dto.Binding,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            DetachedId = dto.DetachedId,
            CreatedBy = dto.CreatedBy is not null ? TrackOwner.FromSnapshot(dto.CreatedBy) : null,
        };
}
