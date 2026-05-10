namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftPatternDto
{
    public double? SizeOverrideNm { get; init; }
    public double? AltitudeOverrideFt { get; init; }

    /// <summary>
    /// Persistent pattern direction (0=Left, 1=Right). Null when no MLT/MRT was issued
    /// or after CLAND/LAHSO. Old snapshots without this field default to null.
    /// </summary>
    public byte? TrafficDirection { get; init; }
}
