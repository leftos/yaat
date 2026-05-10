using Yaat.Sim.Phases;
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

    /// <summary>
    /// Persistent pattern direction set by MLT/MRT/CTO MLT/CTO MRT/CTOMLT/CTOMRT/GA MLT/GA MRT.
    /// Survives phase-list clearing by FH/TR/TL vectors so that a subsequent re-entry
    /// (auto-cycle after T&G, GoAround re-enter, etc.) honors the controller's last
    /// explicit pattern-direction intent. Cleared by CLAND/LAHSO (full-stop intent).
    /// Null = no persistent direction set; PhaseRunner falls back to PhaseList.TrafficDirection.
    /// </summary>
    public PatternDirection? TrafficDirection { get; set; }

    public AircraftPatternDto ToSnapshot() =>
        new()
        {
            SizeOverrideNm = SizeOverrideNm,
            AltitudeOverrideFt = AltitudeOverrideFt,
            TrafficDirection = TrafficDirection.HasValue ? (byte)TrafficDirection.Value : null,
        };

    public static AircraftPattern FromSnapshot(AircraftPatternDto dto) =>
        new()
        {
            SizeOverrideNm = dto.SizeOverrideNm,
            AltitudeOverrideFt = dto.AltitudeOverrideFt,
            TrafficDirection = dto.TrafficDirection.HasValue ? (PatternDirection)dto.TrafficDirection.Value : null,
        };
}
