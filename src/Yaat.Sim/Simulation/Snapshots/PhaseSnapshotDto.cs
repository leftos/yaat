using System.Text.Json.Serialization;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Simulation.Snapshots;

// --- Phase List ---

public sealed class PhaseListDto
{
    public RunwayInfoDto? AssignedRunway { get; init; }
    public TaxiRouteDto? TaxiRoute { get; init; }
    public DepartureClearanceDto? DepartureClearance { get; init; }
    public int? LandingClearance { get; init; }
    public string? ClearedRunwayId { get; init; }
    public int? TrafficDirection { get; init; }
    public RunwayInfoDto? PatternRunway { get; init; }
    public RunwayInfoDto? DepartureRunway { get; init; }
    public int? RequestedExit { get; init; }
    public ApproachClearanceDto? ActiveApproach { get; init; }
    public LahsoTargetDto? LahsoHoldShort { get; init; }
    public required int CurrentIndex { get; init; }
    public required List<PhaseDto> Phases { get; init; }
}

public sealed class RunwayInfoDto
{
    public required string AirportId { get; init; }
    public required string End1 { get; init; }
    public required string End2 { get; init; }
    public required string Designator { get; init; }
    public required double Lat1 { get; init; }
    public required double Lon1 { get; init; }
    public required double Elevation1Ft { get; init; }
    public required double TrueHeading1Deg { get; init; }
    public required double Lat2 { get; init; }
    public required double Lon2 { get; init; }
    public required double Elevation2Ft { get; init; }
    public required double TrueHeading2Deg { get; init; }
    public required double LengthFt { get; init; }
    public required double WidthFt { get; init; }
}

public sealed class DepartureClearanceDto
{
    public required int Type { get; init; }
    public required DepartureInstructionDto Departure { get; init; }
    public int? AssignedAltitude { get; init; }
    public List<NavigationTargetDto>? DepartureRoute { get; init; }
    public string? DepartureSidId { get; init; }
    public double? SidDepartureHeadingMagnetic { get; init; }
    public bool RvSidDeferHeadingUntilMinAlt { get; init; }
    public bool RvSidHoldRunwayHeading { get; init; }
    public RunwayInfoDto? PatternRunway { get; init; }
    public List<int>? PreClearedHoldShortNodeIds { get; init; }
}

public sealed class ApproachClearanceDto
{
    public required string ApproachId { get; init; }
    public required string AirportCode { get; init; }
    public required string RunwayId { get; init; }
    public required double FinalApproachCourseDeg { get; init; }

    /// <summary>
    /// Optional lateral anchor (lat/lon) for parallel-offset approaches whose published MAP
    /// is offset from the runway threshold. Null for ordinary approaches; pre-FAC-extractor
    /// snapshots also have these as null and round-trip cleanly.
    /// </summary>
    public double? FinalApproachAnchorLat { get; init; }

    public double? FinalApproachAnchorLon { get; init; }

    public required bool StraightIn { get; init; }
    public required bool Force { get; init; }

    /// <summary>
    /// True when only a lateral intercept is authorized (JFAC/JLOC) and the aircraft holds
    /// altitude until cleared for the approach (CAPP). Defaults false; pre-feature snapshots
    /// omit it and round-trip to legacy descend-on-intercept behavior.
    /// </summary>
    public bool LateralInterceptOnly { get; init; }

    public int? MapAltitudeFt { get; init; }
    public double? MapDistanceNm { get; init; }
    public double? InterceptCaptureDistanceNm { get; init; }
    public double? InterceptCaptureAngleDeg { get; init; }

    /// <summary>True when a PTACF forced intercept captured the localizer (the glideslope-
    /// established gate is bypassed for it). Optional (defaults false) so older snapshots and
    /// relaxed JFAC/JLOC joins round-trip to the gated behavior.</summary>
    public bool ForcedInterceptCapture { get; init; }

    public MissedApproachHoldDto? MapHold { get; init; }
    public List<ApproachFixDto>? MissedApproachFixes { get; init; }
}

public sealed class MissedApproachHoldDto
{
    public required string FixName { get; init; }
    public required double FixLat { get; init; }
    public required double FixLon { get; init; }
    public required int InboundCourse { get; init; }
    public required double LegLength { get; init; }
    public required bool IsMinuteBased { get; init; }
    public required int Direction { get; init; }
}

