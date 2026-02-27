using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public class CommandPattern
{
    public required string Verb { get; init; }
    public required string Format { get; init; }
}

public class CommandScheme
{
    public required string Name { get; init; }

    public required Dictionary<CanonicalCommandType, CommandPattern>
        Patterns { get; init; }

    public static CommandScheme AtcTrainer()
    {
        return new CommandScheme
        {
            Name = "ATCTrainer",
            Patterns = new Dictionary<CanonicalCommandType, CommandPattern>
            {
                [CanonicalCommandType.FlyHeading] = new()
                    { Verb = "FH", Format = "{verb} {arg}" },
                [CanonicalCommandType.TurnLeft] = new()
                    { Verb = "TL", Format = "{verb} {arg}" },
                [CanonicalCommandType.TurnRight] = new()
                    { Verb = "TR", Format = "{verb} {arg}" },
                [CanonicalCommandType.RelativeLeft] = new()
                    { Verb = "LT", Format = "{verb} {arg}" },
                [CanonicalCommandType.RelativeRight] = new()
                    { Verb = "RT", Format = "{verb} {arg}" },
                [CanonicalCommandType.FlyPresentHeading] = new()
                    { Verb = "FPH", Format = "{verb}" },
                [CanonicalCommandType.ClimbMaintain] = new()
                    { Verb = "CM", Format = "{verb} {arg}" },
                [CanonicalCommandType.DescendMaintain] = new()
                    { Verb = "DM", Format = "{verb} {arg}" },
                [CanonicalCommandType.Speed] = new()
                    { Verb = "SPD", Format = "{verb} {arg}" },
                [CanonicalCommandType.Squawk] = new()
                    { Verb = "SQ", Format = "{verb} {arg}" },
                [CanonicalCommandType.Delete] = new()
                    { Verb = "DEL", Format = "{verb}" },
                [CanonicalCommandType.Pause] = new()
                    { Verb = "PAUSE", Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new()
                    { Verb = "UNPAUSE", Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new()
                    { Verb = "SIMRATE", Format = "{verb} {arg}" },
            }
        };
    }

    public static CommandScheme Vice()
    {
        return new CommandScheme
        {
            Name = "VICE",
            Patterns = new Dictionary<CanonicalCommandType, CommandPattern>
            {
                [CanonicalCommandType.FlyHeading] = new()
                    { Verb = "H", Format = "{verb}{arg}" },
                [CanonicalCommandType.TurnLeft] = new()
                    { Verb = "L", Format = "{verb}{arg}" },
                [CanonicalCommandType.TurnRight] = new()
                    { Verb = "R", Format = "{verb}{arg}" },
                [CanonicalCommandType.RelativeLeft] = new()
                    { Verb = "T", Format = "{verb}{arg}L" },
                [CanonicalCommandType.RelativeRight] = new()
                    { Verb = "T", Format = "{verb}{arg}R" },
                [CanonicalCommandType.FlyPresentHeading] = new()
                    { Verb = "H", Format = "{verb}" },
                [CanonicalCommandType.ClimbMaintain] = new()
                    { Verb = "C", Format = "{verb}{arg}" },
                [CanonicalCommandType.DescendMaintain] = new()
                    { Verb = "D", Format = "{verb}{arg}" },
                [CanonicalCommandType.Speed] = new()
                    { Verb = "S", Format = "{verb}{arg}" },
                [CanonicalCommandType.Squawk] = new()
                    { Verb = "SQ", Format = "{verb}{arg}" },
                [CanonicalCommandType.Delete] = new()
                    { Verb = "X", Format = "{verb}" },
                [CanonicalCommandType.Pause] = new()
                    { Verb = "PAUSE", Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new()
                    { Verb = "UNPAUSE", Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new()
                    { Verb = "SIMRATE", Format = "{verb} {arg}" },
            }
        };
    }
}
