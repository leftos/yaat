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

    /// <summary>A departure's request to a delivery student for its IFR clearance or VFR departure instructions.</summary>
    Clearance,

    /// <summary>A runway spawn at an untowered field asking the radar student for its departure release.</summary>
    Release,

    /// <summary>
    /// A jet or turboprop parked on a push-back stand asking ground for a push; its after-push call is the taxi request.
    /// </summary>
    Pushback,

    /// <summary>
    /// An IFR arrival's request to the radar student for lower, once it is far enough above its descent profile (field
    /// elevation plus 300 ft per NM of the route left, a published STAR altitude ahead as a lower line): 1,000 ft above it
    /// at first, 2,000 ft above it after a descent clearance.
    /// </summary>
    Descent,

    /// <summary>
    /// An IFR aircraft at or approaching its destination with no clearance beyond it, asking the radar student for further
    /// clearance.
    /// </summary>
    FurtherClearance,
}

/// <summary>
/// What an aircraft remembers of its last capped request for lower (<see cref="AircraftState.DescentRequestCap"/>), kept
/// when another request replaces it and cleared by a descent clearance.
/// </summary>
public enum DescentRequestCap
{
    /// <summary>No capped request for lower since the last descent clearance.</summary>
    None,

    /// <summary>Capped after it was asked from the 1,000 ft band: it re-opens once, from the 2,000 ft band.</summary>
    Reopenable,

    /// <summary>Capped after it was asked from the 2,000 ft band, or its one re-open is spent: the pilot asks nothing more.</summary>
    Spent,
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
    LatLon? AirspaceReferencePosition,
    string? ParkingName
)
{
    public static PilotRequestContext None { get; } = new(null, null, null, null, null, null);

    public static PilotRequestContext Facility(string? facilityCallName) => new(null, facilityCallName, null, null, null, null);

    public static PilotRequestContext Runway(string? runwayId, string? facilityCallName) => new(runwayId, facilityCallName, null, null, null, null);

    /// <summary>A request to enter controlled airspace: the airspace's class, its identifier and its reference position.</summary>
    public static PilotRequestContext Airspace(AirspaceClass airspaceClass, string airspaceIdent, LatLon referencePosition) =>
        new(null, null, airspaceClass, airspaceIdent, referencePosition, null);

    /// <summary>
    /// Whether the request is a taxi-in request (to parking), named spot or not, rather than a departure's ready-to-taxi
    /// call.
    /// </summary>
    public bool IsTaxiIn { get; init; }

    /// <summary>
    /// An arrival's taxi-in request: the parking the pilot asked to taxi to, or null when it asked for parking without
    /// naming a spot.
    /// </summary>
    public static PilotRequestContext TaxiIn(string? facilityCallName, string? parkingName) =>
        new(null, facilityCallName, null, null, null, parkingName) { IsTaxiIn = true };
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

    /// <summary>
    /// The parking an arrival asked to taxi to (a taxi-in request); null for a departure's ready-to-taxi call, and for a
    /// taxi-in request that named no spot (<see cref="TaxiIn"/>).
    /// </summary>
    public string? ParkingName { get; init; }

    /// <summary>A taxi-in request (to parking), whether or not it named a spot; false for a departure's ready-to-taxi call.</summary>
    public bool TaxiIn { get; init; }

    /// <summary>
    /// The pilot's last asking was a re-ask after the controller answered "standby": the wait was acknowledged, so the
    /// request is not yet one the controller has ignored. Cleared by a re-ask that followed no answer.
    /// </summary>
    public bool ReaskedAfterStandby { get; set; }

    /// <summary>
    /// The re-asks voiced since the request was recorded. For a request for lower or an approach request
    /// <see cref="PilotRequestTracker"/> voices at most <see cref="PilotRequestTracker.MaxFollowUps"/>, then leaves it
    /// open and unvoiced (<see cref="IsCapped"/>); other kinds re-ask without a cap.
    /// </summary>
    public int FollowUpsVoiced { get; set; }

    /// <summary>
    /// A request for lower re-opened as a fresh request after an earlier one, asked in the 1,000 ft band, used up its
    /// re-asks unanswered and the aircraft moved into the 2,000 ft band (<see cref="PilotProactive.TickDescentRequest"/>).
    /// It is not re-opened again: after its own re-asks the pilot waits for the controller.
    /// </summary>
    public bool ReopenedAfterCap { get; set; }

    /// <summary>
    /// A request for lower asked in the 2,000 ft band: the pilot crossed the step-down threshold (2,000 ft above the
    /// profile, after a descent clearance or on a re-open) rather than the first request's 1,000 ft one. False for the
    /// 1,000 ft band. <see cref="PilotProactive.TickDescentRequest"/> re-opens only a capped request asked in the
    /// 1,000 ft band.
    /// </summary>
    public bool AskedInStepDownBand { get; set; }

    public bool IsOpen => ResponseState is PilotPendingRequestResponseState.None or PilotPendingRequestResponseState.Standby;

    /// <summary>
    /// An open request for lower or approach request that has voiced its <see cref="PilotRequestTracker.MaxFollowUps"/>
    /// re-asks unanswered: it stays open and silent. Every other kind is never capped and keeps re-asking.
    /// </summary>
    public bool IsCapped =>
        IsOpen
        && (Kind is PilotPendingRequestKind.Descent or PilotPendingRequestKind.Approach)
        && (FollowUpsVoiced >= PilotRequestTracker.MaxFollowUps);

    /// <summary>Whether the request holds the pilot's other calls back: open and not <see cref="IsCapped"/>.</summary>
    public bool BlocksOtherCalls => IsOpen && !IsCapped;

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
            ParkingName = ParkingName,
            TaxiIn = TaxiIn,
            ReaskedAfterStandby = ReaskedAfterStandby,
            FollowUpsVoiced = FollowUpsVoiced,
            ReopenedAfterCap = ReopenedAfterCap,
            AskedInStepDownBand = AskedInStepDownBand,
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
            ParkingName = dto.ParkingName,
            // Snapshots from before the flag marked a taxi-in request only by its parking name.
            TaxiIn = dto.TaxiIn || (dto.ParkingName is not null),
            ReaskedAfterStandby = dto.ReaskedAfterStandby,
            FollowUpsVoiced = dto.FollowUpsVoiced,
            ReopenedAfterCap = dto.ReopenedAfterCap,
            // Snapshots from before the field restore the 1,000 ft band.
            AskedInStepDownBand = dto.AskedInStepDownBand,
        };
}
