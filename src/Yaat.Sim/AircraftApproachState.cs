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

    public AircraftApproachStateDto ToSnapshot() =>
        new()
        {
            Expected = Expected,
            PendingClearance = PendingClearance?.ToSnapshot(),
            HasReportedFieldInSight = HasReportedFieldInSight,
            HasReportedTrafficInSight = HasReportedTrafficInSight,
            LastReportedTrafficCallsign = LastReportedTrafficCallsign,
            FollowingCallsign = FollowingCallsign,
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
        };
}
