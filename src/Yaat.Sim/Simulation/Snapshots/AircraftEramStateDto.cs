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

    // ERAM FDB line-4 HSF annotation, set via QS. Distinct from the STARS scratchpad.
    public string? AssignedHeading { get; init; }
    public string? AssignedSpeed { get; init; }
    public string? FreeText { get; init; }

    // DRI / separation halo toggled via QP J (Standard=1) / QP T (ReducedSeparation=2); null = no halo.
    public int? DriHaloType { get; init; }

    // CRR group membership label (LF command); drives the FDB CrrGroup field + Range Data Block. Null = ungrouped.
    public string? CrrGroupLabel { get; init; }

    public List<EramPointoutStateDto>? Pointouts { get; init; }
    public List<TcpDto>? ForcedPointoutsTo { get; init; }

    // QH-frozen track: parked at a fixed location, unpaired from the target, exempt from coast/auto-drop.
    public bool IsFrozen { get; init; }
    public double? FrozenLat { get; init; }
    public double? FrozenLon { get; init; }
    public int? FrozenAltitude { get; init; }

    // Transient Field-E accepted indicator (Oxxx/Kxxx): the sector that owned the Track before the accept,
    // whether it was force-taken, and the sim-elapsed accept time. Broadcast enforces the 30 s window.
    public TrackOwnerDto? RecentHandoffPreviousOwner { get; init; }
    public bool RecentHandoffWasForced { get; init; }
    public double? RecentHandoffAcceptedAtSeconds { get; init; }
}