public sealed class ApproachFixDto
{
    public required string Name { get; init; }
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public AltitudeRestrictionDto? AltitudeRestriction { get; init; }
    public SpeedRestrictionDto? SpeedRestriction { get; init; }
    public required bool IsFlyOver { get; init; }
    public required bool IsFaf { get; init; }
    public required int LegType { get; init; }
    public double? ArcCenterLat { get; init; }
    public double? ArcCenterLon { get; init; }
    public double? ArcRadiusNm { get; init; }
    public required bool IsArc { get; init; }
}

public sealed class LahsoTargetDto
{
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public required double DistFromThresholdNm { get; init; }
    public required string CrossingRunwayId { get; init; }
}

// --- Departure instruction (polymorphic) ---

[JsonDerivedType(typeof(DefaultDepartureDto), "Default")]
[JsonDerivedType(typeof(RunwayHeadingDepartureDto), "RunwayHeading")]
[JsonDerivedType(typeof(RelativeTurnDepartureDto), "RelativeTurn")]
[JsonDerivedType(typeof(FlyHeadingDepartureDto), "FlyHeading")]
[JsonDerivedType(typeof(OnCourseDepartureDto), "OnCourse")]
[JsonDerivedType(typeof(DirectFixDepartureDto), "DirectFix")]
[JsonDerivedType(typeof(PresentPositionHoverDepartureDto), "PresentPositionHover")]
public abstract class DepartureInstructionDto;

public sealed class DefaultDepartureDto : DepartureInstructionDto;

public sealed class RunwayHeadingDepartureDto : DepartureInstructionDto;

public sealed class RelativeTurnDepartureDto : DepartureInstructionDto
{
    public required int Degrees { get; init; }
    public required int Direction { get; init; }
}

public sealed class FlyHeadingDepartureDto : DepartureInstructionDto
{
    public required double MagneticHeadingDeg { get; init; }
    public int? Direction { get; init; }
}

public sealed class OnCourseDepartureDto : DepartureInstructionDto;

public sealed class DirectFixDepartureDto : DepartureInstructionDto
{
    public required string FixName { get; init; }
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public int? Direction { get; init; }
}

public sealed class PresentPositionHoverDepartureDto : DepartureInstructionDto
{
    public required int HoverAltitudeAglFt { get; init; }
}

// --- Pattern waypoints ---

