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

public record ResolvedFix(string Name, double Lat, double Lon);

public record UnsupportedCommand(string RawText) : ParsedCommand;

// Tower commands
public record LineUpAndWaitCommand : ParsedCommand;

/// <summary>
/// CTO [hdg]: cleared for takeoff, optionally with assigned heading.
/// TurnDirection is set by CTOR/CTOL variants.
/// TrafficPattern is set by CTOMLT/CTOMRT to establish pattern mode.
/// </summary>
public record ClearedForTakeoffCommand(
    int? AssignedHeading, TurnDirection? Turn, PatternDirection? TrafficPattern = null) : ParsedCommand;

public record CancelTakeoffClearanceCommand : ParsedCommand;

public record GoAroundCommand(
    int? AssignedHeading = null, int? TargetAltitude = null) : ParsedCommand;

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
public record HoldAtFixOrbitCommand(
    string FixName, double Lat, double Lon, TurnDirection Direction) : ParsedCommand;

/// <summary>HFIX: helicopter fly to fix and hover.</summary>
public record HoldAtFixHoverCommand(
    string FixName, double Lat, double Lon) : ParsedCommand;

// Ground commands
public record PushbackCommand(int? Heading = null, string? Taxiway = null) : ParsedCommand;

public record TaxiCommand(
    List<string> Path, List<string> HoldShorts,
    string? DestinationRunway = null, bool NoDelete = false) : ParsedCommand;

public record HoldPositionCommand : ParsedCommand;

public record ResumeCommand : ParsedCommand;

public record CrossRunwayCommand(string RunwayId) : ParsedCommand;

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

public record AtFixCondition(
    string FixName, double Lat, double Lon,
    int? Radial = null, int? Distance = null) : BlockCondition;

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
