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
    /// One-way latch: once the in-trail arrival-spacing manager hands speed authority back
    /// (a manual speed command was issued to this generator arrival, its speed restrictions
    /// were deleted, or the student controller took the track), the manager never resumes
    /// auto-spacing this aircraft. A plain flag is required because
    /// <see cref="ControlTargets.HasExplicitSpeedCommand"/> is cleared by "resume normal
    /// speed", which would otherwise let the manager silently re-engage. Snapshot-serialized.
    /// </summary>
    public bool AutoSpacingReleased { get; set; }

    /// <summary>
    /// The <see cref="ControlTargets.SpeedCeiling"/> the same-runway arrival-protection pass stamped on this
    /// aircraft, or null when the pass is not engaged — non-null means the simulated TRACON is holding the aircraft
    /// back so the arrival ahead can clear the runway before it crosses the threshold. Recording the stamped value
    /// rather than a bare "engaged" flag is what makes the release exact: when the conflict clears or the aircraft
    /// leaves the §5-7-1.b.4 adjustment window the pass puts
    /// <see cref="SameRunwayProtectionDisplacedCeilingKts"/> back, and only while the live ceiling is still this
    /// value — a ceiling something else has lowered since is left alone. Unlike <see cref="AutoSpacingReleased"/>
    /// this is not a latch — it toggles with the conflict. Snapshot-serialized so a restore mid-engagement still
    /// knows whose ceiling it is.
    /// </summary>
    public double? SameRunwayProtectionCeilingKts { get; set; }

    /// <summary>
    /// The ceiling <see cref="SameRunwayProtectionCeilingKts"/> was stamped over, or null when the aircraft carried
    /// none. A scenario-scripted arrival flies a STAR and can be carrying a published crossing-speed restriction,
    /// which it is required to comply with (§5-7-1.b NOTE) and which a controller may remove only with DELETE SPEED
    /// RESTRICTIONS (§5-7-2.e); <see cref="FlightPhysics"/> publishes one just once, on the tick the fix is
    /// sequenced, and never re-stamps it. Releasing the protection by nulling the ceiling outright would therefore
    /// delete that restriction permanently, so the displaced value is stashed here and restored instead. Only
    /// meaningful while <see cref="SameRunwayProtectionCeilingKts"/> is non-null. Snapshot-serialized.
    /// </summary>
    public double? SameRunwayProtectionDisplacedCeilingKts { get; set; }

    /// <summary>
    /// One-way latch set when the simulated tower has told this arrival to reduce to final approach speed under the
    /// same-runway protection (§5-7-3.f, inside
    /// <see cref="Simulation.SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/>). While set the pass re-stamps the
    /// Vapp ceiling every tick and does not release it at the §5-7-1.b.4 window or when the conflict clears — that
    /// paragraph forbids issuing a new adjustment inside 5 nm / the FAF, not keeping one already issued. Cleared only
    /// by <c>ReleaseSameRunwayProtection</c> (landing, go-around, or another speed authority taking the aircraft).
    /// Snapshot-serialized.
    /// </summary>
    public bool SameRunwayProtectionFasInstructed { get; set; }

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
            AutoSpacingReleased = AutoSpacingReleased,
            SameRunwayProtectionCeilingKts = SameRunwayProtectionCeilingKts,
            SameRunwayProtectionDisplacedCeilingKts = SameRunwayProtectionDisplacedCeilingKts,
            SameRunwayProtectionFasInstructed = SameRunwayProtectionFasInstructed,
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
            AutoSpacingReleased = dto.AutoSpacingReleased,
            SameRunwayProtectionCeilingKts = dto.SameRunwayProtectionCeilingKts,
            SameRunwayProtectionDisplacedCeilingKts = dto.SameRunwayProtectionDisplacedCeilingKts,
            SameRunwayProtectionFasInstructed = dto.SameRunwayProtectionFasInstructed,
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
