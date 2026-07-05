namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftEramStateDto
{
    public required bool IsDwellLocked { get; init; }
    public required bool IsVci { get; init; }
    public int? LeaderDirection { get; init; }
    public int? LeaderLength { get; init; }
    public int? InterimAltitude { get; init; }
    public int? LocalInterimAltitude { get; init; }
    public int? ProcedureAltitude { get; init; }
    public int? ControllerEnteredAltitude { get; init; }
    public List<EramPointoutStateDto>? Pointouts { get; init; }
    public List<TcpDto>? ForcedPointoutsTo { get; init; }

    // QH-frozen track: parked at a fixed location, unpaired from the target, exempt from coast/auto-drop.
    public bool IsFrozen { get; init; }
    public double? FrozenLat { get; init; }
    public double? FrozenLon { get; init; }
    public int? FrozenAltitude { get; init; }
}
