namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftEramStateDto
{
    // Each ERAM sector's leader, DRI halo and dwell lock for this track. Null/empty = every sector at CRC's defaults.
    public List<EramSectorDisplayDto>? SectorDisplays { get; init; }

    // Sector IDs that marked this aircraft on-frequency (the ERAM VCI indicator). Null/empty = none.
    public List<string>? OnFrequencySectorIds { get; init; }
    public int? InterimAltitude { get; init; }
    public int? LocalInterimAltitude { get; init; }
    public int? ProcedureAltitude { get; init; }
    public int? ControllerEnteredAltitude { get; init; }

    // ERAM FDB line-4 HSF annotation, set via QS. Distinct from the STARS scratchpad.
    public string? AssignedHeading { get; init; }
    public string? AssignedSpeed { get; init; }
    public string? FreeText { get; init; }

    // CRR group membership label (LF command); drives the FDB CrrGroup field + Range Data Block. Null = ungrouped.
    public string? CrrGroupLabel { get; init; }

    public List<EramPointoutStateDto>? Pointouts { get; init; }

    // ERAM sectors that minimized the point-out data block (QP <FLID>) / cycled it to an FDB (bare FLID). Null = none.
    public List<EramSectorKeyDto>? PointoutMinimizedSectors { get; init; }
    public List<EramSectorKeyDto>? FdbOpenSectors { get; init; }

    // QH-frozen track: parked at a fixed location, unpaired from the target, exempt from coast/auto-drop.
    public bool IsFrozen { get; init; }
    public double? FrozenLat { get; init; }
    public double? FrozenLon { get; init; }
    public int? FrozenAltitude { get; init; }

    // QT Coast Track: the anchor and its sim time, the displayed altitude (hundreds of feet), the speed (knots), the
    // course held after the route (degrees true) and the route fixes flown from the anchor. Null/false = not coasting.
    public bool IsCoastTrack { get; init; }
    public double? CoastLat { get; init; }
    public double? CoastLon { get; init; }
    public double? CoastStartSeconds { get; init; }
    public int? CoastAltitude { get; init; }
    public int? CoastSpeed { get; init; }
    public double? CoastTrueCourse { get; init; }
    public List<LatLon>? CoastRoute { get; init; }

    // Transient Field-E accepted indicator (Oxxx/Kxxx): the sector that owned the Track before the accept,
    // whether it was force-taken, and the sim-elapsed accept time. Broadcast enforces the 30 s window.
    public TrackOwnerDto? RecentHandoffPreviousOwner { get; init; }
    public bool RecentHandoffWasForced { get; init; }
    public double? RecentHandoffAcceptedAtSeconds { get; init; }

    // Vertical-conformance latch and the assignment it was latched against. False/null (older data) re-evaluates on the
    // next tick, as a new aircraft does.
    public bool ReachedAssignedAltitude { get; init; }
    public EramConformanceKeyDto? ConformanceKey { get; init; }
}

public sealed class EramConformanceKeyDto
{
    public int? AssignedFeet { get; init; }
    public int? BlockFloorFeet { get; init; }
    public bool IsAbove { get; init; }
}

public sealed class EramSectorKeyDto
{
    public required string Facility { get; init; }
    public required string Sector { get; init; }
}

// One sector's display state for a track: leader direction and length, DRI halo (Standard=1, ReducedSeparation=2) and
// dwell lock. Null/false = CRC's default.
public sealed class EramSectorDisplayDto
{
    public required string Facility { get; init; }
    public required string Sector { get; init; }
    public int? LeaderDirection { get; init; }
    public int? LeaderLength { get; init; }
    public int? DriHaloType { get; init; }
    public bool IsDwellLocked { get; init; }
}
