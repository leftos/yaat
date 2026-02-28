using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public static class CommandMetadata
{
    public record CommandInfo(
        CanonicalCommandType Type,
        string Label,
        string? SampleArg,
        bool IsGlobal
    );

    public static readonly IReadOnlyList<CommandInfo> AllCommands =
    [
        // Heading
        new(CanonicalCommandType.FlyHeading, "Fly Heading", "270", false),
        new(CanonicalCommandType.TurnLeft, "Turn Left", "270", false),
        new(CanonicalCommandType.TurnRight, "Turn Right", "090", false),
        new(CanonicalCommandType.RelativeLeft, "Relative Left", "20", false),
        new(CanonicalCommandType.RelativeRight, "Relative Right", "30", false),
        new(CanonicalCommandType.FlyPresentHeading, "Fly Present Heading", null, false),
        // Altitude / Speed
        new(CanonicalCommandType.ClimbMaintain, "Climb/Maintain", "240", false),
        new(CanonicalCommandType.DescendMaintain, "Descend/Maintain", "50", false),
        new(CanonicalCommandType.Speed, "Speed", "250", false),
        // Transponder
        new(CanonicalCommandType.Squawk, "Squawk", "1234", false),
        new(CanonicalCommandType.SquawkIdent, "Squawk Ident", null, false),
        new(CanonicalCommandType.SquawkVfr, "Squawk VFR", null, false),
        new(CanonicalCommandType.SquawkNormal, "Squawk Normal", null, false),
        new(CanonicalCommandType.SquawkStandby, "Squawk Standby", null, false),
        new(CanonicalCommandType.Ident, "Ident", null, false),
        // Navigation
        new(CanonicalCommandType.DirectTo, "Direct To", "FIX", false),
        // Tower
        new(CanonicalCommandType.LineUpAndWait, "Line Up and Wait", null, false),
        new(CanonicalCommandType.ClearedForTakeoff, "Cleared for Takeoff", null, false),
        new(CanonicalCommandType.CancelTakeoffClearance, "Cancel Takeoff Clearance", null, false),
        new(CanonicalCommandType.GoAround, "Go Around", null, false),
        new(CanonicalCommandType.ClearedToLand, "Cleared to Land", null, false),
        new(CanonicalCommandType.CancelLandingClearance, "Cancel Landing Clearance", null, false),
        new(CanonicalCommandType.TouchAndGo, "Touch and Go", null, false),
        new(CanonicalCommandType.StopAndGo, "Stop and Go", null, false),
        new(CanonicalCommandType.LowApproach, "Low Approach", null, false),
        new(CanonicalCommandType.ClearedForOption, "Cleared for the Option", null, false),
        // Pattern
        new(CanonicalCommandType.EnterLeftDownwind, "Enter Left Downwind", "28R", false),
        new(CanonicalCommandType.EnterRightDownwind, "Enter Right Downwind", "28R", false),
        new(CanonicalCommandType.EnterLeftBase, "Enter Left Base", "28R 3", false),
        new(CanonicalCommandType.EnterRightBase, "Enter Right Base", "28R 3", false),
        new(CanonicalCommandType.EnterFinal, "Enter Final", "28R", false),
        new(CanonicalCommandType.MakeLeftTraffic, "Make Left Traffic", null, false),
        new(CanonicalCommandType.MakeRightTraffic, "Make Right Traffic", null, false),
        new(CanonicalCommandType.TurnCrosswind, "Turn Crosswind", null, false),
        new(CanonicalCommandType.TurnDownwind, "Turn Downwind", null, false),
        new(CanonicalCommandType.TurnBase, "Turn Base", null, false),
        new(CanonicalCommandType.ExtendDownwind, "Extend Downwind", null, false),
        // Hold
        new(CanonicalCommandType.HoldPresentPosition360Left, "Hold (360 Left)", null, false),
        new(CanonicalCommandType.HoldPresentPosition360Right, "Hold (360 Right)", null, false),
        new(CanonicalCommandType.HoldPresentPositionHover, "Hold Present Position", null, false),
        new(CanonicalCommandType.HoldAtFixLeft, "Hold at Fix (Left)", "FIX", false),
        new(CanonicalCommandType.HoldAtFixRight, "Hold at Fix (Right)", "FIX", false),
        new(CanonicalCommandType.HoldAtFixHover, "Hold at Fix", "FIX", false),
        // Sim control
        new(CanonicalCommandType.Delete, "Delete", null, false),
        new(CanonicalCommandType.Pause, "Pause", null, true),
        new(CanonicalCommandType.Unpause, "Unpause", null, true),
        new(CanonicalCommandType.SimRate, "Sim Rate", "2", true),
        new(CanonicalCommandType.Wait, "Wait (seconds)", "30", false),
        new(CanonicalCommandType.WaitDistance, "Wait (distance)", "4", false),
        new(CanonicalCommandType.Add, "Add Aircraft", "IFR H J -270 15 10000", true),
        new(CanonicalCommandType.SpawnNow, "Spawn Now", null, false),
        new(CanonicalCommandType.SpawnDelay, "Set Spawn Delay", "120", false),
    ];
}