public sealed class PatternWaypointsDto
{
    public required double DepartureEndLat { get; init; }
    public required double DepartureEndLon { get; init; }
    public required double CrosswindTurnLat { get; init; }
    public required double CrosswindTurnLon { get; init; }
    public required double DownwindStartLat { get; init; }
    public required double DownwindStartLon { get; init; }
    public required double DownwindAbeamLat { get; init; }
    public required double DownwindAbeamLon { get; init; }
    public required double BaseTurnLat { get; init; }
    public required double BaseTurnLon { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double UpwindHeadingDeg { get; init; }
    public required double CrosswindHeadingDeg { get; init; }
    public required double DownwindHeadingDeg { get; init; }
    public required double BaseHeadingDeg { get; init; }
    public required double FinalHeadingDeg { get; init; }

    /// <summary>Pattern altitude MSL (feet). Optional for backward compat
    /// with snapshots predating this field — restore infers 0 on miss.</summary>
    public double? PatternAltitudeFt { get; init; }

    /// <summary>0=Left, 1=Right. Optional for backward compat — when null,
    /// FromSnapshot infers from the abeam position relative to the threshold
    /// along the landing heading.</summary>
    public int? Direction { get; init; }
}

// --- Phase (polymorphic) ---

[JsonDerivedType(typeof(HoldingShortPhaseDto), "HoldingShort")]
[JsonDerivedType(typeof(CrossingRunwayPhaseDto), "CrossingRunway")]
[JsonDerivedType(typeof(ClearRunwayPhaseDto), "ClearRunway")]
[JsonDerivedType(typeof(AirTaxiPhaseDto), "AirTaxi")]
[JsonDerivedType(typeof(HoldingInPositionPhaseDto), "HoldingInPosition")]
[JsonDerivedType(typeof(HoldingAfterPushbackPhaseDto), "HoldingAfterPushback")]
[JsonDerivedType(typeof(HoldingAfterExitPhaseDto), "HoldingAfterExit")]
[JsonDerivedType(typeof(AtParkingPhaseDto), "AtParking")]
[JsonDerivedType(typeof(TaxiingPhaseDto), "Taxiing")]
[JsonDerivedType(typeof(FollowingPhaseDto), "Following")]
[JsonDerivedType(typeof(PushbackPhaseDto), "Pushback")]
[JsonDerivedType(typeof(PushbackToSpotPhaseDto), "PushbackToSpot")]
[JsonDerivedType(typeof(RunwayExitPhaseDto), "RunwayExit")]
[JsonDerivedType(typeof(HelicopterLandingPhaseDto), "HelicopterLanding")]
[JsonDerivedType(typeof(GoAroundPhaseDto), "GoAround")]
[JsonDerivedType(typeof(HelicopterTakeoffPhaseDto), "HelicopterTakeoff")]
[JsonDerivedType(typeof(LowApproachPhaseDto), "LowApproach")]
[JsonDerivedType(typeof(RunwayHoldingPhaseDto), "RunwayHolding")]
[JsonDerivedType(typeof(MakeTurnPhaseDto), "MakeTurn")]
[JsonDerivedType(typeof(VfrHoldPhaseDto), "VfrHold")]
[JsonDerivedType(typeof(AirspaceBoundaryHoldPhaseDto), "AirspaceBoundaryHold")]
[JsonDerivedType(typeof(STurnPhaseDto), "STurn")]
[JsonDerivedType(typeof(StopAndGoPhaseDto), "StopAndGo")]
[JsonDerivedType(typeof(TouchAndGoPhaseDto), "TouchAndGo")]
[JsonDerivedType(typeof(TakeoffPhaseDto), "Takeoff")]
[JsonDerivedType(typeof(InitialClimbPhaseDto), "InitialClimb")]
[JsonDerivedType(typeof(LineUpPhaseDto), "LineUp")]
[JsonDerivedType(typeof(LinedUpAndWaitingPhaseDto), "LinedUpAndWaiting")]
[JsonDerivedType(typeof(FinalApproachPhaseDto), "FinalApproach")]
[JsonDerivedType(typeof(LandingPhaseDto), "Landing")]
[JsonDerivedType(typeof(MidfieldCrossingPhaseDto), "MidfieldCrossing")]
[JsonDerivedType(typeof(TeardropReentryPhaseDto), "TeardropReentry")]
[JsonDerivedType(typeof(PatternEntryPhaseDto), "PatternEntry")]
[JsonDerivedType(typeof(BasePhaseDto), "Base")]
[JsonDerivedType(typeof(CrosswindPhaseDto), "Crosswind")]
[JsonDerivedType(typeof(DownwindPhaseDto), "Downwind")]
[JsonDerivedType(typeof(UpwindPhaseDto), "Upwind")]
[JsonDerivedType(typeof(VfrFollowPhaseDto), "VfrFollow")]
[JsonDerivedType(typeof(HoldingPatternPhaseDto), "HoldingPattern")]
[JsonDerivedType(typeof(ProcedureTurnPhaseDto), "ProcedureTurn")]
[JsonDerivedType(typeof(ApproachNavigationPhaseDto), "ApproachNavigation")]
[JsonDerivedType(typeof(InterceptCoursePhaseDto), "InterceptCourse")]
[JsonDerivedType(typeof(DepartureProcedurePhaseDto), "DepartureProcedure")]
public abstract class PhaseDto
{
    public required int Status { get; init; }
    public required double ElapsedSeconds { get; init; }
    public List<ClearanceRequirementDto>? Requirements { get; init; }
}

public sealed class ClearanceRequirementDto
{
    public required int Type { get; init; }
    public required bool IsSatisfied { get; init; }
}

// --- Ground phases ---

public sealed class HoldingShortPhaseDto : PhaseDto
{
    public required int HoldShortNodeId { get; init; }
    public required string RunwayId { get; init; }

    /// <summary>
    /// Why the aircraft holds short (destination runway / runway crossing / explicit HSC).
    /// Null on legacy snapshots (schema &lt; 12); restore then falls back to RunwayCrossing,
    /// the value those snapshots were reconstructed with before this field existed.
    /// </summary>
    public HoldShortReason? Reason { get; init; }

