using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

public abstract record ParsedCommand;

public record FlyHeadingCommand(int Heading) : ParsedCommand;

public record TurnLeftCommand(int Heading) : ParsedCommand;

public record TurnRightCommand(int Heading) : ParsedCommand;

public record LeftTurnCommand(int Degrees) : ParsedCommand;

public record RightTurnCommand(int Degrees) : ParsedCommand;

public record FlyPresentHeadingCommand : ParsedCommand;

public record ClimbMaintainCommand(int Altitude) : ParsedCommand;

public record DescendMaintainCommand(int Altitude) : ParsedCommand;

public record SpeedCommand(int Speed) : ParsedCommand;

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

public record DirectToCommand(List<ResolvedFix> Fixes) : ParsedCommand;

public record ForceDirectToCommand(List<ResolvedFix> Fixes) : ParsedCommand;

public record AppendDirectToCommand(List<ResolvedFix> Fixes) : ParsedCommand;

public record ExpectApproachCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record ResolvedFix(string Name, double Lat, double Lon);

public record SayCommand(string Text) : ParsedCommand;

public record UnsupportedCommand(string RawText) : ParsedCommand;

// Departure instruction hierarchy for CTO commands
public abstract record DepartureInstruction;

/// <summary>VFR: fly runway heading. IFR: navigate to first route fix.</summary>
public record DefaultDeparture : DepartureInstruction;

/// <summary>Fly runway heading (explicit instruction).</summary>
public record RunwayHeadingDeparture : DepartureInstruction;

/// <summary>Turn a relative number of degrees after takeoff (e.g., crosswind = 90°).</summary>
public record RelativeTurnDeparture(int Degrees, TurnDirection Direction) : DepartureInstruction;

/// <summary>Fly a specific heading after takeoff, with optional turn direction.</summary>
public record FlyHeadingDeparture(int Heading, TurnDirection? Direction) : DepartureInstruction;

/// <summary>On course: fly direct to destination airport.</summary>
public record OnCourseDeparture : DepartureInstruction;

/// <summary>Direct to a named fix after takeoff.</summary>
public record DirectFixDeparture(string FixName, double Lat, double Lon) : DepartureInstruction;

/// <summary>Closed traffic: re-enter the pattern after takeoff.</summary>
public record ClosedTrafficDeparture(PatternDirection Direction) : DepartureInstruction;

// Tower commands
public record LineUpAndWaitCommand : ParsedCommand;

/// <summary>
/// CTO with departure instruction and optional altitude override.
/// </summary>
public record ClearedForTakeoffCommand(DepartureInstruction Departure, int? AssignedAltitude = null) : ParsedCommand;

public record CancelTakeoffClearanceCommand : ParsedCommand;

public record GoAroundCommand(int? AssignedHeading = null, int? TargetAltitude = null, PatternDirection? TrafficPattern = null) : ParsedCommand;

public record ClearedToLandCommand(bool NoDelete = false) : ParsedCommand;

public record CancelLandingClearanceCommand : ParsedCommand;

// Pattern commands
public record EnterLeftDownwindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterRightDownwindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterLeftBaseCommand(string? RunwayId = null, double? FinalDistanceNm = null) : ParsedCommand;

public record EnterRightBaseCommand(string? RunwayId = null, double? FinalDistanceNm = null) : ParsedCommand;

public record EnterFinalCommand(string? RunwayId = null) : ParsedCommand;

public record MakeLeftTrafficCommand : ParsedCommand;

public record MakeRightTrafficCommand : ParsedCommand;

public record TurnCrosswindCommand : ParsedCommand;

public record TurnDownwindCommand : ParsedCommand;

public record TurnBaseCommand : ParsedCommand;

public record ExtendDownwindCommand : ParsedCommand;

public record MakeShortApproachCommand : ParsedCommand;

public record MakeLeft360Command : ParsedCommand;

public record MakeRight360Command : ParsedCommand;

public record MakeLeft270Command : ParsedCommand;

public record MakeRight270Command : ParsedCommand;

public record CircleAirportCommand : ParsedCommand;

public record SequenceCommand(int Number, string? FollowCallsign) : ParsedCommand;

// Option approach / special ops commands
public record TouchAndGoCommand : ParsedCommand;

