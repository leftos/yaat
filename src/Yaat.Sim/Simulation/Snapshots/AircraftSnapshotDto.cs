using Yaat.Sim.Phases;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Complete snapshot of a single aircraft's mutable state.
/// </summary>
public sealed class AircraftSnapshotDto
{
    // Identity
    public required string Callsign { get; init; }
    public required string AircraftType { get; init; }
    public string? ScenarioId { get; init; }
    public required string Cid { get; init; }

    /// <summary>
    /// Operational airport context (e.g. "OAK"). Non-required so older snapshots
    /// deserialize cleanly with the default empty string.
    /// </summary>
    public string AirportId { get; init; } = "";

    // Position & Physics
    public required LatLon Position { get; init; }
    public required double TrueHeadingDeg { get; init; }
    public required double TrueTrackDeg { get; init; }
    public required double Declination { get; init; }
    public required double Altitude { get; init; }
    public required double IndicatedAirspeed { get; init; }
    public required double VerticalSpeed { get; init; }
    public required double BankAngle { get; init; }
    public required double WindN { get; init; }
    public required double WindE { get; init; }

    public required AircraftFlightPlanDto FlightPlan { get; init; }
    public required AircraftGroundOpsDto Ground { get; init; }

    public required AircraftTransponderDto Transponder { get; init; }

    public required bool IsOnGround { get; init; }

    /// <summary>
    /// Cross-phase pilot-comms one-shot: set the first time the aircraft transmits anything
    /// in solo-training mode. Non-required so older snapshots (pre-M10.1.1) deserialize
    /// cleanly with the default <see langword="false"/>.
    /// </summary>
    public bool HasMadeInitialContact { get; init; }

    /// <summary>
    /// Controller callsign-use acknowledgement for VFR Class C entry. Non-required so older
    /// snapshots deserialize cleanly with the default <see langword="false"/>.
    /// </summary>
    public bool HasControllerAcknowledgedInitialContact { get; init; }

    /// <summary>
    /// Explicit VFR Class Bravo clearance gate. Non-required so older snapshots deserialize
    /// cleanly with the default <see langword="false"/>.
    /// </summary>
    public bool IsClearedIntoBravo { get; init; }

    /// <summary>
    /// Set after <c>LinedUpAndWaitingPhase</c>'s 10-second "ready" reminder has fired once.
    /// Non-required so older snapshots deserialize cleanly with the default
    /// <see langword="false"/>.
    /// </summary>
    public bool HasAnnouncedLinedUpReady { get; init; }

    /// <summary>
    /// Solo-training pilot-originated request awaiting a controller response. Non-required
    /// so older snapshots deserialize with no pending request.
    /// </summary>
    public PilotPendingRequestDto? PendingPilotRequest { get; init; }

    public required AircraftTrackDto Track { get; init; }
    public required AircraftStarsStateDto Stars { get; init; }

    // Domain sub-objects
    public required AircraftApproachStateDto Approach { get; init; }
    public required AircraftProcedureDto Procedure { get; init; }
    public required AircraftPatternDto Pattern { get; init; }
    public required AircraftVoiceDto Voice { get; init; }
    public required AircraftHoldAnnotationDto HoldAnnotation { get; init; }
    public required AircraftEramStateDto Eram { get; init; }
    public required AircraftClearanceDto Clearance { get; init; }
    public required AircraftGhostTrackDto Ghost { get; init; }

    // Position history
    public List<PositionDto>? PositionHistory { get; init; }

    // Approach score
    public ApproachScoreDto? ActiveApproachScore { get; init; }

    // Nested state
    public required ControlTargetsDto Targets { get; init; }
    public required CommandQueueDto Queue { get; init; }
    public PhaseListDto? Phases { get; init; }
    public List<DeferredDispatchDto>? DeferredDispatches { get; init; }
}

// --- Nested DTOs ---

public sealed class TrackOwnerDto
{
    public required string Callsign { get; init; }
    public string? FacilityId { get; init; }
    public int? Subset { get; init; }
    public string? SectorId { get; init; }
    public required int OwnerType { get; init; }
}

public sealed class TcpDto
{
    public required int Subset { get; init; }
    public required string SectorId { get; init; }
    public required string Id { get; init; }
    public string? ParentTcpId { get; init; }
}

public sealed class PointoutDto
{
    public required TcpDto Recipient { get; init; }
    public required TcpDto Sender { get; init; }
    public required int Status { get; init; }
}

public sealed class EramPointoutStateDto
{
    public required string OriginatingFacility { get; init; }
    public required string OriginatingSector { get; init; }
    public required string ReceivingFacility { get; init; }
    public required string ReceivingSector { get; init; }
    public required bool IsAcknowledged { get; init; }
    public required bool IsRecipientSuppressed { get; init; }
    public required bool IsRSideCleared { get; init; }
    public required bool IsDSideCleared { get; init; }
}

public sealed class SharedStateDto
{
    public required bool ForceFdb { get; init; }
    public required bool IsHighlighted { get; init; }
    public required int LeaderDirection { get; init; }
    public DateTime? IsQueriedUntil { get; init; }
    public required bool WasPreviouslyOwned { get; init; }
    public required int TpaType { get; init; }
    public required double TpaSize { get; init; }
}

public sealed class PositionDto
{
    public required double Lat { get; init; }
    public required double Lon { get; init; }
}

public sealed class PilotPendingRequestDto
{
    public required int Kind { get; init; }
    public required int ResponseState { get; init; }
    public required double FirstRequestedAtSeconds { get; init; }
    public required double LastRequestedAtSeconds { get; init; }
    public required double NextFollowUpDueSeconds { get; init; }
    public required string LastPilotLine { get; init; }
    public string? RunwayId { get; init; }
    public string? FacilityCallName { get; init; }
    public string? AirspaceClass { get; init; }
    public string? AirspaceIdent { get; init; }
    public LatLon? AirspaceReferencePosition { get; init; }
}

public sealed class ApproachScoreDto
{
    public required string Callsign { get; init; }
    public required string ApproachId { get; init; }
    public required string AirportCode { get; init; }
    public required string RunwayId { get; init; }
    public double? InterceptAngleDeg { get; init; }
    public double? InterceptDistanceNm { get; init; }
    public double? EstablishedDistanceNm { get; init; }
    public double? EstablishedAngleDeg { get; init; }
    public double? GlideSlopeDeviationAtThresholdDeg { get; init; }
    public double? LocalizerDeviationAtThresholdDeg { get; init; }
    public double? TouchdownDistanceFromThresholdFt { get; init; }
    public double? TouchdownCenterlineOffsetFt { get; init; }
    public required bool GoAround { get; init; }
    public string? GoAroundReason { get; init; }
}

public sealed class PendingApproachDto
{
    public required ApproachClearanceDto Clearance { get; init; }
    public required RunwayInfoDto AssignedRunway { get; init; }
}
