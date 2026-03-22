using System.Text.Json.Serialization;
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
    public RunwayInfoDto? PatternRunway { get; init; }
}

public sealed class ApproachClearanceDto
{
    public required string ApproachId { get; init; }
    public required string AirportCode { get; init; }
    public required string RunwayId { get; init; }
    public required double FinalApproachCourseDeg { get; init; }
    public required bool StraightIn { get; init; }
    public required bool Force { get; init; }
    public int? MapAltitudeFt { get; init; }
    public double? MapDistanceNm { get; init; }
    public double? InterceptCaptureDistanceNm { get; init; }
    public double? InterceptCaptureAngleDeg { get; init; }
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
}

// --- Phase (polymorphic) ---

[JsonDerivedType(typeof(HoldingShortPhaseDto), "HoldingShort")]
[JsonDerivedType(typeof(CrossingRunwayPhaseDto), "CrossingRunway")]
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
[JsonDerivedType(typeof(PatternEntryPhaseDto), "PatternEntry")]
[JsonDerivedType(typeof(BasePhaseDto), "Base")]
[JsonDerivedType(typeof(CrosswindPhaseDto), "Crosswind")]
[JsonDerivedType(typeof(DownwindPhaseDto), "Downwind")]
[JsonDerivedType(typeof(UpwindPhaseDto), "Upwind")]
[JsonDerivedType(typeof(HoldingPatternPhaseDto), "HoldingPattern")]
[JsonDerivedType(typeof(ApproachNavigationPhaseDto), "ApproachNavigation")]
[JsonDerivedType(typeof(InterceptCoursePhaseDto), "InterceptCourse")]
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
}

public sealed class CrossingRunwayPhaseDto : PhaseDto
{
    public required int TargetNodeId { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required bool Initialized { get; init; }
    public required double TimeSinceLastLog { get; init; }
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

public sealed class HoldingAfterExitPhaseDto : PhaseDto;

public sealed class AtParkingPhaseDto : PhaseDto;

public sealed class TaxiingPhaseDto : PhaseDto
{
    public required int TargetNodeId { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required bool Initialized { get; init; }
    public required double TimeSinceLastLog { get; init; }
    public required double PrevDistToTarget { get; init; }
    public required double CurrentNodeRequiredSpeed { get; init; }
    public List<SpeedConstraintDto>? SpeedConstraints { get; init; }
}

public sealed class SpeedConstraintDto
{
    public required double PathDistNm { get; init; }
    public required double RequiredSpeedKts { get; init; }
    public required int NodeId { get; init; }
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
    public int? ClearNodeId { get; init; }
    public required bool ReachedExitNode { get; init; }
    public string? ExitTaxiway { get; init; }
    public string? RunwayId { get; init; }
    public int? LastResolvedPreference { get; init; }
    public required double ExitSpeed { get; init; }
    public required double TimeSinceLastLog { get; init; }
    public required bool StoppedForLahso { get; init; }
    public int? CurrentCenterlineNodeId { get; init; }
    public int? NextCenterlineNodeId { get; init; }
    public double RunwayHeadingDeg { get; init; }
    public int ExitStateValue { get; init; }
    public bool Braking { get; init; }
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
}

public sealed class HelicopterTakeoffPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public DepartureInstructionDto? Departure { get; init; }
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
}

public sealed class STurnPhaseDto : PhaseDto
{
    public required int InitialDirection { get; init; }
    public required int Count { get; init; }
    public required double FinalHeadingDeg { get; init; }
    public required int TurnsCompleted { get; init; }
    public required bool TurningToFinal { get; init; }
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
    public double RunwayDerLat { get; init; }
    public double RunwayDerLon { get; init; }
    public double RunwayHeadingDeg { get; init; }
    public double VfrTurnAltitude { get; init; }
    public bool VfrTurnApplied { get; init; }
}

public sealed class LineUpPhaseDto : PhaseDto
{
    public int? HoldShortNodeId { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required bool Initialized { get; init; }
    public required double TimeSinceLastLog { get; init; }
    public required double Stage1Lat { get; init; }
    public required double Stage1Lon { get; init; }
    public required bool HasStage1 { get; init; }
    public required bool Stage1Complete { get; init; }
    public required double CenterlineLat { get; init; }
    public required double CenterlineLon { get; init; }
    public required bool AligningOnly { get; init; }
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
    public required bool InterceptChecked { get; init; }
    public required bool IsPatternTraffic { get; init; }
    public required bool TooHighGoAroundChecked { get; init; }
    public bool FasSet { get; init; }
    public required double MapDistNm { get; init; }
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
    public int? ResolvedExitNodeId { get; init; }
    public required double ExitTurnOffSpeed { get; init; }
    public int? LastResolvedPreference { get; init; }
    public required bool StoppedForLahso { get; init; }
}

// --- Pattern phases ---

public sealed class MidfieldCrossingPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
}

public sealed class PatternEntryPhaseDto : PhaseDto
{
    public required double EntryLat { get; init; }
    public required double EntryLon { get; init; }
    public required double PatternAltitude { get; init; }
    public double? LeadInLat { get; init; }
    public double? LeadInLon { get; init; }
}

public sealed class BasePhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public double? FinalDistanceNm { get; init; }
    public required bool IsExtended { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double FinalHeadingDeg { get; init; }
}

public sealed class CrosswindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required double CrosswindHeadingDeg { get; init; }
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
}

public sealed class UpwindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required double UpwindHeadingDeg { get; init; }
    public double MinTurnAltitude { get; init; }
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
}