    /// <summary>
    /// Set after this phase instance fired its solo-training pilot check-in. Per-phase-instance
    /// — fresh phase instances default to false, so re-entering the phase at a different
    /// hold-short re-fires the announcement. Non-required so older snapshots default to false.
    /// </summary>
    public bool HasAnnouncedReady { get; init; }
}

public sealed class CrossingRunwayPhaseDto : PhaseDto
{
    public required int ApproachNodeId { get; init; }
    public required int TargetNodeId { get; init; }
    public required bool Initialized { get; init; }
    public required double TimeSinceLastLog { get; init; }

    /// <summary>
    /// Runway being crossed (sourced from the preceding HoldShortPoint.TargetName).
    /// Non-required so older snapshots default to null; the client status text falls
    /// back to AssignedRunway in that case (legacy display behaviour).
    /// </summary>
    public string? CrossingRunwayId { get; init; }

    /// <summary>
    /// Navigator state for the slice between approach and target. Non-required so
    /// older snapshots default to null; <see cref="CrossingRunwayPhase.FromSnapshot"/>
    /// rebuilds the navigator from <see cref="AircraftGroundState.AssignedTaxiRoute"/>
    /// on the first OnTick after restore. Carrying the navigator state forward is a
    /// future optimization — the rebuilt slice is canonical today.
    /// </summary>
    public GroundNavigatorDto? Navigator { get; init; }

    /// <summary>
    /// Index into the rebuilt crossing slice; forward-compat placeholder for the same
    /// reason as <see cref="Navigator"/>. Defaults to 0 for legacy snapshots.
    /// </summary>
    public int CrossingRouteSegmentIndex { get; init; }
}

/// <summary>
/// Snapshot for <see cref="Yaat.Sim.Phases.Ground.ClearRunwayPhase"/> (issue #172 W5). Carries the runway
/// hold-short node and the approach (runway-side) node; the navigator is rebuilt on the first OnTick after
/// restore, like <see cref="CrossingRunwayPhaseDto"/>.
/// </summary>
public sealed class ClearRunwayPhaseDto : PhaseDto
{
    public required int RunwayNodeId { get; init; }
    public required int ApproachNodeId { get; init; }
}

public sealed class AirTaxiPhaseDto : PhaseDto
{
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public string? DestinationName { get; init; }
    public required double TargetAltitude { get; init; }
    public required bool LiftingOff { get; init; }
    public required bool Descending { get; init; }
    public required double TimeSinceLastLog { get; init; }
}

public sealed class HoldingInPositionPhaseDto : PhaseDto;

public sealed class HoldingAfterPushbackPhaseDto : PhaseDto;

public sealed class HoldingAfterExitPhaseDto : PhaseDto
{
    public string? RunwayId { get; init; }
    public string? ExitTaxiway { get; init; }
    public int? HoldShortNodeId { get; init; }
}

public sealed class AtParkingPhaseDto : PhaseDto;

public sealed class TaxiingPhaseDto : PhaseDto
{
    public required int TargetNodeId { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required bool Initialized { get; init; }
    public required double TimeSinceLastLog { get; init; }
    public required double PrevDistToTarget { get; init; }
    public GroundNavigatorDto? Navigator { get; init; }
}

public sealed class GroundNavigatorDto
{
    public int TargetNodeId { get; init; }
    public double TargetLat { get; init; }
    public double TargetLon { get; init; }
    public double SegmentFromLat { get; init; }
    public double SegmentFromLon { get; init; }
    public double PrevDistToTarget { get; init; }
    public double CurrentNodeRequiredSpeed { get; init; }
    public double MaxSpeedKts { get; init; }
    public double? DecelRateKts { get; init; }
    public double? NextSegmentBearing { get; init; }
    public int TicksNearTarget { get; init; }
}

public sealed class FollowingPhaseDto : PhaseDto
{
    public required string TargetCallsign { get; init; }
    public required double TimeSinceLastLog { get; init; }
}

public sealed class PushbackPhaseDto : PhaseDto
{
    public int? TargetHeading { get; init; }
    public double? TargetLatitude { get; init; }
    public double? TargetLongitude { get; init; }
    public required double StartLat { get; init; }
    public required double StartLon { get; init; }
    public required double TotalDistToTarget { get; init; }
    public required bool ReachedTarget { get; init; }
    public required bool IsAligned { get; init; }
    public required double TimeSinceLastLog { get; init; }
}

public sealed class PushbackToSpotPhaseDto : PhaseDto
{
    public required TaxiRouteDto Route { get; init; }
    public int? TargetHeading { get; init; }
    public required int TargetNodeId { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required bool Initialized { get; init; }
    public required bool ReachedFinalNode { get; init; }
    public required bool Pivoting { get; init; }
    public required double PivotTargetHeadingDeg { get; init; }
    public required double TimeSinceLastLog { get; init; }
}

public sealed class RunwayExitPhaseDto : PhaseDto
{
    public int? ExitNodeId { get; init; }
    public required bool ReachedExitNode { get; init; }
    public string? ExitTaxiway { get; init; }
    public string? RunwayId { get; init; }
    public int? LastResolvedPreference { get; init; }
    public string? LastResolvedPreferenceTaxiway { get; init; }
    public List<int>? ExitWaypointNodeIds { get; init; }
    public int ExitWaypointIndex { get; init; }
    public required double ExitSpeed { get; init; }
    public required double TimeSinceLastLog { get; init; }
    public required double RunwayHeadingDeg { get; init; } = 0.0;
    public required int ExitStateValue { get; init; } = 0;
    public GroundNavigatorDto? Navigator { get; init; }
}

// --- Tower phases ---

public sealed class HelicopterLandingPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required bool TouchedDown { get; init; }
}

public sealed class GoAroundPhaseDto : PhaseDto
{
    public double? AssignedMagneticHeadingDeg { get; init; }
    public int? TargetAltitude { get; init; }
    public required bool ReenterPattern { get; init; }
    public required double FieldElevation { get; init; }
    public required double RunwayTrueHeadingDeg { get; init; }
    public required bool HeadingAssigned { get; init; }

