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
    public bool ForceLanding { get; init; }
    public bool ForcedRollout { get; init; }
    public int? TrafficDirection { get; init; }
    public RunwayInfoDto? PatternRunway { get; init; }
    public RunwayInfoDto? DepartureRunway { get; init; }
    public int? RequestedExit { get; init; }
    public string? RequestedExitTaxiway { get; init; }
    public ApproachClearanceDto? ActiveApproach { get; init; }
    public LahsoTargetDto? LahsoHoldShort { get; init; }

    // Taxiways the crew has told the controller it is unable to exit at on this landing: every exit search of the landing and the
    // runway exit skips them until a new instruction names one again. Not `required`: absent on older snapshots, where none is given
    // up; and not written while null, so a snapshot with nothing given up — every snapshot 0 — serializes exactly as it did before
    // the field existed (yaat-server pins snapshot 0's hash).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? GivenUpExitTaxiways { get; init; }
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
    public required double WidthFt { get; init; }

    /// <summary>
    /// Airport (field) elevation. Nullable: snapshots written before runway ends carried their own
    /// elevations have no value, and <see cref="Yaat.Sim.Phases.RunwayInfo.AirportElevationFt"/> then
    /// falls back to the mean of the two ends — which for those snapshots is exactly what they stored.
    /// </summary>
    public double? AirportElevationFt { get; init; }
}

public sealed class DepartureClearanceDto
{
    public required int Type { get; init; }
    public required DepartureInstructionDto Departure { get; init; }
    public int? AssignedAltitude { get; init; }
    public List<NavigationTargetDto>? DepartureRoute { get; init; }
    public List<ProcedureLegDto>? DepartureProcedureLegs { get; init; }
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
[JsonDerivedType(typeof(PatternExitDepartureDto), "PatternExit")]
[JsonDerivedType(typeof(ClosedTrafficDepartureDto), "ClosedTraffic")]
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

public sealed class PatternExitDepartureDto : DepartureInstructionDto
{
    /// <summary>0=Upwind, 1=Crosswind, 2=Downwind, 3=Base, 4=Final (matches PatternEntryLeg). Only Crosswind/Downwind are produced.</summary>
    public required int ExitLeg { get; init; }

    /// <summary>0=Left, 1=Right (matches PatternDirection).</summary>
    public required int Direction { get; init; }
}

public sealed class ClosedTrafficDepartureDto : DepartureInstructionDto
{
    /// <summary>0=Left, 1=Right (matches PatternDirection).</summary>
    public required int Direction { get; init; }

    /// <summary>Optional cross-runway pattern runway; null = pattern on the takeoff runway.</summary>
    public string? RunwayId { get; init; }

    /// <summary>Optional pattern altitude override (ft MSL).</summary>
    public int? PatternAltitude { get; init; }

    /// <summary>The pattern-altitude token as typed, so a restored clearance renders the canonical text it was issued with.</summary>
    public string? PatternAltitudeText { get; init; }
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

    /// <summary>Resolved downwind offset (nm). Optional for backward compat — when null,
    /// FromSnapshot derives it from the abeam offset from the threshold.</summary>
    public double? PatternSizeNm { get; init; }

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
[JsonDerivedType(typeof(HelicopterApproachPhaseDto), "HelicopterApproach")]
[JsonDerivedType(typeof(GoAroundPhaseDto), "GoAround")]
[JsonDerivedType(typeof(HelicopterTakeoffPhaseDto), "HelicopterTakeoff")]
[JsonDerivedType(typeof(LowApproachPhaseDto), "LowApproach")]
[JsonDerivedType(typeof(RunwayHoldingPhaseDto), "RunwayHolding")]
[JsonDerivedType(typeof(MakeTurnPhaseDto), "MakeTurn")]
[JsonDerivedType(typeof(VfrHoldPhaseDto), "VfrHold")]
[JsonDerivedType(typeof(AirspaceBoundaryHoldPhaseDto), "AirspaceBoundaryHold")]
[JsonDerivedType(typeof(MilitaryRoutePhaseDto), "MilitaryRoute")]
[JsonDerivedType(typeof(AerialRefuelingAnchorPhaseDto), "AerialRefuelingAnchor")]
[JsonDerivedType(typeof(STurnPhaseDto), "STurn")]
[JsonDerivedType(typeof(StopAndGoPhaseDto), "StopAndGo")]
[JsonDerivedType(typeof(TouchAndGoPhaseDto), "TouchAndGo")]
[JsonDerivedType(typeof(TakeoffPhaseDto), "Takeoff")]
[JsonDerivedType(typeof(RejectedTakeoffPhaseDto), "RejectedTakeoff")]
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
[JsonDerivedType(typeof(PatternExitPhaseDto), "PatternExit")]
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
    /// Navigator state for the slice between approach and target, including the primitive it was playing and its progress
    /// (<see cref="GroundNavigatorDto.Playback"/>). The first OnTick after restore rebuilds the slice from the restored
    /// source route and sets the slice segment up again on this navigator, which resumes the saved primitive where it
    /// stood. Null for a crossing that had not ticked, and in older snapshots: the restore then builds a fresh navigator.
    /// </summary>
    public GroundNavigatorDto? Navigator { get; init; }

    /// <summary>
    /// The slice segment the crossing was on; the restore's slice rebuild starts on it. Defaults to 0 for legacy
    /// snapshots.
    /// </summary>
    public int CrossingRouteSegmentIndex { get; init; }

    /// <summary>
    /// The aircraft's latitude when the navigator set up the slice segment it is on. The navigator builds that segment's
    /// primitive (an entry-alignment turn, a re-anchored line) from the pose it is set up at, so a restore sets the segment
    /// up again from this pose, not from wherever the aircraft has rolled to since. Null in snapshots written before the
    /// field existed, which rebuild from the live pose.
    /// </summary>
    public double? SegmentSetupLat { get; init; }

    /// <summary>Longitude of the pose the current slice segment was set up at. See <see cref="SegmentSetupLat"/>.</summary>
    public double? SegmentSetupLon { get; init; }

    /// <summary>True heading (degrees) of the pose the current slice segment was set up at. See <see cref="SegmentSetupLat"/>.</summary>
    public double? SegmentSetupHeadingDeg { get; init; }

