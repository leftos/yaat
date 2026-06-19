namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftApproachStateDto
{
    public string? Expected { get; init; }
    public PendingApproachDto? PendingClearance { get; init; }
    public required bool HasReportedFieldInSight { get; init; }
    public required bool HasReportedTrafficInSight { get; init; }
    public string? LastReportedTrafficCallsign { get; init; }
    public string? FollowingCallsign { get; init; }
    public double? FollowBestGapNm { get; init; }
    public double FollowRunawaySeconds { get; init; }

    /// <summary>
    /// In-trail auto-spacing released latch. Non-required so older snapshots default to
    /// <see langword="false"/>.
    /// </summary>
    public bool AutoSpacingReleased { get; init; }

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
