namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Snapshot form of <see cref="AircraftMilitaryRoute"/>. Every property is optional so a snapshot
/// written before schema v17 restores cleanly as "not on a military route".
/// </summary>
public sealed class AircraftMilitaryRouteDto
{
    public string? Designator { get; init; }
    public int Kind { get; init; }
    public int Status { get; init; }
    public string? Direction { get; init; }
    public string? EntryPointId { get; init; }
    public string? ExitPointId { get; init; }
    public int CurrentSegmentIndex { get; init; } = -1;
    public int AltitudeSource { get; init; }
    public int? AssignedOverrideFt { get; init; }
    public int? AssignedFloorFt { get; init; }
    public int? AssignedCeilingFt { get; init; }
    public bool Marsa { get; init; }
    public double? AppliedFloorFt { get; init; }
    public double? AppliedCeilingFt { get; init; }
    public uint? PreRouteSquawk { get; init; }
}
