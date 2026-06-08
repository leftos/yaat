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
    /// </summary>
    public string? LastReportedTrafficCallsign { get; set; }

    public string? FollowingCallsign { get; set; }

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

    public AircraftApproachStateDto ToSnapshot() =>
        new()
        {
            Expected = Expected,
            PendingClearance = PendingClearance?.ToSnapshot(),
            HasReportedFieldInSight = HasReportedFieldInSight,
            HasReportedTrafficInSight = HasReportedTrafficInSight,
            LastReportedTrafficCallsign = LastReportedTrafficCallsign,
            FollowingCallsign = FollowingCallsign,
            FollowBestGapNm = FollowBestGapNm,
            FollowRunawaySeconds = FollowRunawaySeconds,
            AutoSpacingReleased = AutoSpacingReleased,
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
            FollowBestGapNm = dto.FollowBestGapNm,
            FollowRunawaySeconds = dto.FollowRunawaySeconds,
            AutoSpacingReleased = dto.AutoSpacingReleased,
        };
}