    /// <summary>
    /// The crossing path the phase was handed (<c>CrossingRunwayPhase.OverOwnPath</c>) instead of slicing the
    /// aircraft's assigned taxi route — the crossing that releases a follow armed at a runway bar. Null for the
    /// ordinary crossing, and in snapshots written before the field existed.
    /// </summary>
    public TaxiRouteDto? OwnPath { get; init; }
}

/// <summary>
/// Snapshot for <see cref="Yaat.Sim.Phases.Ground.ClearRunwayPhase"/> (issue #172 W5). Carries the runway
/// hold-short node and the approach (runway-side) node; the navigator is not carried, and the first OnTick after
/// restore builds a fresh one from those nodes and the aircraft's pose.
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
    public bool StoppedInsideHoldingDistance { get; init; }
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

    /// <summary>
    /// Node of the hold-short whose stop the phase already moved forward for being unmakeable, so a restored
    /// phase does not move it a second time. Null on legacy snapshots, and whenever no bar has been moved.
    /// </summary>
    public int? UnableStopNodeId { get; init; }

    /// <summary>
    /// Node of the uncleared runway hold-short the route starts on whose marking the nose was found past, which the phase is
    /// stopping for at the firm rate until it takes the hold or the bar is cleared. Null whenever no such stop is under way.
    /// </summary>
    public int? PassedStartBarNodeId { get; init; }
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

    /// <summary>
    /// The current fillet segment is being flown as the straight line a node-aimed alignment arc rolled out on, from
    /// <see cref="SegmentFromLat"/>/<see cref="SegmentFromLon"/> to the fillet's to-node, not as its curve.
    /// </summary>
    public bool OnAimedLineOverFillet { get; init; }

    /// <summary>
    /// The from-node of the fillet <see cref="OnAimedLineOverFillet"/> is flown over; null (a snapshot without it) reads
    /// as no aimed line, so the restore plays the fillet as its curve.
    /// </summary>
    public int? AimedLineFilletFromNodeId { get; init; }

    /// <summary>
    /// The primitive the navigator was playing and how far along it it had got, so a restore resumes that primitive where
    /// it stood rather than building a new one from the aircraft's pose. Null when no primitive was active, and in
    /// snapshots written before the field existed: the restore then sets the segment up again from where the aircraft
    /// stands.
    /// </summary>
    public GroundNavigatorPlaybackDto? Playback { get; init; }
}

/// <summary>
/// The navigator's active primitive with its playback progress, its arc-entry blend and the entry-alignment bookkeeping
/// that decides what follows the primitive (<see cref="GroundNavigatorDto.Playback"/>).
/// </summary>
public sealed class GroundNavigatorPlaybackDto
{
    public required PathPrimitiveDto Primitive { get; init; }

    /// <summary>
    /// The from-node of the route segment the playback was captured on; a resume needs both ends of the segment to match,
    /// this one and the to-node (<see cref="GroundNavigatorDto.TargetNodeId"/>).
    /// </summary>
    public required int FromNodeId { get; init; }

    /// <summary>The primitive is an entry-alignment turn holding the segment's own primitive back until it completes.</summary>
    public required bool HasPendingSegmentPrimitive { get; init; }

    public required double ArcBearingFromCenterDeg { get; init; }
    public required double ArcRemainingSweepDeg { get; init; }
    public required double BezierT { get; init; }
    public required double BezierTraveledFt { get; init; }
    public required double BezierLeadInRemainingFt { get; init; }
    public required double ArcEntryOffsetLatDeg { get; init; }
    public required double ArcEntryOffsetLonDeg { get; init; }
    public required double ArcEntryTravelledFt { get; init; }
    public required double ArcEntryBlendFt { get; init; }

    /// <summary>The primitive has not yet had its first tick, which captures its entry offset.</summary>
    public required bool ArcEntryPending { get; init; }

    public required double CumulativeTurnSinceAdvanceDeg { get; init; }

    /// <summary>The active entry-alignment turn is aimed at a route node rather than at a bearing.</summary>
    public required bool AimedAtRouteNode { get; init; }

    public required int NodeAimSegmentIndex { get; init; }
    public required int AimedPastThroughSegmentIndex { get; init; }
    public required bool EntryArcAimedAtNodeOffRealLeg { get; init; }

    /// <summary>
    /// The reversal arc of a turn about on a taxiway, waiting while <see cref="Primitive"/> — the jog that centres it on the
    /// centreline — plays. Null when no jog is playing, and in snapshots written before the field existed. Not written while
    /// null, so a snapshot with no jog playing — every snapshot 0 — serializes exactly as it did before the field existed
    /// (yaat-server pins snapshot 0's hash).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SlowTurnPrimitiveDto? PendingTurnAboutArc { get; init; }

    /// <summary>
    /// True while the navigator is playing the reversal arc of a turn about on a taxiway (<see cref="Primitive"/>), whose
    /// end-of-arc heading nudge is limited on that arc's own tight radius rather than the comfortable main-gear one. Absent
    /// while no reversal plays and in snapshots written before the field existed, so a snapshot with no turn about
    /// serializes exactly as it did before the field existed (yaat-server pins snapshot 0's hash).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TurnAboutReversalPlaying { get; init; }

    /// <summary>
    /// True while the jog of a turn about on a taxiway, and then its reversal, plays toward a node where the route then
    /// turns to the side the reversal ends on, so the straight after it holds the taxiway's bearing to abeam that node
    /// rather than steering back onto the centreline first. Absent (null) when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TurnAboutRollsOutAlongEdge { get; init; }

    /// <summary>
    /// How far (ft) the straight a turn about holds on its roll-out bearing runs inside the coming turn, off the segment's
    /// centreline, kept through the node turn that straight ends in: on the straight it places the arrival point that lays
    /// the node turn tangent to the outgoing centreline, through the node turn it keeps the end-of-arc nudge off. Absent
    /// (null) when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TurnAboutRollOutOffsetFt { get; init; }

    /// <summary>
    /// True while the jog of a turn about on a taxiway, and then its reversal, plays a reversal rolled out on the taxiway
    /// edge's own bearing rather than re-aimed past the bend, so a straight after it that ends in a stop is laid on the
    /// centreline through the stop. Absent (null) when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TurnAboutReversalOnEdgeBearing { get; init; }