    // True when the pre-go-around terminating phase was a full-stop landing.
    // Drives the next auto-cycled circuit's terminator: false → TouchAndGoPhase, true → LandingPhase.
    // Default false on absent field keeps replays of older snapshots on the pre-fix code path.
    public bool NextLandingFullStop { get; init; }
}

public sealed class HelicopterTakeoffPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public DepartureInstructionDto? Departure { get; init; }
    public double? CompletionAgl { get; init; }
}

public sealed class LowApproachPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double GoAroundAgl { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required bool ClimbingOut { get; init; }
}

public sealed class RunwayHoldingPhaseDto : PhaseDto
{
    public required string CrossingRunwayId { get; init; }
}

public sealed class MakeTurnPhaseDto : PhaseDto
{
    public required int Direction { get; init; }
    public required double TargetDegrees { get; init; }
    public required double StartHeadingDeg { get; init; }
    public required double CumulativeTurn { get; init; }
    public required double LastHeadingDeg { get; init; }
    public required bool Exiting { get; init; }
    public double? PriorTargetSpeed { get; init; }
    public bool PriorHasExplicitSpeed { get; init; }
    public bool SpeedReduced { get; init; }
}

public sealed class VfrHoldPhaseDto : PhaseDto
{
    public string? FixName { get; init; }
    public double? FixLat { get; init; }
    public double? FixLon { get; init; }
    public int? OrbitDirection { get; init; }
    public required bool AtFix { get; init; }
    public required double CumulativeTurn { get; init; }
    public required double LastHeadingDeg { get; init; }
    public double? PriorTargetSpeed { get; init; }
    public bool PriorHasExplicitSpeed { get; init; }
    public bool SpeedReduced { get; init; }
}

public sealed class AirspaceBoundaryHoldPhaseDto : PhaseDto
{
    public required int AirspaceClass { get; init; }
    public required string Ident { get; init; }
    public required string NameText { get; init; }
    public required double ReferenceLat { get; init; }
    public required double ReferenceLon { get; init; }
    public required int OrbitDirection { get; init; }
    public int? VolumeLowerFtMsl { get; init; }
    public int? VolumeUpperFtMsl { get; init; }
    public List<NavigationTargetDto>? OriginalRoute { get; init; }
    public double? OriginalTargetHeadingDeg { get; init; }
    public int? OriginalTurnDirection { get; init; }
    public double? OriginalTargetSpeed { get; init; }
    public required double CumulativeTurn { get; init; }
    public required double LastHeadingDeg { get; init; }
    public required bool Started { get; init; }
}

public sealed class STurnPhaseDto : PhaseDto
{
    public required int InitialDirection { get; init; }
    public required int Count { get; init; }
    public required double FinalHeadingDeg { get; init; }
    public required int TurnsCompleted { get; init; }
    public required bool TurningToFinal { get; init; }
    public double? PriorTargetSpeed { get; init; }
    public bool PriorHasExplicitSpeed { get; init; }
    public bool SpeedReduced { get; init; }
}

public sealed class StopAndGoPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double PauseDuration { get; init; }
    public required double PauseElapsed { get; init; }
    public required bool Stopped { get; init; }
    public required bool Reaccelerating { get; init; }
    public required bool Airborne { get; init; }
    public required bool GoTriggered { get; init; }
}

