using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public class CommandPattern
{
    public required string Verb { get; set; }
    public required string Format { get; init; }
}

public class CommandScheme
{
    public required CommandParseMode ParseMode { get; init; }

    public required Dictionary<CanonicalCommandType, CommandPattern> Patterns { get; init; }

    /// <summary>
    /// Returns "ATCTrainer", "VICE", or null if custom.
    /// </summary>
    public static string? DetectPresetName(CommandScheme scheme)
    {
        var atc = AtcTrainer();
        if (MatchesPreset(scheme, atc))
        {
            return "ATCTrainer";
        }

        var vice = Vice();
        if (MatchesPreset(scheme, vice))
        {
            return "VICE";
        }

        return null;
    }

    public static CommandScheme AtcTrainer()
    {
        return new CommandScheme
        {
            ParseMode = CommandParseMode.SpaceSeparated,
            Patterns = new Dictionary<CanonicalCommandType, CommandPattern>
            {
                // Heading
                [CanonicalCommandType.FlyHeading] = new() { Verb = "FH", Format = "{verb} {arg}" },
                [CanonicalCommandType.TurnLeft] = new() { Verb = "TL", Format = "{verb} {arg}" },
                [CanonicalCommandType.TurnRight] = new() { Verb = "TR", Format = "{verb} {arg}" },
                [CanonicalCommandType.RelativeLeft] = new() { Verb = "LT", Format = "{verb} {arg}" },
                [CanonicalCommandType.RelativeRight] = new() { Verb = "RT", Format = "{verb} {arg}" },
                [CanonicalCommandType.FlyPresentHeading] = new() { Verb = "FPH", Format = "{verb}" },
                // Altitude / Speed
                [CanonicalCommandType.ClimbMaintain] = new() { Verb = "CM", Format = "{verb} {arg}" },
                [CanonicalCommandType.DescendMaintain] = new() { Verb = "DM", Format = "{verb} {arg}" },
                [CanonicalCommandType.Speed] = new() { Verb = "SPD", Format = "{verb} {arg}" },
                // Transponder
                [CanonicalCommandType.Squawk] = new() { Verb = "SQ", Format = "{verb} {arg}" },
                [CanonicalCommandType.SquawkIdent] = new() { Verb = "SQI", Format = "{verb}" },
                [CanonicalCommandType.SquawkVfr] = new() { Verb = "SQVFR", Format = "{verb}" },
                [CanonicalCommandType.SquawkNormal] = new() { Verb = "SQNORM", Format = "{verb}" },
                [CanonicalCommandType.SquawkStandby] = new() { Verb = "SQSBY", Format = "{verb}" },
                [CanonicalCommandType.Ident] = new() { Verb = "IDENT", Format = "{verb}" },
                // Navigation
                [CanonicalCommandType.DirectTo] = new() { Verb = "DCT", Format = "{verb} {arg}" },
                // Tower
                [CanonicalCommandType.LineUpAndWait] = new() { Verb = "LUAW", Format = "{verb}" },
                [CanonicalCommandType.ClearedForTakeoff] = new() { Verb = "CTO", Format = "{verb}" },
                [CanonicalCommandType.CancelTakeoffClearance] = new() { Verb = "CTOC", Format = "{verb}" },
                [CanonicalCommandType.GoAround] = new() { Verb = "GA", Format = "{verb}" },
                [CanonicalCommandType.ClearedToLand] = new() { Verb = "CTL", Format = "{verb}" },
                [CanonicalCommandType.TouchAndGo] = new() { Verb = "TG", Format = "{verb}" },
                [CanonicalCommandType.StopAndGo] = new() { Verb = "SG", Format = "{verb}" },
                [CanonicalCommandType.LowApproach] = new() { Verb = "LA", Format = "{verb}" },
                [CanonicalCommandType.ClearedForOption] = new() { Verb = "COPT", Format = "{verb}" },
                // Pattern
                [CanonicalCommandType.EnterLeftDownwind] = new() { Verb = "ELD", Format = "{verb}" },
                [CanonicalCommandType.EnterRightDownwind] = new() { Verb = "ERD", Format = "{verb}" },
                [CanonicalCommandType.EnterLeftBase] = new() { Verb = "ELB", Format = "{verb}" },
                [CanonicalCommandType.EnterRightBase] = new() { Verb = "ERB", Format = "{verb}" },
                [CanonicalCommandType.EnterFinal] = new() { Verb = "EF", Format = "{verb}" },
                [CanonicalCommandType.MakeLeftTraffic] = new() { Verb = "MLT", Format = "{verb}" },
                [CanonicalCommandType.MakeRightTraffic] = new() { Verb = "MRT", Format = "{verb}" },
                [CanonicalCommandType.TurnCrosswind] = new() { Verb = "TC", Format = "{verb}" },
                [CanonicalCommandType.TurnDownwind] = new() { Verb = "TD", Format = "{verb}" },
                [CanonicalCommandType.TurnBase] = new() { Verb = "TB", Format = "{verb}" },
                [CanonicalCommandType.ExtendDownwind] = new() { Verb = "EXT", Format = "{verb}" },
                // Hold
                [CanonicalCommandType.HoldPresentPosition360Left] = new() { Verb = "HPP360L", Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPosition360Right] = new() { Verb = "HPP360R", Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPositionHover] = new() { Verb = "HPP", Format = "{verb}" },
                [CanonicalCommandType.HoldAtFixLeft] = new() { Verb = "HFIXL", Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixRight] = new() { Verb = "HFIXR", Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixHover] = new() { Verb = "HFIX", Format = "{verb} {arg}" },
                // Sim control
                [CanonicalCommandType.Delete] = new() { Verb = "DEL", Format = "{verb}" },
                [CanonicalCommandType.Pause] = new() { Verb = "PAUSE", Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new() { Verb = "UNPAUSE", Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new() { Verb = "SIMRATE", Format = "{verb} {arg}" },
            },
        };
    }

    public static CommandScheme Vice()
    {
        return new CommandScheme
        {
            ParseMode = CommandParseMode.Concatenated,
            Patterns = new Dictionary<CanonicalCommandType, CommandPattern>
            {
                // Heading
                [CanonicalCommandType.FlyHeading] = new() { Verb = "H", Format = "{verb}{arg}" },
                [CanonicalCommandType.TurnLeft] = new() { Verb = "L", Format = "{verb}{arg}" },
                [CanonicalCommandType.TurnRight] = new() { Verb = "R", Format = "{verb}{arg}" },
                [CanonicalCommandType.RelativeLeft] = new() { Verb = "T", Format = "{verb}{arg}L" },
                [CanonicalCommandType.RelativeRight] = new() { Verb = "T", Format = "{verb}{arg}R" },
                [CanonicalCommandType.FlyPresentHeading] = new() { Verb = "H", Format = "{verb}" },
                // Altitude / Speed
                [CanonicalCommandType.ClimbMaintain] = new() { Verb = "C", Format = "{verb}{arg}" },
                [CanonicalCommandType.DescendMaintain] = new() { Verb = "D", Format = "{verb}{arg}" },
                [CanonicalCommandType.Speed] = new() { Verb = "S", Format = "{verb}{arg}" },
                // Transponder
                [CanonicalCommandType.Squawk] = new() { Verb = "SQ", Format = "{verb}{arg}" },
                [CanonicalCommandType.SquawkIdent] = new() { Verb = "SQI", Format = "{verb}" },
                [CanonicalCommandType.SquawkVfr] = new() { Verb = "SQVFR", Format = "{verb}" },
                [CanonicalCommandType.SquawkNormal] = new() { Verb = "SQNORM", Format = "{verb}" },
                [CanonicalCommandType.SquawkStandby] = new() { Verb = "SQSBY", Format = "{verb}" },
                [CanonicalCommandType.Ident] = new() { Verb = "IDENT", Format = "{verb}" },
                // Navigation
                [CanonicalCommandType.DirectTo] = new() { Verb = "DCT", Format = "{verb} {arg}" },
                // Tower
                [CanonicalCommandType.LineUpAndWait] = new() { Verb = "LUAW", Format = "{verb}" },
                [CanonicalCommandType.ClearedForTakeoff] = new() { Verb = "CTO", Format = "{verb}" },
                [CanonicalCommandType.CancelTakeoffClearance] = new() { Verb = "CTOC", Format = "{verb}" },
                [CanonicalCommandType.GoAround] = new() { Verb = "GA", Format = "{verb}" },
                [CanonicalCommandType.ClearedToLand] = new() { Verb = "CTL", Format = "{verb}" },
                [CanonicalCommandType.TouchAndGo] = new() { Verb = "TG", Format = "{verb}" },
                [CanonicalCommandType.StopAndGo] = new() { Verb = "SG", Format = "{verb}" },
                [CanonicalCommandType.LowApproach] = new() { Verb = "LA", Format = "{verb}" },
                [CanonicalCommandType.ClearedForOption] = new() { Verb = "COPT", Format = "{verb}" },
                // Pattern
                [CanonicalCommandType.EnterLeftDownwind] = new() { Verb = "ELD", Format = "{verb}" },
                [CanonicalCommandType.EnterRightDownwind] = new() { Verb = "ERD", Format = "{verb}" },
                [CanonicalCommandType.EnterLeftBase] = new() { Verb = "ELB", Format = "{verb}" },
                [CanonicalCommandType.EnterRightBase] = new() { Verb = "ERB", Format = "{verb}" },
                [CanonicalCommandType.EnterFinal] = new() { Verb = "EF", Format = "{verb}" },
                [CanonicalCommandType.MakeLeftTraffic] = new() { Verb = "MLT", Format = "{verb}" },
                [CanonicalCommandType.MakeRightTraffic] = new() { Verb = "MRT", Format = "{verb}" },
                [CanonicalCommandType.TurnCrosswind] = new() { Verb = "TC", Format = "{verb}" },
                [CanonicalCommandType.TurnDownwind] = new() { Verb = "TD", Format = "{verb}" },
                [CanonicalCommandType.TurnBase] = new() { Verb = "TB", Format = "{verb}" },
                [CanonicalCommandType.ExtendDownwind] = new() { Verb = "EXT", Format = "{verb}" },
                // Hold
                [CanonicalCommandType.HoldPresentPosition360Left] = new() { Verb = "HPP360L", Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPosition360Right] = new() { Verb = "HPP360R", Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPositionHover] = new() { Verb = "HPP", Format = "{verb}" },
                [CanonicalCommandType.HoldAtFixLeft] = new() { Verb = "HFIXL", Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixRight] = new() { Verb = "HFIXR", Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixHover] = new() { Verb = "HFIX", Format = "{verb} {arg}" },
                // Sim control
                [CanonicalCommandType.Delete] = new() { Verb = "X", Format = "{verb}" },
                [CanonicalCommandType.Pause] = new() { Verb = "PAUSE", Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new() { Verb = "UNPAUSE", Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new() { Verb = "SIMRATE", Format = "{verb} {arg}" },
            },
        };
    }

    private static bool MatchesPreset(CommandScheme current, CommandScheme preset)
    {
        if (current.ParseMode != preset.ParseMode)
        {
            return false;
        }

        if (current.Patterns.Count != preset.Patterns.Count)
        {
            return false;
        }

        foreach (var (type, presetPattern) in preset.Patterns)
        {
            if (!current.Patterns.TryGetValue(type, out var p))
            {
                return false;
            }

            if (!string.Equals(p.Verb, presetPattern.Verb, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(p.Format, presetPattern.Format, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
