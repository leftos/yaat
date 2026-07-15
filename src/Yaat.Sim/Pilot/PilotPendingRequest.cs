using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Pilot;

public enum PilotPendingRequestKind
{
    Taxi,
    Takeoff,
    Landing,
    Approach,
    AirspaceEntry,
}

public enum PilotPendingRequestResponseState
{
    None,
    Standby,
    Satisfied,
    Denied,
    Superseded,
}

public sealed record PilotRequestContext(
    string? RunwayId,
    string? FacilityCallName,
    AirspaceClass? AirspaceClass,
    string? AirspaceIdent,
    LatLon? AirspaceReferencePosition
)
{
    public static PilotRequestContext None { get; } = new(null, null, null, null, null);

    public static PilotRequestContext Facility(string? facilityCallName) => new(null, facilityCallName, null, null, null);

    public static PilotRequestContext Runway(string? runwayId, string? facilityCallName) => new(runwayId, facilityCallName, null, null, null);
}

public sealed class PilotPendingRequest
{
    public required PilotPendingRequestKind Kind { get; init; }
    public PilotPendingRequestResponseState ResponseState { get; set; }
    public required double FirstRequestedAtSeconds { get; init; }
    public double LastRequestedAtSeconds { get; set; }
    public double NextFollowUpDueSeconds { get; set; }

    /// <summary>Terminal (SAY-column, callsign-free) form of the last pilot line, re-queued on follow-up.</summary>
    public required string LastPilotLine { get; set; }

    /// <summary>Spoken (TTS, callsign spelled) form of the last pilot line, re-queued on follow-up.</summary>
    public required string LastPilotLineTts { get; set; }
    public string? RunwayId { get; init; }
    public string? FacilityCallName { get; init; }
    public string? AirspaceClass { get; init; }
    public string? AirspaceIdent { get; init; }
    public LatLon? AirspaceReferencePosition { get; init; }

    public bool IsOpen => ResponseState is PilotPendingRequestResponseState.None or PilotPendingRequestResponseState.Standby;

    public PilotPendingRequestDto ToSnapshot() =>
        new()
        {
            Kind = (int)Kind,
            ResponseState = (int)ResponseState,
            FirstRequestedAtSeconds = FirstRequestedAtSeconds,
            LastRequestedAtSeconds = LastRequestedAtSeconds,
            NextFollowUpDueSeconds = NextFollowUpDueSeconds,
            LastPilotLine = LastPilotLine,
            LastPilotLineTts = LastPilotLineTts,
            RunwayId = RunwayId,
            FacilityCallName = FacilityCallName,
            AirspaceClass = AirspaceClass,
            AirspaceIdent = AirspaceIdent,
            AirspaceReferencePosition = AirspaceReferencePosition,
        };

    public static PilotPendingRequest FromSnapshot(PilotPendingRequestDto dto) =>
        new()
        {
            Kind = (PilotPendingRequestKind)dto.Kind,
            ResponseState = (PilotPendingRequestResponseState)dto.ResponseState,
            FirstRequestedAtSeconds = dto.FirstRequestedAtSeconds,
            LastRequestedAtSeconds = dto.LastRequestedAtSeconds,
            NextFollowUpDueSeconds = dto.NextFollowUpDueSeconds,
            LastPilotLine = dto.LastPilotLine,
            // Pre-#297 snapshots stored only one string (the spoken form) in LastPilotLine and lack
            // LastPilotLineTts; fall back to it so restored follow-ups reproduce the recorded behavior.
            LastPilotLineTts = dto.LastPilotLineTts ?? dto.LastPilotLine,
            RunwayId = dto.RunwayId,
            FacilityCallName = dto.FacilityCallName,
            AirspaceClass = dto.AirspaceClass,
            AirspaceIdent = dto.AirspaceIdent,
            AirspaceReferencePosition = dto.AirspaceReferencePosition,
        };
}