public sealed class TouchAndGoPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double RolloutDuration { get; init; }
    public required double RolloutElapsed { get; init; }
    public required bool Reaccelerating { get; init; }
    public required bool Airborne { get; init; }
}

public sealed class TakeoffPhaseDto : PhaseDto
{
    public required bool Airborne { get; init; }
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public DepartureInstructionDto? Departure { get; init; }
}

public sealed class InitialClimbPhaseDto : PhaseDto
{
    public DepartureInstructionDto? Departure { get; init; }
    public int? AssignedAltitude { get; init; }
    public List<NavigationTargetDto>? DepartureRoute { get; init; }
    public required bool IsVfr { get; init; }
    public required int CruiseAltitude { get; init; }
    public string? DepartureSidId { get; init; }
    public double? SidDepartureHeadingMagnetic { get; init; }
    public required double FieldElevation { get; init; }
    public required double TargetAltitude { get; init; }
    public double? DepartureHeadingDeg { get; init; }
    public double? PhaseCompletionAltitude { get; init; }
    public required double SelfClearAltitude { get; init; }
    public required double RunwayDerLat { get; init; } = 0.0;
    public required double RunwayDerLon { get; init; } = 0.0;
    public required double RunwayHeadingDeg { get; init; } = 0.0;
    public required double VfrTurnAltitude { get; init; } = 0.0;
    public required bool VfrTurnApplied { get; init; } = false;
    public required bool RvSidActive { get; init; } = false;
    public required double RvSidHandoffElapsed { get; init; } = 0.0;
    public bool RvSidDeferHeadingUntilMinAlt { get; init; }
    public bool RvSidHoldRunwayHeading { get; init; }
    public List<ProcedureLegDto>? DepartureProcedureLegs { get; init; }
    public bool ProceduralDeparture { get; init; }
}

public sealed class LineUpPhaseDto : PhaseDto
{
    public required double RunwayHeadingDeg { get; init; }
    public required bool Initialized { get; init; }
    public required double TimeSinceLastLog { get; init; }
    public required double PerpHeadingDeg { get; init; }
    public required bool PerpAligned { get; init; }
    public required bool OnCenterline { get; init; }

    /// <summary>
    /// Rolling takeoff mode at snapshot time. Non-required and defaults to
    /// false so pre-rolling snapshots round-trip without alteration.
    /// <see cref="LineUpPhase.FromSnapshot"/> does not restore this field —
    /// the phase re-derives it from the phase list at the next OnStart.
    /// </summary>
    public bool RollingMode { get; init; }

    /// <summary>
    /// CTOC hold-position state at snapshot time. Non-required, defaults false so
    /// pre-feature snapshots round-trip unchanged. Restored by
    /// <see cref="LineUpPhase.FromSnapshot"/> so a held aircraft stays held on replay.
    /// </summary>
    public bool HoldPosition { get; init; }
}

public sealed class LinedUpAndWaitingPhaseDto : PhaseDto
{
    public DepartureInstructionDto? Departure { get; init; }
    public int? AssignedAltitude { get; init; }
}