    /// <summary>
    /// True while the straight after a turn about is laid on the centreline through a stop, from abeam where the aircraft
    /// rolled out off it, and steered in its last look-ahead window along the line past the stop rather than at the stop
    /// itself, so the aircraft stops square to the bar. Absent (null) when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TurnAboutSquareStopLine { get; init; }

    /// <summary>
    /// The brake leg a rolling aircraft flies, heading held straight, down to its pivot speed before the turn about
    /// <see cref="Primitive"/> begins. Absent (null) when no brake leg is under way, so a snapshot without one serializes
    /// exactly as it did before the field existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TurnAboutBrakeDto? TurnAboutBrake { get; init; }

    /// <summary>
    /// The stop a rolling aircraft with no room to turn about on its taxiway brakes to, heading held straight, and holds at.
    /// Absent (null) when the aircraft is not so held.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TurnAboutHoldDto? TurnAboutHold { get; init; }
}

/// <summary>
/// A rolling aircraft's stop when it has no room to turn about on its taxiway (<see cref="GroundNavigatorPlaybackDto.TurnAboutHold"/>).
/// </summary>
public sealed class TurnAboutHoldDto
{
    /// <summary>Latitude of the point the aircraft comes to rest at.</summary>
    public required double StopLat { get; init; }

    /// <summary>Longitude of that point.</summary>
    public required double StopLon { get; init; }

    /// <summary>The heading (deg true) held straight while stopping.</summary>
    public required double BearingDeg { get; init; }

    /// <summary>The brake rate (kts/s).</summary>
    public required double DecelRateKts { get; init; }

    /// <summary>The taxiway it has no room to turn about on, named in its unable.</summary>
    public required string Taxiway { get; init; }

    /// <summary>Whether it has said its unable, on the stop's first tick.</summary>
    public required bool UnableSaid { get; init; }
}

/// <summary>A rolling aircraft's brake leg before its turn about on a taxiway (<see cref="GroundNavigatorPlaybackDto.TurnAboutBrake"/>).</summary>
public sealed class TurnAboutBrakeDto
{
    /// <summary>Latitude of the point the turn about was solved from, where the aircraft reaches its pivot speed.</summary>
    public required double EndLat { get; init; }

    /// <summary>Longitude of that point.</summary>
    public required double EndLon { get; init; }

    /// <summary>The heading (deg true) held straight while braking.</summary>
    public required double BearingDeg { get; init; }

    /// <summary>The turn about's pivot speed (kts).</summary>
    public required double PivotKts { get; init; }

    /// <summary>The brake rate (kts/s).</summary>
    public required double DecelRateKts { get; init; }
}

/// <summary>A navigator path primitive (<c>PathPrimitive</c>), by shape.</summary>
[JsonDerivedType(typeof(StraightPrimitiveDto), "Straight")]
[JsonDerivedType(typeof(BezierPrimitiveDto), "Bezier")]
[JsonDerivedType(typeof(SlowTurnPrimitiveDto), "SlowTurn")]
public abstract class PathPrimitiveDto
{
    public required double LengthFt { get; init; }
    public required int ToNodeId { get; init; }
}

public sealed class StraightPrimitiveDto : PathPrimitiveDto
{
    public required double FromLat { get; init; }
    public required double FromLon { get; init; }
    public required double ToLat { get; init; }
    public required double ToLon { get; init; }
    public required double BearingDeg { get; init; }
}

public sealed class BezierPrimitiveDto : PathPrimitiveDto
{
    public required double P0Lat { get; init; }
    public required double P0Lon { get; init; }
    public required double P1Lat { get; init; }
    public required double P1Lon { get; init; }
    public required double P2Lat { get; init; }
    public required double P2Lon { get; init; }
    public required double P3Lat { get; init; }
    public required double P3Lon { get; init; }
    public required double EntryTangentBearingDeg { get; init; }
    public required double ExitTangentBearingDeg { get; init; }
}

public sealed class SlowTurnPrimitiveDto : PathPrimitiveDto
{
    public required double CenterLat { get; init; }
    public required double CenterLon { get; init; }
    public required double RadiusFt { get; init; }
    public required double StartBearingFromCenterDeg { get; init; }
    public required double SweepDeg { get; init; }
    public required bool RightTurn { get; init; }
    public required double EntryTangentBearingDeg { get; init; }
    public required double ExitTangentBearingDeg { get; init; }
    public required double MaxSpeedKts { get; init; }
}

public sealed class FollowingPhaseDto : PhaseDto
{
    public required string TargetCallsign { get; init; }
    public required double TimeSinceLastLog { get; init; }

    /// <summary>
    /// Runways the crossing clearance that started this follow cleared (<c>FollowingPhase.CrossingClearedRunways</c>).
    /// Empty for a follow no crossing clearance started, and in snapshots written before the field existed.
    /// </summary>
    public List<string> CrossingClearedRunways { get; init; } = [];

    /// <summary>
    /// Whether the follower has been on the pavement of one of <see cref="CrossingClearedRunways"/> — the clearance is
    /// spent once it is clear of them again. False in snapshots written before the field existed.
    /// </summary>
    public bool HasBeenOnClearedRunway { get; init; }

    /// <summary>
    /// The runway hold-short node the follow is stopping at (<c>FollowingPhase</c>'s latched bar). Null with no bar ahead,
    /// and in snapshots written before the field existed.
    /// </summary>
    public int? LatchedBarNodeId { get; init; }

    /// <summary>
    /// The bearing (deg true) from the latched bar back up the taxiway edge leading into it — the axis its hold line is
    /// square to. Null with no latched bar.
    /// </summary>
    public double? LatchedBarApproachDeg { get; init; }

    /// <summary>
    /// The first end node id (<c>Nodes[0]</c>) of the straight taxi edge the follower was last found on, where
    /// <c>FollowingPhase</c> looks first for the taxiway it is on. Null before the first find, while off every taxiway, and in
    /// snapshots written before the field existed (the restored follow then scans the whole layout once).
    /// </summary>
    public int? TaxiEdgeNodeA { get; init; }

    /// <summary>The other end node id (<c>Nodes[1]</c>) of that taxi edge. See <see cref="TaxiEdgeNodeA"/>.</summary>
    public int? TaxiEdgeNodeB { get; init; }

    /// <summary>
    /// The follow route the follower drives (<c>FollowingPhase.FollowRoute</c>): its route to the merge node, then the lead's
    /// path from there. Null before the follow planned one, and in snapshots written before the field existed; the restored
    /// follow then plans on its next tick.
    /// </summary>
    public TaxiRouteDto? FollowRoute { get; init; }

