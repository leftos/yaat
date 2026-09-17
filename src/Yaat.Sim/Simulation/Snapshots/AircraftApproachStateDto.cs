namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftApproachStateDto
{
    public string? Expected { get; init; }
    public PendingApproachDto? PendingClearance { get; init; }
    public required bool HasReportedFieldInSight { get; init; }
    public required bool HasReportedTrafficInSight { get; init; }
    public string? LastReportedTrafficCallsign { get; init; }
    public string? FollowingCallsign { get; init; }

    /// <summary>
    /// Per-aircraft final-approach-speed settle distance (NM). Nullable / non-required so older
    /// snapshots default to <see langword="null"/> → the phase's 2.0 NM competent floor.
    /// </summary>
    public double? FinalApproachFasReachGateNm { get; init; }

    /// <summary>
    /// In-trail auto-spacing released latch. Non-required so older snapshots default to
    /// <see langword="false"/>.
    /// </summary>
    public bool AutoSpacingReleased { get; init; }

    /// <summary>
    /// Same-runway arrival-protection ceiling ownership: the ceiling the pass stamped, and the one it displaced.
    /// Both nullable / non-required so older snapshots default to <see langword="null"/> — no ceiling is attributed
    /// to the pass, and it re-engages on the next tick if the conflict is still predicted.
    /// </summary>
    public double? SameRunwayProtectionCeilingKts { get; init; }
    public double? SameRunwayProtectionDisplacedCeilingKts { get; init; }

    /// <summary>
    /// The pass's "tower has instructed final approach speed" latch. Non-required so older snapshots default to
    /// <see langword="false"/> — the instruction is re-issued on the next tick if the conflict is still predicted and
    /// the follower is still outside the §5-7-1.b.4 window.
    /// </summary>
    public bool SameRunwayProtectionFasInstructed { get; init; }

    /// <summary>
    /// Deferred REPORT command armed state. All non-required so older snapshots default to
    /// unarmed (<see langword="false"/> / <see langword="null"/>).
    /// </summary>
    public bool ReportArmedCrosswind { get; init; }
    public bool ReportArmedDownwind { get; init; }
    public bool ReportArmedBase { get; init; }
    public bool ReportArmedFinal { get; init; }
    public int? ReportFinalMileTarget { get; init; }
    public string? ReportAtFixName { get; init; }
    public double? ReportAtFixLat { get; init; }
    public double? ReportAtFixLon { get; init; }
}
