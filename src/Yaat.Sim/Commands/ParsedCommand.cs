using Yaat.Sim.Phases;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Commands;

public readonly struct ParseResult<T>
    where T : class
{
    public T? Value { get; }
    public string? Reason { get; }
    public bool IsSuccess => Value is not null;

    private ParseResult(T? value, string? reason)
    {
        Value = value;
        Reason = reason;
    }

    public static ParseResult<T> Ok(T value) => new(value, null);

    public static ParseResult<T> Fail(string reason) => new(null, reason);
}

public abstract record ParsedCommand;

public record FlyHeadingCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record TurnLeftCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record TurnRightCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record LeftTurnCommand(int Degrees) : ParsedCommand;

public record RightTurnCommand(int Degrees) : ParsedCommand;

public record FlyPresentHeadingCommand : ParsedCommand;

public record ClimbMaintainCommand(int Altitude) : ParsedCommand;

public record DescendMaintainCommand(int Altitude) : ParsedCommand;

public enum SpeedModifier
{
    None,
    Floor,
    Ceiling,
}

public record SpeedCommand(int Speed, SpeedModifier Modifier = SpeedModifier.None) : ParsedCommand;

public record ResumeNormalSpeedCommand : ParsedCommand;

public record ReduceToFinalApproachSpeedCommand : ParsedCommand;

public record DeleteSpeedRestrictionsCommand : ParsedCommand;

public record ExpediteCommand(int? UntilAltitude = null) : ParsedCommand;

public record NormalRateCommand : ParsedCommand;

public record MachCommand(double MachNumber) : ParsedCommand;

public record ForceHeadingCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record ForceAltitudeCommand(int Altitude) : ParsedCommand;

public record ForceSpeedCommand(int Speed) : ParsedCommand;

public record WarpCommand(string PositionLabel, double Latitude, double Longitude, MagneticHeading MagneticHeading, int Altitude, int Speed)
    : ParsedCommand;

public record WarpGroundCommand(string Taxiway1, string Taxiway2, int? NodeId = null, string? ParkingName = null) : ParsedCommand;

public record SquawkCommand(uint Code) : ParsedCommand;

public record SquawkResetCommand : ParsedCommand;

public record SquawkVfrCommand : ParsedCommand;

public record SquawkNormalCommand : ParsedCommand;

public record SquawkStandbyCommand : ParsedCommand;

public record IdentCommand : ParsedCommand;

public record RandomSquawkCommand : ParsedCommand;

public record SquawkAllCommand : ParsedCommand;

public record SquawkNormalAllCommand : ParsedCommand;

public record SquawkStandbyAllCommand : ParsedCommand;

public record DirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record ForceDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record AppendDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record AppendForceDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record TurnLeftDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record TurnRightDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record ExpectApproachCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record ResolvedFix(string Name, double Lat, double Lon);

public record SayCommand(string Text) : ParsedCommand;

public record SaySpeedCommand : ParsedCommand;

public record SayMachCommand : ParsedCommand;

public record SayExpectedApproachCommand : ParsedCommand;

public record SayAltitudeCommand : ParsedCommand;

public record SayHeadingCommand : ParsedCommand;

public record SayPositionCommand : ParsedCommand;

public record UnsupportedCommand(string RawText) : ParsedCommand;

// Departure instruction hierarchy for CTO commands
public abstract record DepartureInstruction
{
    public abstract DepartureInstructionDto ToSnapshot();

    public static DepartureInstruction FromSnapshot(DepartureInstructionDto dto) =>
        dto switch
        {
            RunwayHeadingDepartureDto => new RunwayHeadingDeparture(),
            RelativeTurnDepartureDto rt => new RelativeTurnDeparture(rt.Degrees, (TurnDirection)rt.Direction),
            FlyHeadingDepartureDto fh => new FlyHeadingDeparture(
                new MagneticHeading(fh.MagneticHeadingDeg),
                fh.Direction.HasValue ? (TurnDirection)fh.Direction.Value : null
            ),
            OnCourseDepartureDto => new OnCourseDeparture(),
            DirectFixDepartureDto df => new DirectFixDeparture(
                df.FixName,
                df.Lat,
                df.Lon,
                df.Direction.HasValue ? (TurnDirection)df.Direction.Value : null
            ),
            DefaultDepartureDto => new DefaultDeparture(),
            _ => new DefaultDeparture(),
        };
}

/// <summary>VFR: fly runway heading. IFR: navigate to first route fix.</summary>
public record DefaultDeparture : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new DefaultDepartureDto();
}

/// <summary>Fly runway heading (explicit instruction).</summary>
public record RunwayHeadingDeparture : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new RunwayHeadingDepartureDto();
}