    /// <summary>
    /// The index of <see cref="FollowRoute"/>'s first segment on the lead's path, the segment out of the merge node
    /// (<c>FollowingPhase.MergeSegmentIndex</c>); the segments before it are the follower's own route to the merge. Zero with no
    /// follow route.
    /// </summary>
    public int MergeSegmentIndex { get; init; }

    /// <summary>
    /// The lead's path edge into the merge node, in the lead's direction of travel — the edge the give-way stop keeps clear of
    /// with the one out of the merge — written as <see cref="FollowRoute"/>'s own segments are, so a restore resolves the edge
    /// the live follow held between two nodes joined by more than one. Null where the lead's path starts at the merge, with no
    /// follow route, and in older snapshots.
    /// </summary>
    public TaxiSegmentDto? LeadEdgeIntoMerge { get; init; }

    /// <summary>
    /// Runways whose pavement the follower occupied during the follow and has not yet exited clear of, whose bars it passes on
    /// the way out (<c>FollowingPhase</c>'s exiting runways). Empty in snapshots written before the field existed.
    /// </summary>
    public List<string> ExitingRunways { get; init; } = [];

    /// <summary>
    /// Whether the follower is giving way short of the merge (<c>FollowingPhase.IsGivingWay</c>), stopping short of the lead's
    /// track there until the lead's tail is past the merge and the follow gap is open. False with no follow route.
    /// </summary>
    public bool GivingWay { get; init; }

    /// <summary>
    /// Whether the follow holds in position for good, planning no more (<c>FollowingPhase.IsUnjoinable</c>): no taxi path to
    /// the lead's path, or the follower ahead of the lead. False in snapshots written before the field existed.
    /// </summary>
    public bool Unjoinable { get; init; }

    /// <summary>
    /// Whether the follower is braking to rest along a follow route it lost (<c>FollowingPhase.IsBrakingLostRoute</c>), dropped
    /// once at rest: true while braking, null otherwise. Not written while null, so a snapshot of a follow not braking along a
    /// lost route serializes exactly as it did before the field existed; null in snapshots written before it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BrakingLostRoute { get; init; }

    /// <summary>
    /// Whether this follow has said the clearing-route run-out call, holding short of the runway ahead and not clear of the one
    /// behind: true once said, null otherwise. Not written while null, so a snapshot of a follow that has said nothing
    /// serializes exactly as it did before the field existed; null in snapshots written before it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaidHeldShortNotClear { get; init; }

    /// <summary>
    /// Whether this follow has said that it lost the traffic and is holding in position, asking for taxi instructions: true once
    /// said, null otherwise. Not written while null, so a snapshot of a follow that has said nothing serializes exactly as it did
    /// before the field existed; null in snapshots written before it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaidLostTraffic { get; init; }

    /// <summary>
    /// Whether this follow has said that it cannot follow at all, with no taxi route onto its traffic's path: true once said,
    /// null otherwise. Not written while null, so a snapshot of a follow that has said nothing serializes exactly as it did
    /// before the field existed; null in snapshots written before it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaidUnableNoRoute { get; init; }

    /// <summary>
    /// The clearing route the follower drives off a runway it has no follow route on (<c>FollowingPhase.ClearingRoute</c>): to
    /// the nearest hold-short bar ahead and on past it. Null when not clearing, and in older snapshots.
    /// </summary>
    public TaxiRouteDto? ClearingRoute { get; init; }

    /// <summary>The hold-short bar node the clearing route clears; null when not clearing.</summary>
    public int? ClearingBarNodeId { get; init; }

    /// <summary>
    /// The runways a clearing route has been tried for since the follow last installed a follow route
    /// (<c>FollowingPhase.ClearingAttemptedRunways</c>). Written only when one has; null in older snapshots.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ClearingAttemptedRunways { get; init; }

    /// <summary>
    /// The follow navigator's state, including the primitive it was playing and its progress (<see cref="GroundNavigatorDto.Playback"/>),
    /// resumed at the restored follow's first segment set-up. Null with neither a follow route nor a clearing route.
    /// </summary>
    public GroundNavigatorDto? Navigator { get; init; }
}

/// <summary>
/// A <c>PushbackPhase</c>: the <see cref="TugMove"/> it flies, where the planner said it ends, its stand push-off
/// amendment, and its progress. A snapshot written before tug moves existed carries none of the move fields
/// (<see cref="Shape"/> is null) and only the <c>Legacy*</c> ones, which <c>PushbackPhase.FromSnapshot</c> turns into
/// an equivalent move.
/// </summary>
public sealed class PushbackPhaseDto : PhaseDto
{
    /// <summary>
    /// Which end the tug leads with over the move. Defaults to <see cref="PushbackLegKind.Push"/>, which is
    /// what a snapshot written before tug moves existed meant — an ordinary tail-first reverse.
    /// </summary>
    public PushbackLegKind Kind { get; init; }

    /// <summary>The move's shape; null on a snapshot written before tug moves existed.</summary>
    public TugMoveShape? Shape { get; init; }

    public double StraightDistanceFt { get; init; }
    public double PointLatitude { get; init; }
    public double PointLongitude { get; init; }
    public double LineTravelTrueDeg { get; init; }
    public double? StopAtLatitude { get; init; }
    public double? StopAtLongitude { get; init; }
    public double FacingTrueDeg { get; init; }
    public bool Tight { get; init; }
    public bool Creep { get; init; }
    public bool DwellBefore { get; init; }
    public bool StartsAtStand { get; init; }

    /// <summary>
    /// The tug goes straight on into another move when this one completes, so the move neither crawls its last 10 ft
    /// nor stops dead. Absent on a snapshot written before move boundaries were flown through, which restores false —
    /// the behaviour those snapshots were recorded with.
    /// </summary>
    public bool ContinuesIntoNextMove { get; init; }

    /// <summary>
    /// The move is flown through from the stand push-off with no reversal between, so it keeps the push's ramp priority
    /// in the conflict detector. Absent on a snapshot written before the priority was narrowed to leg 1, which restores
    /// false — only the push-off itself then keeps its priority, through <see cref="StartsAtStand"/>.
    /// </summary>
    public bool ContinuesStandPushOff { get; init; }