public sealed class FinalApproachPhaseDto : PhaseDto
{
    public required bool SkipInterceptCheck { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double ThresholdElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double GsAngleDeg { get; init; }
    public required bool GoAroundTriggered { get; init; }
    public required bool NoClearanceWarningIssued { get; init; }

    /// <summary>
    /// True once the red <c>NoLndgClnc</c> datablock flash has armed for this aircraft on the
    /// current final approach. Arms earlier than <see cref="NoClearanceWarningIssued"/> (the pilot
    /// short-final callout) so the RPO gets more reaction time. Non-required so legacy snapshots
    /// deserialize cleanly; <c>FromSnapshot</c> seeds it from <see cref="NoClearanceWarningIssued"/>.
    /// </summary>
    public bool NoClearanceFlashIssued { get; init; }

    public required bool InterceptChecked { get; init; }
    public required bool IsPatternTraffic { get; init; }
    public required bool TooHighGoAroundChecked { get; init; }
    public required bool FasSet { get; init; } = false;

    /// <summary>
    /// True once the aircraft has been commanded down to the configuration speed
    /// (1.3·Vref). Two-stage decel: configuration gate (this) precedes the FAS gate.
    /// Defaults to false on legacy snapshots; <c>FromSnapshot</c> seeds it from
    /// <see cref="FasSet"/> so restored aircraft past the config gate don't re-fire it.
    /// </summary>
    public bool ConfigSet { get; init; }

    public required double MapDistNm { get; init; }

    /// <summary>
    /// True heading the aircraft tracks on final. For aligned approaches this matches the
    /// runway heading; for offset approaches it differs by the published offset. Nullable
    /// to round-trip pre-FAC-extractor snapshots which have only RunwayHeadingDeg.
    /// </summary>
    public double? FinalApproachCourseDeg { get; init; }

    /// <summary>
    /// Lateral cross-track reference latitude. Null = use the runway threshold (ordinary
    /// approaches). Non-null for parallel-offset approaches whose published MAP fix is
    /// laterally offset from the threshold (e.g. KDCA LDA-X 19).
    /// </summary>
    public double? AnchorLat { get; init; }

    public double? AnchorLon { get; init; }

    /// <summary>
    /// True once the aircraft has reached glideslope altitude from below (or started
    /// above). While false, FinalApproachPhase holds assigned/current altitude rather
    /// than commanding a climb up to the GS — aircraft must never fly UP to capture.
    /// </summary>
    public bool GsCaptured { get; init; }

    /// <summary>Remaining cooldown (seconds) before another spacing S-turn may fire (AIM 4-3-5). Defaults to 0.</summary>
    public double STurnSpacingCooldownSeconds { get; init; }

    /// <summary>True once the one-shot pilot-decision go-around roll has been performed or suppressed. Defaults to false.</summary>
    public bool GoAroundRolled { get; init; }
}

public sealed class LandingPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required bool TouchedDown { get; init; }
    public required bool CanGoAround { get; init; }
    public required double LahsoHoldShortDistNm { get; init; }
    public required bool HasLahso { get; init; }
    public int? CandidateExitHoldShortId { get; init; }
    public int? CandidateExitBranchPointId { get; init; }
    public string? CandidateExitTaxiway { get; init; }
    public double CandidateExitTurnOffSpeed { get; init; }
    public List<int>? CandidateExitPathNodeIds { get; init; }
    public int? ActivePreferenceSide { get; init; }
    public string? ActivePreferenceTaxiway { get; init; }
    public int? OriginalPreferenceSide { get; init; }
    public string? OriginalPreferenceTaxiway { get; init; }
    public bool ExitResolutionEnabled { get; init; }
    public required bool StoppedForLahso { get; init; }

    // LandingPhase additions (optional for backward-compat with older snapshots)
    public int CurrentStateValue { get; init; }
    public double TouchdownLat { get; init; }
    public double TouchdownLon { get; init; }
    public double StabilizedSinceSec { get; init; }
    public List<int>? UnableBranchPointIds { get; init; }
    public int? InferredSideValue { get; init; }
}

// --- Pattern phases ---

public sealed class MidfieldCrossingPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }

    /// <summary>
    /// Initial-turn bias toward the assigned pattern side. Non-required, defaults false
    /// so pre-feature snapshots and arrival/wrong-side joins round-trip unchanged.
    /// </summary>
    public bool BiasTurnToPatternSide { get; init; }
}

public sealed class TeardropReentryPhaseDto : PhaseDto
{
    public required PatternWaypointsDto Waypoints { get; init; }
    public required double OutboundLat { get; init; }
    public required double OutboundLon { get; init; }
    public required double LeadInLat { get; init; }
    public required double LeadInLon { get; init; }
}