public record StopAndGoCommand : ParsedCommand;

public record LowApproachCommand : ParsedCommand;

public record ClearedForOptionCommand : ParsedCommand;

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

public record LandCommand(string SpotName, bool NoDelete = false) : ParsedCommand;

public record ClearedTakeoffPresentCommand : ParsedCommand;

// Ground commands
public record PushbackCommand(int? Heading = null, string? Taxiway = null) : ParsedCommand;

public record TaxiCommand(List<string> Path, List<string> HoldShorts, string? DestinationRunway = null, bool NoDelete = false) : ParsedCommand;

public record HoldPositionCommand : ParsedCommand;

public record ResumeCommand : ParsedCommand;

public record CrossRunwayCommand(string RunwayId) : ParsedCommand;

public record HoldShortCommand(string Target) : ParsedCommand;

public record FollowCommand(string TargetCallsign) : ParsedCommand;

// Exit commands
public record ExitLeftCommand(bool NoDelete = false) : ParsedCommand;

public record ExitRightCommand(bool NoDelete = false) : ParsedCommand;

public record ExitTaxiwayCommand(string Taxiway, bool NoDelete = false) : ParsedCommand;

/// <summary>
/// A compound command consisting of sequential blocks,
/// each containing parallel commands and an optional trigger.
/// </summary>
public record WaitCommand(double Seconds) : ParsedCommand;

public record WaitDistanceCommand(double DistanceNm) : ParsedCommand;

public record CompoundCommand(List<ParsedBlock> Blocks);

public record ParsedBlock(BlockCondition? Condition, List<ParsedCommand> Commands);

public abstract record BlockCondition;

public record LevelCondition(int Altitude) : BlockCondition;

public record AtFixCondition(string FixName, double Lat, double Lon, int? Radial = null, int? Distance = null) : BlockCondition;

public record GiveWayCondition(string TargetCallsign) : BlockCondition;

// Track operations commands
public record SetActivePositionCommand(string TcpCode) : ParsedCommand;

public record TrackAircraftCommand : ParsedCommand;

public record DropTrackCommand : ParsedCommand;

public record InitiateHandoffCommand(string TcpCode) : ParsedCommand;

public record AcceptHandoffCommand : ParsedCommand;

public record CancelHandoffCommand : ParsedCommand;

public record AcceptAllHandoffsCommand : ParsedCommand;

public record InitiateHandoffAllCommand(string TcpCode) : ParsedCommand;

public record PointOutCommand(string TcpCode) : ParsedCommand;

public record AcknowledgeCommand : ParsedCommand;

public record AnnotateCommand : ParsedCommand;

public record ScratchpadCommand(string Text) : ParsedCommand;

public record TemporaryAltitudeCommand(int AltitudeHundreds) : ParsedCommand;

public record CruiseCommand(int AltitudeHundreds) : ParsedCommand;

public record OnHandoffCommand : ParsedCommand;

public record FrequencyChangeCommand : ParsedCommand;

public record ContactTcpCommand(string TcpCode) : ParsedCommand;

public record ContactTowerCommand : ParsedCommand;

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
    string ApproachId,
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

public record JoinFinalApproachCourseCommand(string ApproachId) : ParsedCommand;

public record JoinStarCommand(string StarId, string? Transition) : ParsedCommand;

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

public record PositionTurnAltitudeClearanceCommand(int Heading, int Altitude, string ApproachId) : ParsedCommand;

public record ClimbViaCommand(int? Altitude) : ParsedCommand;

public record DescendViaCommand(int? Altitude) : ParsedCommand;

public record CrossFixCommand(string FixName, double FixLat, double FixLon, int Altitude, CrossFixAltitudeType AltType, int? Speed) : ParsedCommand;

public record DepartFixCommand(string FixName, double FixLat, double FixLon, int Heading) : ParsedCommand;

public record ListApproachesCommand(string? AirportCode) : ParsedCommand;

public record ClearedVisualApproachCommand(string RunwayId, string? AirportCode, PatternDirection? TrafficDirection, string? FollowCallsign)
    : ParsedCommand;

public record ReportFieldInSightCommand : ParsedCommand;

public record ReportTrafficInSightCommand(string? TargetCallsign) : ParsedCommand;