    /// <summary>
    /// The move is the tow's last, whose completion records <see cref="EndTaxiway"/> as the aircraft's taxiway. Absent
    /// on a snapshot written before the field existed, which restores false and leaves the taxiway as it was.
    /// </summary>
    public bool IsLastMove { get; init; }

    /// <summary>The taxiway the tow's final goal names, or null; read only on the last move.</summary>
    public string? EndTaxiway { get; init; }

    /// <summary>
    /// The tow has a forced leg kind or a marked-point goal, so a mid-push facing change is refused. Absent on a snapshot
    /// written before the field existed, which restores false — no such tow existed then.
    /// </summary>
    public bool KeepsItsPlan { get; init; }
    public double PlannedEndLatitude { get; init; }
    public double PlannedEndLongitude { get; init; }

    // The stand push-off's mid-push facing amendment (TugAmendment); all null on every other move.
    public TugGoalKind? AmendmentGoalKind { get; init; }
    public int? AmendmentNodeId { get; init; }
    public string? AmendmentTaxiway { get; init; }
    public double? AmendmentStandLatitude { get; init; }
    public double? AmendmentStandLongitude { get; init; }
    public double? AmendmentStandNoseTrueDeg { get; init; }

    // The move's TugMoveProgress.
    public double ProgressStartLatitude { get; init; }
    public double ProgressStartLongitude { get; init; }
    public double ProgressStartTravelTrueDeg { get; init; }
    public double ProgressDistanceFt { get; init; }
    public bool ProgressCaptured { get; init; }
    public double ProgressMaxTravelDeviationDeg { get; init; }

    public double LastLatitude { get; init; }
    public double LastLongitude { get; init; }
    public double DwellElapsedSeconds { get; init; }
    public required double TimeSinceLastLog { get; init; }

    /// <summary>
    /// A move restored from a pre-tug-move snapshot that has not ticked yet: its first tick restarts it from the live
    /// pose (and, for a straight or turn, re-simulates its planned end).
    /// </summary>
    public bool ProgressPending { get; init; }

    /// <summary>
    /// A pending simple push's recorded start: its first tick owes the simple pushback distance less how far the
    /// aircraft already is from here.
    /// </summary>
    public double? PendingPushedFromLatitude { get; init; }
    public double? PendingPushedFromLongitude { get; init; }

    // The pre-tug-move fields, under their original JSON names. Read only by PushbackPhase.FromSnapshot to restore
    // a snapshot written before tug moves; never written.

    /// <summary>Pre-tug-move: the final nose heading, degrees true. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("TargetHeading")]
    public int? LegacyTargetHeading { get; init; }

    /// <summary>Pre-tug-move: the reverse target. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("TargetLatitude")]
    public double? LegacyTargetLatitude { get; init; }

    /// <summary>Pre-tug-move: the reverse target. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("TargetLongitude")]
    public double? LegacyTargetLongitude { get; init; }

    /// <summary>Pre-tug-move: a spot push's rest point. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("PullForwardLatitude")]
    public double? LegacyPullForwardLatitude { get; init; }

    /// <summary>Pre-tug-move: a spot push's rest point. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("PullForwardLongitude")]
    public double? LegacyPullForwardLongitude { get; init; }

    /// <summary>Pre-tug-move: the spot push was on its pull forward. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("PullingForward")]
    public bool? LegacyPullingForward { get; init; }

    /// <summary>Pre-tug-move: where the push (or its current leg) began. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("StartLat")]
    public double? LegacyStartLat { get; init; }

    /// <summary>Pre-tug-move: where the push (or its current leg) began. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("StartLon")]
    public double? LegacyStartLon { get; init; }

    /// <summary>Pre-tug-move: the distance to the target at the start. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("TotalDistToTarget")]
    public double? LegacyTotalDistToTarget { get; init; }

    /// <summary>Pre-tug-move: the push had reached its target. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("ReachedTarget")]
    public bool? LegacyReachedTarget { get; init; }

    /// <summary>Pre-tug-move: the nose had finished its in-place alignment. Read only to restore an old snapshot; never written.</summary>
    [JsonPropertyName("IsAligned")]
    public bool? LegacyIsAligned { get; init; }
}

/// <summary>
/// The pre-#233 spot pushback: a multi-segment reverse along a taxi route, pivoting in place at the corners. Retained
/// as data only, so that snapshots written before the tug-move rework still deserialize — the phase that flew it is
/// gone, and <see cref="Yaat.Sim.Phases.PhaseList"/> converts this DTO to a <see cref="PushbackPhaseDto"/> on restore.
/// Never written by this build, and never flown.
/// </summary>
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

    // Latched once the aircraft is committed AND has begun the turn-off, which is what closes the window
    // for a late EL/ER/EXIT. Not `required`: absent on older snapshots, where false is the safe reading
    // (the restore path rebuilds the exit route from segment 0 anyway).
    public bool TurnStarted { get; init; }

    // The braking rate the committed exit was chosen with, which the turn-off flies at. Not `required`: absent on older
    // snapshots, where null (the category taxi rate) is what those builds braked the turn-off at.
    public double? TurnOffDecelRate { get; init; }

    // A backtrack exit committed whose route is not yet built: the heading flips to the reciprocal when it is.
    public bool BacktrackPending { get; init; }

    // The pilot has already reported that it is stopped with no exit ahead.
    public bool ReportedNoExitAhead { get; init; }

    public GroundNavigatorDto? Navigator { get; init; }
}

// --- Tower phases ---

public sealed class HelicopterLandingPhaseDto : PhaseDto
{
    public required double FieldElevation { get; init; }
    public required bool TouchedDown { get; init; }
}

public sealed class HelicopterApproachPhaseDto : PhaseDto
{
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public string? DestinationName { get; init; }
    public required double FieldElevation { get; init; }
    public required double HoldAltitude { get; init; }
    public required double TimeSinceLastLog { get; init; }
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

    /// <summary>The pending pursuit a climb armed for a lead with no runway
    /// (<see cref="Phases.Tower.GoAroundPhase.PursuesRunwaylessLeadAfterClimb"/>); written only when armed, and absent reads as
    /// false, so a snapshot taken before it existed needs no migration.</summary>
    public bool? PursuesRunwaylessLeadAfterClimb { get; init; }
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

