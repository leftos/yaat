using Yaat.Sim.Phases;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Approach-related per-aircraft state: the controller-issued expectation, deferred
/// approach clearance pending fix arrival, and the visual-approach pilot reports
/// (field-in-sight, traffic-in-sight, follow-the-leader).
/// </summary>
public class AircraftApproachState
{
    public string? Expected { get; set; }

    /// <summary>
    /// Approach clearance issued while the aircraft is still on a STAR en route to the
    /// approach connecting fix. Activated when the aircraft reaches the connecting fix
    /// via normal navigation. Null when no deferred approach is pending.
    /// </summary>
    public PendingApproachInfo? PendingClearance { get; set; }

    public bool HasReportedFieldInSight { get; set; }
    public bool HasReportedTrafficInSight { get; set; }

    /// <summary>
    /// Callsign of the most recently acquired traffic (RTIS or RTISF). A bare
    /// FOLLOW with no explicit argument defaults to this value; a second RTIS/RTISF
    /// for different traffic replaces it. Null until the first successful report.
    /// A bare forced verb (FOLLOWF/RTISF) with no typed callsign also populates this
    /// from a still-pending RTIS (<see cref="TrafficAcquisitionObservation"/>), so a
    /// called-but-not-yet-acquired target can be followed without re-typing it.
    /// </summary>
    public string? LastReportedTrafficCallsign { get; set; }

    public string? FollowingCallsign { get; set; }

    /// <summary>
    /// Per-aircraft distance-from-threshold (NM) at which this aircraft settles at final
    /// approach speed (Vref) on final. Assigned once at spawn from the aircraft's identity so
    /// each aircraft slows down at its own distance — reproducing the live-network spread where
    /// pilots reduce to FAS anywhere from the tight ~2 NM competent floor out to ~5 NM (a draggy
    /// early slow-down that compresses the arrival stream). <see cref="Phases.Tower.FinalApproachPhase"/>
    /// reads this and slides its whole two-stage decel profile outward accordingly. Null falls
    /// back to the phase's default <c>FasReachGateNm</c> (2.0 NM), so aircraft from pre-feature
    /// recordings and directly-constructed test aircraft keep the original tight behavior.
    /// Snapshot-serialized so the decision is durable across rewind, restore, and replay.
    /// </summary>
    public double? FinalApproachFasReachGateNm { get; set; }

    /// <summary>
    /// Smallest gap (nm) seen to the follow target since the current FOLLOW was issued.
    /// Used by <see cref="Phases.AirborneFollowHelper.CheckLeadLifecycle"/> to detect
    /// monotonic divergence (runaway distance). Null until first measurement; reset
    /// to null whenever <see cref="FollowingCallsign"/> is set or cleared.
    /// </summary>
    public double? FollowBestGapNm { get; set; }

    /// <summary>
    /// Seconds during which the gap to the follow target has been strictly greater
    /// than <see cref="FollowBestGapNm"/>. Reset to zero when the gap re-converges or
    /// when FOLLOW is (re)issued. Once it exceeds the runaway grace window, the
    /// helper cancels follow with an "unable to catch up" pilot transmission.
    /// </summary>
    public double FollowRunawaySeconds { get; set; }

    /// <summary>
    /// One-way latch: once the in-trail arrival-spacing manager hands speed authority back
    /// (a manual speed command was issued to this generator arrival, its speed restrictions
    /// were deleted, or the student controller took the track), the manager never resumes
    /// auto-spacing this aircraft. A plain flag is required because
    /// <see cref="ControlTargets.HasExplicitSpeedCommand"/> is cleared by "resume normal
    /// speed", which would otherwise let the manager silently re-engage. Snapshot-serialized.
    /// </summary>
    public bool AutoSpacingReleased { get; set; }

    /// <summary>
    /// Deferred pattern-leg reports armed by the controller's <c>REPORT</c> command. When set,
    /// the corresponding pattern phase voices a "turning crosswind/downwind/base/final" pilot
    /// report on each circuit. These flags persist across laps (the phase instances are rebuilt
    /// fresh by <see cref="Phases.PatternBuilder.BuildNextCircuit"/> but the armed state lives
    /// here), giving the requested re-arm-every-round behavior. Cleared by <c>REPORT OFF</c>,
    /// landing, or track teardown. Snapshot-serialized.
    /// </summary>
    public bool ReportArmedCrosswind { get; set; }
    public bool ReportArmedDownwind { get; set; }
    public bool ReportArmedBase { get; set; }
    public bool ReportArmedFinal { get; set; }

