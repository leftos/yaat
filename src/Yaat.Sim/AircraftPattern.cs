using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Per-aircraft pattern overrides. Null fields fall back to category defaults
/// (downwind offset, pattern altitude). Set by CM/DM during pattern mode and
/// by MLT/MRT/CTO/GA when the controller specifies an explicit altitude.
/// </summary>
public class AircraftPattern
{
    /// <summary>Override for pattern downwind offset distance (NM). Null uses category default.</summary>
    public double? SizeOverrideNm { get; set; }

    /// <summary>Override for pattern altitude (feet MSL). Null uses category-based default.</summary>
    public double? AltitudeOverrideFt { get; set; }

    public AircraftPatternDto ToSnapshot() => new() { SizeOverrideNm = SizeOverrideNm, AltitudeOverrideFt = AltitudeOverrideFt };

    public static AircraftPattern FromSnapshot(AircraftPatternDto dto) =>
        new() { SizeOverrideNm = dto.SizeOverrideNm, AltitudeOverrideFt = dto.AltitudeOverrideFt };
}