    /// <summary>Low approach is the lead-in to a landing on a diverging runway (#292); turn onto that
    /// runway's final at the gate below instead of the straight climb-out. Absent on pre-#292 snapshots
    /// (defaults false).</summary>
    public bool RetargetToDifferentRunway { get; init; }

    /// <summary>Gate point on the diverging runway's final (last feasible turn point) — see
    /// <see cref="RetargetToDifferentRunway"/>. Zero on pre-#292 snapshots.</summary>
    public double RetargetGateLat { get; init; }
    public double RetargetGateLon { get; init; }
    public double RetargetRunwayHeadingBDeg { get; init; }
    public double RetargetGateNm { get; init; }
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
    public int Mode { get; init; }
    public string? VolumeId { get; init; }
    public int? LevelOffCeilingFtMsl { get; init; }
    public List<NavigationTargetDto>? OriginalRoute { get; init; }
    public double? OriginalTargetHeadingDeg { get; init; }
    public int? OriginalTurnDirection { get; init; }
    public double? OriginalTargetSpeed { get; init; }
    public double? OriginalTargetAltitude { get; init; }
    public double? OriginalAltitudeCeiling { get; init; }
    public required double CumulativeTurn { get; init; }
    public required double LastHeadingDeg { get; init; }
    public required bool Started { get; init; }
}

public sealed class MilitaryRoutePhaseDto : PhaseDto
{
    public required string Designator { get; init; }
    public required int Kind { get; init; }

    /// <summary>Published direction of an aerial refueling track, or null/empty for a training route.</summary>
    public string? Direction { get; init; }

    public string? EntryPointId { get; init; }
    public string? ExitPointId { get; init; }
    public bool Marsa { get; init; }
    public bool TerrainFollowing { get; init; }
    public List<string>? PointNames { get; init; }
    public bool Started { get; init; }
}

public sealed class AerialRefuelingAnchorPhaseDto : PhaseDto
{
    public required string Designator { get; init; }
    public string? Direction { get; init; }

    /// <summary>Flown once to reach the anchor point.</summary>
    public List<string>? EntryNames { get; init; }

    /// <summary>The orbit corners, flown on repeat.</summary>
    public List<string>? PatternNames { get; init; }

    public bool Started { get; init; }
    public int Laps { get; init; }
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
    public double RollElapsedSeconds { get; init; }
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
    public double RollElapsedSeconds { get; init; }
}

public sealed class TakeoffPhaseDto : PhaseDto
{
    public required bool Airborne { get; init; }
    public required double FieldElevation { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public DepartureInstructionDto? Departure { get; init; }
    public double RollElapsedSeconds { get; init; }

    /// <summary>The pending pursuit a climb armed for a lead with no runway
    /// (<see cref="Phases.Tower.TakeoffPhase.PursuesRunwaylessLeadAfterClimb"/>); written only when armed, and absent reads as
    /// false, so a snapshot taken before it existed needs no migration.</summary>
    public bool? PursuesRunwaylessLeadAfterClimb { get; init; }
}

public sealed class RejectedTakeoffPhaseDto : PhaseDto
{
    public required double ReactionRemainingSeconds { get; init; }
    public required double RunwayHeadingDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double PavementLengthFt { get; init; }
    public required bool OverrunReported { get; init; }
    public required bool AutoTriggered { get; init; }
    public string? CannotStopShortOf { get; init; }
    public double RollElapsedSeconds { get; init; }
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
    /// <summary>
    /// Departure runway heading captured when the phase started. Restored by
    /// <see cref="LineUpPhase.FromSnapshot"/> so it round-trips: a restore that is snapshotted again before its
    /// rebuild has run still writes the real heading here rather than 0.
    /// </summary>
    public required double RunwayHeadingDeg { get; init; }

    /// <summary>
    /// Rolling takeoff mode at snapshot time. Non-required and defaults to
    /// false so pre-rolling snapshots round-trip without alteration. Restored by
    /// <see cref="LineUpPhase.FromSnapshot"/>: the snapshot's value outranks the phase list, which cannot
    /// show a mid-phase CTO upgrade or a CTOC revert.
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

    /// <summary>
    /// True once the clean→approach-flap stage has fired, or the category has no such stage. Precedes <see cref="ConfigSet"/>;
    /// defaults to false on earlier snapshots and <c>FromSnapshot</c> seeds it from the later latches.
    /// </summary>
    public bool FlapSet { get; init; }

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

    /// <summary>Accumulated seconds off-course for the lateral-alignment go-around gate (null in pre-gate snapshots).</summary>
    public double? LateralOffCourseSeconds { get; init; }

    /// <summary>Remaining lateral-alignment-gate hold-off after a commanded retarget/join (null in pre-gate snapshots).</summary>
    public double? LateralGateGraceSeconds { get; init; }

    /// <summary>The ceiling of a follower's spacing before the FAS bleed (kt): the latest schedule speed, else the speed latched
    /// at phase entry. Null in snapshots made before the field, which re-latch at the first spacing tick.</summary>
    public double? SpacingCeilingKts { get; init; }

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

    /// <summary>The braking rate that selected the candidate exit; null on snapshots written before it round-tripped.</summary>
    public double? CandidateExitSelectionDecelRate { get; init; }

    /// <summary>The side the exit search found the candidate exit on (<c>ExitSide</c>); null when none, or on older snapshots.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CandidateExitSide { get; init; }

    public int? ActivePreferenceSide { get; init; }
    public string? ActivePreferenceTaxiway { get; init; }
    public int? OriginalPreferenceSide { get; init; }
    public string? OriginalPreferenceTaxiway { get; init; }
    public bool ExitResolutionEnabled { get; init; }

    // The crew has told the controller it is unable to make the exit the controller named, so it does not say it again
    // for that exit. Not `required`: absent on older snapshots, where false lets the call be made once more.
    public bool UnableBroadcast { get; init; }

    // The remembered original preference was the very instance the phase list's RequestedExit held, so a restore shares it
    // again; an equal but separate instruction (re-issued, not yet ticked) restores as new. Not `required`: absent on older
    // snapshots, where false makes the restored list's preference read as a new instruction.
    public bool OriginalPreferenceIsRequestedExit { get; init; }
    public required bool StoppedForLahso { get; init; }

    // LandingPhase additions (optional for backward-compat with older snapshots)
    public int CurrentStateValue { get; init; }
    public double TouchdownLat { get; init; }
    public double TouchdownLon { get; init; }