    /// <summary>
    /// Deferred N-mile-final report armed by <c>REPORT &lt;n&gt; FINAL</c>: the distance (NM) to the
    /// runway threshold at which the pilot voices an "n-mile final" report. One-shot — cleared
    /// when it fires. Null when unarmed. Snapshot-serialized.
    /// </summary>
    public int? ReportFinalMileTarget { get; set; }

    /// <summary>
    /// Deferred at-fix report armed by <c>REPORT &lt;fix&gt;</c>: the pilot voices "at {fix}" when the
    /// aircraft reaches the fix. The fix coordinates are resolved at arm time so the armed state
    /// is self-contained across snapshot restore. One-shot — cleared when it fires. Null when
    /// unarmed. Snapshot-serialized.
    /// </summary>
    public string? ReportAtFixName { get; set; }
    public double? ReportAtFixLat { get; set; }
    public double? ReportAtFixLon { get; set; }

    /// <summary>
    /// Clears every armed deferred report. Called by <c>REPORT OFF</c> and on full-stop landing
    /// (a touch-and-go deliberately does NOT clear, so pattern-leg reports re-arm next circuit).
    /// </summary>
    public void ClearArmedReports()
    {
        ReportArmedCrosswind = false;
        ReportArmedDownwind = false;
        ReportArmedBase = false;
        ReportArmedFinal = false;
        ReportFinalMileTarget = null;
        ReportAtFixName = null;
        ReportAtFixLat = null;
        ReportAtFixLon = null;
    }

    public AircraftApproachStateDto ToSnapshot() =>
        new()
        {
            Expected = Expected,
            PendingClearance = PendingClearance?.ToSnapshot(),
            HasReportedFieldInSight = HasReportedFieldInSight,
            HasReportedTrafficInSight = HasReportedTrafficInSight,
            LastReportedTrafficCallsign = LastReportedTrafficCallsign,
            FollowingCallsign = FollowingCallsign,
            FinalApproachFasReachGateNm = FinalApproachFasReachGateNm,
            FollowBestGapNm = FollowBestGapNm,
            FollowRunawaySeconds = FollowRunawaySeconds,
            AutoSpacingReleased = AutoSpacingReleased,
            ReportArmedCrosswind = ReportArmedCrosswind,
            ReportArmedDownwind = ReportArmedDownwind,
            ReportArmedBase = ReportArmedBase,
            ReportArmedFinal = ReportArmedFinal,
            ReportFinalMileTarget = ReportFinalMileTarget,
            ReportAtFixName = ReportAtFixName,
            ReportAtFixLat = ReportAtFixLat,
            ReportAtFixLon = ReportAtFixLon,
        };

    public static AircraftApproachState FromSnapshot(AircraftApproachStateDto dto) =>
        new()
        {
            Expected = dto.Expected,
            PendingClearance = dto.PendingClearance is not null ? PendingApproachInfo.FromSnapshot(dto.PendingClearance) : null,
            HasReportedFieldInSight = dto.HasReportedFieldInSight,
            HasReportedTrafficInSight = dto.HasReportedTrafficInSight,
            LastReportedTrafficCallsign = dto.LastReportedTrafficCallsign,
            FollowingCallsign = dto.FollowingCallsign,
            FinalApproachFasReachGateNm = dto.FinalApproachFasReachGateNm,
            FollowBestGapNm = dto.FollowBestGapNm,
            FollowRunawaySeconds = dto.FollowRunawaySeconds,
            AutoSpacingReleased = dto.AutoSpacingReleased,
            ReportArmedCrosswind = dto.ReportArmedCrosswind,
            ReportArmedDownwind = dto.ReportArmedDownwind,
            ReportArmedBase = dto.ReportArmedBase,
            ReportArmedFinal = dto.ReportArmedFinal,
            ReportFinalMileTarget = dto.ReportFinalMileTarget,
            ReportAtFixName = dto.ReportAtFixName,
            ReportAtFixLat = dto.ReportAtFixLat,
            ReportAtFixLon = dto.ReportAtFixLon,
        };
}