/// <summary>Turn a relative number of degrees after takeoff (e.g., crosswind = 90°).</summary>
public record RelativeTurnDeparture(int Degrees, TurnDirection Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new RelativeTurnDepartureDto { Degrees = Degrees, Direction = (int)Direction };
}

/// <summary>Fly a specific heading after takeoff, with optional turn direction.</summary>
public record FlyHeadingDeparture(MagneticHeading MagneticHeading, TurnDirection? Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() =>
        new FlyHeadingDepartureDto { MagneticHeadingDeg = MagneticHeading.Degrees, Direction = Direction.HasValue ? (int)Direction.Value : null };
}

/// <summary>On course: fly direct to destination airport.</summary>
public record OnCourseDeparture : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new OnCourseDepartureDto();
}

/// <summary>Direct to a named fix after takeoff, with optional turn direction preference.</summary>
public record DirectFixDeparture(string FixName, double Lat, double Lon, TurnDirection? Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() =>
        new DirectFixDepartureDto
        {
            FixName = FixName,
            Lat = Lat,
            Lon = Lon,
            Direction = Direction.HasValue ? (int)Direction.Value : null,
        };
}

/// <summary>Closed traffic: re-enter the pattern after takeoff.</summary>
/// <param name="Direction">Left or right traffic pattern.</param>
/// <param name="RunwayId">Optional runway for the pattern (cross-runway ops). When null, uses the takeoff runway.</param>
public record ClosedTrafficDeparture(PatternDirection Direction, string? RunwayId = null) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new DefaultDepartureDto();
}

// Tower commands
public record LineUpAndWaitCommand : ParsedCommand;

/// <summary>
/// CTO with departure instruction and optional altitude override.
/// </summary>
public record ClearedForTakeoffCommand(DepartureInstruction Departure, int? AssignedAltitude = null) : ParsedCommand;

public record CancelTakeoffClearanceCommand : ParsedCommand;

public record GoAroundCommand(MagneticHeading? AssignedMagneticHeading, int? TargetAltitude, PatternDirection? TrafficPattern) : ParsedCommand;

public record ClearedToLandCommand(bool NoDelete = false) : ParsedCommand;

public record LandAndHoldShortCommand(string CrossingRunwayId) : ParsedCommand;

public record CancelLandingClearanceCommand : ParsedCommand;

// Pattern commands
public record EnterLeftDownwindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterRightDownwindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterLeftCrosswindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterRightCrosswindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterLeftBaseCommand(string? RunwayId = null, double? FinalDistanceNm = null) : ParsedCommand;

public record EnterRightBaseCommand(string? RunwayId = null, double? FinalDistanceNm = null) : ParsedCommand;

public record EnterFinalCommand(string? RunwayId = null) : ParsedCommand;

public record MakeLeftTrafficCommand(string? RunwayId = null) : ParsedCommand;

public record MakeRightTrafficCommand(string? RunwayId = null) : ParsedCommand;

public record TurnCrosswindCommand : ParsedCommand;

public record TurnDownwindCommand : ParsedCommand;

public record TurnBaseCommand : ParsedCommand;

public record ExtendDownwindCommand : ParsedCommand;

public record MakeShortApproachCommand : ParsedCommand;

public record MakeLeft360Command : ParsedCommand;

public record MakeRight360Command : ParsedCommand;

public record MakeLeft270Command : ParsedCommand;

public record MakeRight270Command : ParsedCommand;

public record PatternSizeCommand(double SizeNm) : ParsedCommand;

public record MakeNormalApproachCommand : ParsedCommand;

public record Cancel270Command : ParsedCommand;

public record MakeLeftSTurnsCommand(int Count = 2) : ParsedCommand;

public record MakeRightSTurnsCommand(int Count = 2) : ParsedCommand;

public record Plan270Command : ParsedCommand;

public record CircleAirportCommand : ParsedCommand;

// Option approach / special ops commands
public record TouchAndGoCommand(string? RunwayId, PatternDirection? TrafficPattern) : ParsedCommand;

public record StopAndGoCommand(PatternDirection? TrafficPattern) : ParsedCommand;

public record LowApproachCommand(PatternDirection? TrafficPattern) : ParsedCommand;

public record ClearedForOptionCommand(PatternDirection? TrafficPattern) : ParsedCommand;

// Hold commands

/// <summary>HPPL / HPPR: 360-degree orbits at present position.</summary>
public record HoldPresentPosition360Command(TurnDirection Direction) : ParsedCommand;

/// <summary>HPP: helicopter hover at present position.</summary>
public record HoldPresentPositionHoverCommand : ParsedCommand;

/// <summary>HFIXL / HFIXR: fly to fix, then orbit with 360-degree turns.</summary>
public record HoldAtFixOrbitCommand(string FixName, double Lat, double Lon, TurnDirection Direction) : ParsedCommand;