    // Ground speed just after touchdown; null when the phase has not touched down.
    public double? TouchdownGroundSpeedKts { get; init; }

    // A forced rollout that found no usable exit does not search again until it passes this point (nm along the runway
    // from the landing threshold); null when it has not cached a miss.
    public double? ForcedNoExitUntilAlongNm { get; init; }
    public double StabilizedSinceSec { get; init; }
    public List<int>? UnableBranchPointIds { get; init; }
    public int? InferredSideValue { get; init; }

    // The rest of the landing plan. Vref carries the gust additive the aircraft actually flew and the runway id
    // is the one it was assigned, so a rewind lands on the same numbers instead of recomputing them from the
    // weather and the phase list at restore time. Null on snapshots written before the plan round-tripped and on
    // a phase that has not started: those restore by rebuilding from the category table on the first tick.
    public string? RunwayId { get; init; }
    public double? FlareEntryAgl { get; init; }
    public double? FlareFpm { get; init; }
    public double? Vref { get; init; }
    public double? Vtd { get; init; }
    public double? CoastSpeed { get; init; }
    public double? DefaultDecel { get; init; }
    public double? TouchdownAgl { get; init; }
}

// --- Pattern phases ---

public sealed class MidfieldCrossingPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }

    /// <summary>
    /// Direction the initial join turn is biased in (0=Left, 1=Right, matches TurnDirection);
    /// null for the shortest-way arrival/wrong-side join.
    /// </summary>
    public int? InitialTurn { get; init; }

    /// <summary>Crossing flown at pattern altitude for every category (in-pattern crossover).
    /// Nullable for tolerance of recordings predating this field.</summary>
    public bool? CrossAtPatternAltitude { get; init; }
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

    /// <summary>Where the base leg began (<see cref="Phases.Pattern.BasePhase.StartPoint"/>); null for a snapshot
    /// written before it was recorded, and a follower joining this base then uses the aircraft's present position.</summary>
    public double? StartLat { get; init; }

    /// <inheritdoc cref="StartLat"/>
    public double? StartLon { get; init; }

    /// <summary>Whether the follower was widening its base for spacing (<see cref="Phases.Pattern.BasePhase.FollowWidenActive"/>);
    /// null for a snapshot written before the widen existed, restored as not widening.</summary>
    public bool? FollowWidenActive { get; init; }
}

public sealed class CrosswindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required double CrosswindHeadingDeg { get; init; }

    /// <summary>Continuous-climb target (ft MSL) for a pattern-exit downwind departure; null otherwise.</summary>
    public int? DepartureClimbTargetFt { get; init; }

    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }

    /// <summary>One-shot: the follow-extension "unable to turn" advisory has been transmitted.
    /// Nullable for tolerance of recordings predating this field.</summary>
    public bool? FollowExtensionWarningIssued { get; init; }
}

public sealed class PatternExitPhaseDto : PhaseDto
{
    public required double ExitHeadingDeg { get; init; }

    /// <summary>0=Left, 1=Right (matches PatternDirection).</summary>
    public required int Direction { get; init; }

    /// <summary>Fully-resolved continuous-climb target (ft MSL): assigned altitude, else filed cruise,
    /// else pattern altitude. Nullable for tolerance of recordings predating this field.</summary>
    public int? ClimbTargetFt { get; init; }
}

public sealed class DownwindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }
    public required double BaseTurnAlongTrack { get; init; }
    public required double AbeamAlongTrack { get; init; }

    /// <summary>Along-track of the leg's midfield point (midway between the downwind start and the
    /// abeam point). Nullable for recordings predating this field, which recompute it from the waypoints.</summary>
    public double? MidfieldAlongTrack { get; init; }

    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public required double DownwindHeadingDeg { get; init; }
    public required bool PastAbeam { get; init; }

    /// <summary>The level a hold past abeam latched (null = no hold latched).</summary>
    public double? HoldLevelFt { get; init; }

    /// <summary>The line descent's fixed altitude at the base trigger (null = no line descent being flown).</summary>
    public double? DescentTargetFt { get; init; }

    /// <summary>A controller altitude issued during the leg, which owns its altitude (null = none).</summary>
    public double? ControllerAltitudeFt { get; init; }

    /// <summary>The assigned altitude the leg last saw, recorded at its start; a change is a new controller
    /// assignment. Null for no assignment; meaningful only when <see cref="AssignedAltitudeBaselineRecorded"/>.</summary>
    public double? SeenAssignedAltitudeFt { get; init; }

    /// <summary>True once the leg has recorded <see cref="SeenAssignedAltitudeFt"/>. Null or false (a recording predating
    /// the field) makes the restored leg take the assignment in force on its next tick as the baseline.</summary>
    public bool? AssignedAltitudeBaselineRecorded { get; init; }

    public required bool MidfieldBroadcastIssued { get; init; } = false;
    public bool ShortApproachArmed { get; init; }

    /// <summary>Downwind re-intercepts its computed track (wrong-side / cross-runway join). Nullable
    /// for tolerance of recordings predating this field.</summary>
    public bool? RejoinTrack { get; init; }

    /// <summary>Downwind ends at midfield for an in-pattern crossover to a parallel runway. Nullable
    /// for tolerance of recordings predating this field.</summary>
    public bool? ExitAtMidfield { get; init; }
    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }

    /// <summary>One-shot: the follow-extension "unable to turn" advisory has been transmitted.
    /// Nullable for tolerance of recordings predating this field.</summary>
    public bool? FollowExtensionWarningIssued { get; init; }
}

public sealed class UpwindPhaseDto : PhaseDto
{
    public PatternWaypointsDto? Waypoints { get; init; }
    public required bool IsExtended { get; init; }

    /// <summary>Early-crosswind-turn one-shot armed by a TC during takeoff/initial climb (issue #208).
    /// Nullable for tolerance of recordings made before the field existed.</summary>
    public bool? TurnCrosswindArmed { get; init; }

    public required double TargetLat { get; init; }
    public required double TargetLon { get; init; }
    public required double UpwindHeadingDeg { get; init; }
    public required double MinTurnAltitude { get; init; } = 0.0;

    /// <summary>Continuous-climb target (ft MSL) for a pattern-exit departure; null otherwise.</summary>
    public int? DepartureClimbTargetFt { get; init; }

