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

    // Position & Physics
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required double TrueHeadingDeg { get; init; }
    public required double TrueTrackDeg { get; init; }
    public required double Declination { get; init; }
    public required double Altitude { get; init; }
    public required double IndicatedAirspeed { get; init; }
    public required double VerticalSpeed { get; init; }
    public required double BankAngle { get; init; }
    public required double WindN { get; init; }
    public required double WindE { get; init; }

    // Flight plan
    public required bool HasFlightPlan { get; init; }
    public required string Departure { get; init; }
    public required string Destination { get; init; }
    public required string Route { get; init; }
    public required string Remarks { get; init; }
    public required string EquipmentSuffix { get; init; }
    public required string FlightRules { get; init; }
    public required int CruiseAltitude { get; init; }
    public required int CruiseSpeed { get; init; }
    public required string TransponderMode { get; init; }

    // Beacon
    public required uint AssignedBeaconCode { get; init; }
    public required uint BeaconCode { get; init; }
    public required bool IsIdenting { get; init; }
    public double? IdentStartedAt { get; init; }

    // Ground state
    public required bool IsOnGround { get; init; }
    public string? ParkingSpot { get; init; }
    public string? CurrentTaxiway { get; init; }
    public required bool IsHeld { get; init; }
    public string? GiveWayTarget { get; init; }
    public required bool AutoDeleteExempt { get; init; }
    public required double ConflictBreakRemainingSeconds { get; init; }
    public double? GroundSpeedLimit { get; init; }
    public double? PushbackTrueHeadingDeg { get; init; }

    // Track operations
    public TrackOwnerDto? Owner { get; init; }
    public TrackOwnerDto? HandoffPeer { get; init; }
    public TrackOwnerDto? HandoffRedirectedBy { get; init; }
    public PointoutDto? Pointout { get; init; }
    public string? Scratchpad1 { get; init; }
    public required bool WasScratchpad1Cleared { get; init; }
    public string? PreviousScratchpad1 { get; init; }
    public string? Scratchpad2 { get; init; }
    public string? PreviousScratchpad2 { get; init; }
    public string? AsdexScratchpad1 { get; init; }
    public string? AsdexScratchpad2 { get; init; }
    public int? TemporaryAltitude { get; init; }
    public int? PilotReportedAltitude { get; init; }
    public required bool IsAnnotated { get; init; }
    public required bool OnHandoff { get; init; }
    public required bool HandoffAccepted { get; init; }
    public double? HandoffInitiatedAt { get; init; }
    public int? AssignedAltitude { get; init; }

    // Approach / procedure
    public string? ExpectedApproach { get; init; }
    public PendingApproachDto? PendingApproachClearance { get; init; }
    public string? ActiveSidId { get; init; }
    public string? ActiveStarId { get; init; }
    public string? DepartureRunway { get; init; }
    public string? DestinationRunway { get; init; }
    public required bool SidViaMode { get; init; }
    public required bool StarViaMode { get; init; }
    public int? SidViaCeiling { get; init; }
    public int? StarViaFloor { get; init; }
    public required bool SpeedRestrictionsDeleted { get; init; }
    public required bool IsExpediting { get; init; }
    public double? PatternSizeOverrideNm { get; init; }

    // Visual approach
    public required bool HasReportedFieldInSight { get; init; }
    public required bool HasReportedTrafficInSight { get; init; }
    public string? FollowingCallsign { get; init; }

    // CRC display
    public required int VoiceType { get; init; }
    public required bool TdlsDumped { get; init; }

    // Hold annotations
    public string? HoldAnnotationFix { get; init; }
    public required int HoldAnnotationDirection { get; init; }
    public required int HoldAnnotationTurns { get; init; }
    public int? HoldAnnotationLegLength { get; init; }
    public required bool HoldAnnotationLegLengthInNm { get; init; }
    public required int HoldAnnotationEfc { get; init; }

    // Clearance (departure from CRC)
    public string? ClearanceExpect { get; init; }
    public string? ClearanceSid { get; init; }
    public string? ClearanceTransition { get; init; }
    public string? ClearanceClimbout { get; init; }
    public string? ClearanceClimbvia { get; init; }
    public string? ClearanceInitialAlt { get; init; }
    public string? ClearanceContactInfo { get; init; }
    public string? ClearanceLocalInfo { get; init; }
    public string? ClearanceDepFreq { get; init; }

    // Unsupported (ghost) tracks
    public required bool IsUnsupported { get; init; }
    public double? UnsupportedLatitude { get; init; }
    public double? UnsupportedLongitude { get; init; }

    // Conflict alert / display inhibitions
    public required bool IsCaInhibited { get; init; }
    public required bool IsModeCInhibited { get; init; }
    public required bool IsMsawInhibited { get; init; }
    public required bool IsDuplicateBeaconInhibited { get; init; }
    public int? TpaType { get; init; }
    public int? GlobalLeaderDirection { get; init; }
    public List<TcpDto>? ForcedPointoutsTo { get; init; }
    public Dictionary<string, SharedStateDto>? SharedState { get; init; }

    // Position history
    public List<PositionDto>? PositionHistory { get; init; }

    // Approach score
    public ApproachScoreDto? ActiveApproachScore { get; init; }

    // Nested state
    public required ControlTargetsDto Targets { get; init; }
    public required CommandQueueDto Queue { get; init; }
    public PhaseListDto? Phases { get; init; }
    public List<DeferredDispatchDto>? DeferredDispatches { get; init; }
    public TaxiRouteDto? AssignedTaxiRoute { get; init; }
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