/// <summary>HFIX: helicopter fly to fix and hover.</summary>
public record HoldAtFixHoverCommand(string FixName, double Lat, double Lon) : ParsedCommand;

// Helicopter commands
public record AirTaxiCommand(string? Destination) : ParsedCommand;

public record LandCommand(string SpotName, bool NoDelete = false, bool IsTaxiway = false) : ParsedCommand;

public record ClearedTakeoffPresentCommand : ParsedCommand;

// Ground commands
public record PushbackCommand(
    MagneticHeading? MagneticHeading,
    string? Taxiway,
    string? FacingTaxiway,
    string? DestinationParking,
    string? DestinationSpot
) : ParsedCommand;

public record TaxiCommand(
    List<string> Path,
    List<string> HoldShorts,
    string? DestinationRunway = null,
    bool NoDelete = false,
    string? DestinationParking = null,
    List<string>? CrossRunways = null,
    string? DestinationSpot = null
) : ParsedCommand;

public record HoldPositionCommand : ParsedCommand;

public record ResumeCommand : ParsedCommand;

public record CrossRunwayCommand(string RunwayId) : ParsedCommand;

public record HoldShortCommand(string Target) : ParsedCommand;

public record AssignRunwayCommand(string RunwayId) : ParsedCommand;

public record FollowCommand(string TargetCallsign) : ParsedCommand;

public record FollowGroundCommand(string TargetCallsign) : ParsedCommand;

public record GiveWayCommand(string TargetCallsign, string? Location = null) : ParsedCommand;

public record TaxiAllCommand(string? DestinationRunway = null, string? DestinationParking = null, string? DestinationSpot = null) : ParsedCommand;

public record BreakConflictCommand : ParsedCommand;

public record GoCommand : ParsedCommand;

// Exit commands
public record ExitLeftCommand(bool NoDelete = false, string? Taxiway = null) : ParsedCommand;

public record ExitRightCommand(bool NoDelete = false, string? Taxiway = null) : ParsedCommand;

public record ExitTaxiwayCommand(string Taxiway, bool NoDelete = false) : ParsedCommand;

/// <summary>
/// A compound command consisting of sequential blocks,
/// each containing parallel commands and an optional trigger.
/// </summary>
public record WaitCommand(double Seconds) : ParsedCommand;

public record WaitDistanceCommand(double DistanceNm) : ParsedCommand;

public record CompoundCommand(List<ParsedBlock> Blocks)
{
    /// <summary>
    /// Original canonical command text that produced this compound command.
    /// Set by the parser or caller for snapshot serialization of CommandQueue.
    /// </summary>
    public string? SourceText { get; init; }
}

public record ParsedBlock(BlockCondition? Condition, List<ParsedCommand> Commands);

public abstract record BlockCondition;

public record LevelCondition(int Altitude) : BlockCondition;

public record AtFixCondition(string FixName, double Lat, double Lon, int? Radial = null, int? Distance = null) : BlockCondition;

public record GiveWayCondition(string TargetCallsign) : BlockCondition;

public record DistanceFinalCondition(double DistanceNm) : BlockCondition;

public record OnHandoffCondition : BlockCondition;

// Track operations commands
public record SetActivePositionCommand(string TcpCode) : ParsedCommand;

public record TrackAircraftCommand(string? TcpCode = null) : ParsedCommand;

public record DropTrackCommand : ParsedCommand;

public record InitiateHandoffCommand(string? TcpCode) : ParsedCommand;

public record ForceHandoffCommand(string TcpCode) : ParsedCommand;

public record AcceptHandoffCommand(string? Callsign = null) : ParsedCommand;

public record CancelHandoffCommand : ParsedCommand;

public record AcceptAllHandoffsCommand : ParsedCommand;

public record InitiateHandoffAllCommand(string TcpCode) : ParsedCommand;

public record PointOutCommand(string? TcpCode = null) : ParsedCommand;

public record AcknowledgeCommand : ParsedCommand;

public record StripAnnotateCommand(int Box, string? Text) : ParsedCommand;

public record StripPushCommand(string BayName) : ParsedCommand;

public record Scratchpad1Command(string Text) : ParsedCommand;

public record Scratchpad2Command(string Text) : ParsedCommand;

public record TemporaryAltitudeCommand(int AltitudeHundreds) : ParsedCommand;

public record CruiseCommand(int AltitudeHundreds) : ParsedCommand;

public record OnHandoffCommand : ParsedCommand;

// Coordination commands
public record CoordinationReleaseCommand(string? ListId) : ParsedCommand;

public record CoordinationHoldCommand(string? ListId, string? Text) : ParsedCommand;

