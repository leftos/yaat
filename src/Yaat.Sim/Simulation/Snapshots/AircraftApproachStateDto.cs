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
}
