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

    /// <summary>
    /// Pending EXT pre-arm: when true, PhaseRunner sets IsExtended on the first UpwindPhase
    /// of the next appended circuit and clears the flag. Null/absent in old snapshots
    /// (treated as false on load).
    /// </summary>
    public bool? ExtendNextUpwind { get; init; }

    /// <summary>
    /// Pending pattern-entry modifier (EXT leg / SA / MNA) for a queued-but-unbuilt entry.
    /// Kind (0=ExtendLeg, 1=ShortApproach, 2=NormalApproach) and target leg travel together;
    /// both null/absent in old snapshots (treated as no pending modifier on load).
    /// </summary>
    public byte? PendingEntryModifierKind { get; init; }

    /// <summary>Target pattern leg for the pending modifier (matches PatternEntryLeg). See <see cref="PendingEntryModifierKind"/>.</summary>
    public byte? PendingEntryModifierLeg { get; init; }

    /// <summary>
    /// Landing/option clearance pre-issued for a queued-but-unbuilt pattern entry (matches
    /// ClearanceType). Null/absent in old snapshots (treated as no pre-issued clearance on load).
    /// </summary>
    public byte? PendingLandingClearanceType { get; init; }

    /// <summary>
    /// Runway the pre-issued clearance names. Travels with <see cref="PendingLandingClearanceType"/>;
    /// either both are present or the aircraft has no pre-issued clearance.
    /// </summary>
    public string? PendingLandingClearanceRunwayId { get; init; }
}