public record CoordinationRecallCommand(string? ListId) : ParsedCommand;

public record CoordinationAcknowledgeCommand(string? ListId) : ParsedCommand;

public record CoordinationAutoAckCommand(string ListId) : ParsedCommand;

// Approach commands

public enum CrossFixAltitudeType
{
    At,
    AtOrAbove,
    AtOrBelow,
}

public enum HoldingEntry
{
    Direct,
    Teardrop,
    Parallel,
}

public record ClearedApproachCommand(
    string? ApproachId,
    string? AirportCode,
    bool Force,
    string? AtFix,
    double? AtFixLat,
    double? AtFixLon,
    string? DctFix,
    double? DctFixLat,
    double? DctFixLon,
    int? CrossFixAltitude,
    CrossFixAltitudeType? CrossFixAltType
) : ParsedCommand;

public record JoinApproachCommand(string ApproachId, string? AirportCode, bool Force) : ParsedCommand;

public record ClearedApproachStraightInCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record JoinApproachStraightInCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record JoinFinalApproachCourseCommand(string? ApproachId) : ParsedCommand;

public record JoinStarCommand(string StarId, string? Transition) : ParsedCommand;

public record JoinAirwayCommand(string AirwayId) : ParsedCommand;

public record JoinRadialOutboundCommand(string FixName, double FixLat, double FixLon, int Radial) : ParsedCommand;

public record JoinRadialInboundCommand(string FixName, double FixLat, double FixLon, int Radial) : ParsedCommand;

public record HoldingPatternCommand(
    string FixName,
    double FixLat,
    double FixLon,
    int InboundCourse,
    double LegLength,
    bool IsMinuteBased,
    TurnDirection Direction,
    HoldingEntry? Entry
) : ParsedCommand;

public record PositionTurnAltitudeClearanceCommand(MagneticHeading? MagneticHeading, int? Altitude, string? ApproachId) : ParsedCommand;

public record ClimbViaCommand(int? Altitude) : ParsedCommand;

public record DescendViaCommand(int? Altitude, int? Speed = null, string? SpeedFixName = null, double? SpeedFixLat = null, double? SpeedFixLon = null)
    : ParsedCommand;

public record CrossFixCommand(string FixName, double FixLat, double FixLon, int Altitude, CrossFixAltitudeType AltType, int? Speed) : ParsedCommand;

public record ConstrainedFixAltitude(int AltitudeFt, CrossFixAltitudeType AltType);

public record ConstrainedForceDirectToCommand(
    List<ResolvedFix> Fixes,
    Dictionary<int, ConstrainedFixAltitude> AltitudeConstraints,
    Dictionary<int, int>? SpeedConstraints,
    List<string>? SkippedFixes
) : ParsedCommand;

public record DepartFixCommand(string FixName, double FixLat, double FixLon, MagneticHeading MagneticHeading) : ParsedCommand;

public record ListApproachesCommand(string? AirportCode) : ParsedCommand;

public record ClearedVisualApproachCommand(string RunwayId, string? AirportCode, PatternDirection? TrafficDirection, string? FollowCallsign)
    : ParsedCommand;

public record ReportFieldInSightCommand : ParsedCommand;

public record ReportFieldInSightForcedCommand : ParsedCommand;

public record ReportTrafficInSightCommand(string? TargetCallsign) : ParsedCommand;

public record ReportTrafficInSightForcedCommand(string? TargetCallsign) : ParsedCommand;

// Queue meta-commands
public record DeleteQueuedCommand(int? BlockNumber = null) : ParsedCommand;

public record ShowQueuedCommand : ParsedCommand;

// Flight plan amendment commands
public record ChangeDestinationCommand(string Airport) : ParsedCommand;

public record CreateFlightPlanCommand(string FlightRules, string AircraftType, int CruiseAltitude, string Route) : ParsedCommand;

public record CreateAbbreviatedFlightPlanCommand(
    uint? BeaconCode,
    string? Scratchpad1,
    string? Scratchpad2,
    string? AircraftType,
    int? CruiseAltitude,
    string FlightRules
) : ParsedCommand;

public record SetRemarksCommand(string Text) : ParsedCommand;

// Server/global commands
public record DeleteCommand : ParsedCommand;

public record PauseCommand : ParsedCommand;

public record UnpauseCommand : ParsedCommand;

public record SimRateCommand(int Rate) : ParsedCommand;

public record SpawnNowCommand : ParsedCommand;

public record SpawnDelayCommand(int Seconds) : ParsedCommand;

public record AddAircraftCommand(string Args) : ParsedCommand;

public record ConsolidateCommand(string ReceivingTcpCode, string SendingTcpCode, bool Full) : ParsedCommand;

public record DeconsolidateCommand(string TcpCode) : ParsedCommand;