    public double? LateralOffsetTargetNm { get; init; }
    public int? LateralOffsetDirection { get; init; }
    public bool LateralOffsetAcquired { get; init; }

    /// <summary>One-shot: the follow-extension "unable to turn" advisory has been transmitted.
    /// Nullable for tolerance of recordings predating this field.</summary>
    public bool? FollowExtensionWarningIssued { get; init; }
}

public sealed class VfrFollowPhaseDto : PhaseDto
{
    public required string TargetCallsign { get; init; }
    public RunwayInfoDto? LeadLandingRunway { get; init; }

    /// <summary>Free-pursuit widen excursion active flag (lateral spacing hysteresis).</summary>
    public bool WidenActive { get; init; }

    /// <summary>Free-pursuit widen excursion side: +1 right of the lead's track, -1 left.</summary>
    public int WidenSide { get; init; }

    /// <summary>The circuit a pursuit started from a pattern leg returns to; null for any other pursuit and in older snapshots.</summary>
    public FollowPatternReturnDto? PatternReturn { get; init; }

    /// <summary>
    /// The lead's recorded path the follower measures its gap along, as flat [lat, lon, lat, lon, …] pairs, oldest first;
    /// null when empty and in older snapshots (the path then rebuilds from the lead's next positions).
    /// </summary>
    public double[]? LeadPath { get; init; }

    /// <summary>The lead's base the follower remembered, to extend past and join; null when none and in older snapshots.</summary>
    public FollowLeadBaseDto? LeadBase { get; init; }

    /// <summary>Seconds until the follower may call "S-turning for spacing" again; 0 in older snapshots.</summary>
    public double STurnCallCooldownSeconds { get; init; }

    /// <summary>The turn-out to the downwind heading under way; null when none and in older snapshots.</summary>
    public FollowTurnOutDto? TurnOut { get; init; }

    /// <summary>A base break-off asked the pursuit to start with a turn-out; null (read as false) in older snapshots.</summary>
    public bool? TurnOutRequested { get; init; }

    /// <summary>The gap (nm) at the start of the parallel-hold stall window; null when no window is open and in older snapshots.</summary>
    public double? StallWindowStartGapNm { get; init; }

    /// <summary>Seconds the parallel-hold stall window has been open; null when none and in older snapshots.</summary>
    public double? StallWindowSeconds { get; init; }

    /// <summary>The departure-leg hold this pursuit flies before it may steer at its lead
    /// (<see cref="Phases.Pattern.VfrFollowPhase.ClimbOutGate"/>); written only while it holds, and absent reads as no hold.</summary>
    public FollowClimbOutGateDto? ClimbOutGate { get; init; }
}

/// <summary>
/// A pursuit's departure-leg hold (<see cref="Phases.Pattern.FollowClimbOutGate"/>): the departure-end point it must fly past,
/// the upwind heading held until it is over that point, and the altitude at which the crosswind turn is legal.
/// </summary>
public sealed class FollowClimbOutGateDto
{
    public required double DepartureEndLat { get; init; }
    public required double DepartureEndLon { get; init; }
    public required double UpwindHeadingDeg { get; init; }
    public required double MinTurnAltitude { get; init; }
}

/// <summary>A follower's turn-out to the downwind heading: the runway and circuit it rejoins, and where the turn began.</summary>
public sealed class FollowTurnOutDto
{
    public required RunwayInfoDto Runway { get; init; }

    /// <summary>0=Left, 1=Right (matches PatternDirection).</summary>
    public required int Direction { get; init; }

    public required double PatternAltitudeFt { get; init; }
    public required double StartLat { get; init; }
    public required double StartLon { get; init; }
}

/// <summary>
/// A follow lead's base as the follower remembered it: runway, circuit, start point, final-turn distance and the track into
/// the start point.
/// </summary>
public sealed class FollowLeadBaseDto
{
    public required RunwayInfoDto Runway { get; init; }
    public required PatternWaypointsDto Waypoints { get; init; }
    public required double StartLat { get; init; }
    public required double StartLon { get; init; }
    public double? FinalDistanceNm { get; init; }
    public required double LegTrackDeg { get; init; }
}

public sealed class FollowPatternReturnDto
{
    public required RunwayInfoDto Runway { get; init; }

    /// <summary>0=Left, 1=Right (matches PatternDirection).</summary>
    public required int Direction { get; init; }

    public required double PatternAltitudeFt { get; init; }

    /// <summary>The pursuit started from the base leg; null (read as false) in older snapshots.</summary>
    public bool? FromBase { get; init; }
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

    /// <summary>The hold is an approach's hold-in-lieu of a procedure turn. Optional (defaults false) so recordings made
    /// before the field deserialize cleanly.</summary>
    public bool IsHoldInLieu { get; init; }
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

    /// <summary>The procedure-turn inbound line the phase joins after a PT (all three set, or none). Optional
    /// (defaults null) so recordings made before the fields deserialize cleanly.</summary>
    public double? PostTurnAnchorLat { get; init; }
    public double? PostTurnAnchorLon { get; init; }
    public double? PostTurnInboundCourseDeg { get; init; }

    /// <summary>The fixes are the published missed approach. Optional (defaults false) so recordings made before the
    /// field deserialize cleanly.</summary>
    public bool IsMissedApproach { get; init; }

    /// <summary>The latched ceiling of a follower's pre-final spacing (kt); null when not latched. Optional so recordings
    /// made before the field deserialize cleanly.</summary>
    public double? SpacingCeilingKts { get; init; }
}

public sealed class InterceptCoursePhaseDto : PhaseDto
{
    public required double FinalApproachCourseDeg { get; init; }
    public required double ThresholdLat { get; init; }
    public required double ThresholdLon { get; init; }
    public string? ApproachId { get; init; }

    /// <summary>Controller-assigned magnetic heading captured when the intercept was installed.
    /// Optional (defaults null) so recordings made before the field deserialize cleanly.</summary>
    public double? AssignedInterceptHeadingDeg { get; init; }
    public double? PreviousSignedCrossTrack { get; init; }
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

    /// <summary>A controller "maintain" interrupting the SID's vertical navigation
    /// (<see cref="Phases.Tower.DepartureProcedurePhase"/>); written only when set, and absent reads as
    /// false, so a snapshot taken before it existed needs no migration.</summary>
    public bool? ControllerAltitude { get; init; }
}
