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
        new(CanonicalCommandType.FlyHeading, "Fly Heading", "270", false),
        new(CanonicalCommandType.TurnLeft, "Turn Left", "270", false),
        new(CanonicalCommandType.TurnRight, "Turn Right", "090", false),
        new(CanonicalCommandType.RelativeLeft, "Relative Left", "20", false),
        new(CanonicalCommandType.RelativeRight, "Relative Right", "30", false),
        new(CanonicalCommandType.FlyPresentHeading, "Fly Present Heading", null, false),
        new(CanonicalCommandType.ClimbMaintain, "Climb/Maintain", "240", false),
        new(CanonicalCommandType.DescendMaintain, "Descend/Maintain", "50", false),
        new(CanonicalCommandType.Speed, "Speed", "250", false),
        new(CanonicalCommandType.Squawk, "Squawk", "1234", false),
        new(CanonicalCommandType.DirectTo, "Direct To", "FIX", false),
        new(CanonicalCommandType.Delete, "Delete", null, false),
        new(CanonicalCommandType.Pause, "Pause", null, true),
        new(CanonicalCommandType.Unpause, "Unpause", null, true),
        new(CanonicalCommandType.SimRate, "Sim Rate", "2", true),
    ];
}