public sealed class PatternEntryPhaseDto : PhaseDto
{
    public required double EntryLat { get; init; }
    public required double EntryLon { get; init; }
    public required double PatternAltitude { get; init; }
    public required int Kind { get; init; }
    public double? LeadInLat { get; init; }
    public double? LeadInLon { get; init; }
    public bool HasAnnouncedInitialCall { get; init; }
}

public sealed class BasePhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public double? FinalDistanceNm { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double FinalHeadingDeg { get; init; }
    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }
}

public sealed class CrosswindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required double CrosswindHeadingDeg { get; init; }
    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }
}

public sealed class DownwindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double BaseTurnAlongTrack { get; init; }
    public required double AbeamAlongTrack { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double DownwindHeadingDeg { get; init; }
    public required bool PastAbeam { get; init; }
    public required double AltitudeFloor { get; init; }
    public required bool MidfieldBroadcastIssued { get; init; } = false;
    public bool ShortApproachArmed { get; init; }
    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }
}

public sealed class UpwindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required double UpwindHeadingDeg { get; init; }
    public required double MinTurnAltitude { get; init; } = 0.0;
    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }
}

public sealed class VfrFollowPhaseDto : PhaseDto
{
    public required string TargetCallsign { get; init; }
    public RunwayInfoDto? LeadLandingRunway { get; init; }

    /// <summary>Free-pursuit widen excursion active flag (lateral spacing hysteresis).</summary>
    public bool WidenActive { get; init; }

    /// <summary>Free-pursuit widen excursion side: +1 right of the lead's track, -1 left.</summary>
    public int WidenSide { get; init; }
}

// --- Approach phases ---

public sealed class HoldingPatternPhaseDto : PhaseDto
{
    public required string FixName { get; init; }
    public required double FixLat { get; init; }
    public required double FixLon { get; init; }
    public required int InboundCourse { get; init; }
    public required double LegLength { get; init; }
    public required bool IsMinuteBased { get; init; }
    public required int Direction { get; init; }
    public int? Entry { get; init; }
    public int? MaxCircuits { get; init; }
    public required int State { get; init; }
    public required int ResolvedEntry { get; init; }
    public required double OutboundHeadingDeg { get; init; }
    public required double CorrectedOutboundHeadingDeg { get; init; }
    public required double LegTimerSeconds { get; init; }
    public required int CircuitsCompleted { get; init; }
}

public sealed class ProcedureTurnPhaseDto : PhaseDto
{
    public required string FixName { get; init; }
    public required double FixLat { get; init; }
    public required double FixLon { get; init; }
    public required double InboundCourseDeg { get; init; }
    public required double PtOutboundCourseDeg { get; init; }
    public required double MaxOutboundDistanceNm { get; init; }
    public required int OneEightyTurnDirection { get; init; }
    public required int MinAltitudeFt { get; init; }
    public required int State { get; init; }
    public required double PtOutboundTimerSeconds { get; init; }
}

public sealed class ApproachNavigationPhaseDto : PhaseDto
{
    public required List<ApproachFixDto> Fixes { get; init; }
    public required int CurrentFixIndex { get; init; }
}

public sealed class InterceptCoursePhaseDto : PhaseDto
{
    public required double FinalApproachCourseDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public string? ApproachId { get; init; }
    public double? PreviousSignedCrossTrack { get; init; }
    public double? RunwayHeadingCacheDeg { get; init; }
    public required bool ApproachSpeedSet { get; init; }
    public required bool ForcedIntercept { get; init; }

    /// <summary>JFAC/JLOC relaxed armed join (captures at any cut). Optional (defaults
    /// false) so recordings made before the field deserialize cleanly.</summary>
    public bool RelaxedJoin { get; init; }
}

public sealed class DepartureProcedurePhaseDto : PhaseDto
{
    public required List<ProcedureLegDto> Legs { get; init; }
    public required List<NavigationTargetDto> PostRoute { get; init; }
    public int? AssignedAltitude { get; init; }
    public required int CruiseAltitude { get; init; }
    public required int LegIndex { get; init; }
    public required bool Overridden { get; init; }
    public LatLon? LegEntryPosition { get; init; }
    public double? PreviousSignedCrossTrack { get; init; }
    public double LegElapsedSeconds { get; init; }
}
