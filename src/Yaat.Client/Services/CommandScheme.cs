using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public class CommandPattern
{
    public required List<string> Aliases { get; set; }
    public required string Format { get; init; }

    public string PrimaryVerb => Aliases[0];
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
                [CanonicalCommandType.FlyHeading] = new() { Aliases = ["FH"], Format = "{verb} {arg}" },
                [CanonicalCommandType.TurnLeft] = new() { Aliases = ["TL"], Format = "{verb} {arg}" },
                [CanonicalCommandType.TurnRight] = new() { Aliases = ["TR"], Format = "{verb} {arg}" },
                [CanonicalCommandType.RelativeLeft] = new() { Aliases = ["LT"], Format = "{verb} {arg}" },
                [CanonicalCommandType.RelativeRight] = new() { Aliases = ["RT"], Format = "{verb} {arg}" },
                [CanonicalCommandType.FlyPresentHeading] = new() { Aliases = ["FPH", "FCH"], Format = "{verb}" },
                // Altitude / Speed
                [CanonicalCommandType.ClimbMaintain] = new() { Aliases = ["CM"], Format = "{verb} {arg}" },
                [CanonicalCommandType.DescendMaintain] = new() { Aliases = ["DM"], Format = "{verb} {arg}" },
                [CanonicalCommandType.Speed] = new() { Aliases = ["SPD", "SLOW", "SL", "SPEED"], Format = "{verb} {arg}" },
                // Transponder
                [CanonicalCommandType.Squawk] = new() { Aliases = ["SQ", "SQUAWK"], Format = "{verb} {arg}" },
                [CanonicalCommandType.SquawkIdent] = new() { Aliases = ["SQI", "SQID"], Format = "{verb}" },
                [CanonicalCommandType.SquawkVfr] = new() { Aliases = ["SQVFR", "SQV"], Format = "{verb}" },
                [CanonicalCommandType.SquawkNormal] = new() { Aliases = ["SQNORM", "SN"], Format = "{verb}" },
                [CanonicalCommandType.SquawkStandby] = new() { Aliases = ["SQSBY", "SS"], Format = "{verb}" },
                [CanonicalCommandType.Ident] = new() { Aliases = ["IDENT", "ID"], Format = "{verb}" },
                // Navigation
                [CanonicalCommandType.DirectTo] = new() { Aliases = ["DCT"], Format = "{verb} {arg}" },
                // Tower
                [CanonicalCommandType.LineUpAndWait] = new() { Aliases = ["LUAW", "POS", "LU", "PH"], Format = "{verb}" },
                [CanonicalCommandType.ClearedForTakeoff] = new() { Aliases = ["CTO"], Format = "{verb}" },
                [CanonicalCommandType.CancelTakeoffClearance] = new() { Aliases = ["CTOC"], Format = "{verb}" },
                [CanonicalCommandType.GoAround] = new() { Aliases = ["GA"], Format = "{verb}" },
                [CanonicalCommandType.ClearedToLand] = new() { Aliases = ["CTL", "FS"], Format = "{verb}" },
                [CanonicalCommandType.CancelLandingClearance] = new() { Aliases = ["CLC", "CTLC"], Format = "{verb}" },
                [CanonicalCommandType.TouchAndGo] = new() { Aliases = ["TG"], Format = "{verb}" },
                [CanonicalCommandType.StopAndGo] = new() { Aliases = ["SG"], Format = "{verb}" },
                [CanonicalCommandType.LowApproach] = new() { Aliases = ["LA"], Format = "{verb}" },
                [CanonicalCommandType.ClearedForOption] = new() { Aliases = ["COPT"], Format = "{verb}" },
                // Pattern
                [CanonicalCommandType.EnterLeftDownwind] = new() { Aliases = ["ELD"], Format = "{verb}" },
                [CanonicalCommandType.EnterRightDownwind] = new() { Aliases = ["ERD"], Format = "{verb}" },
                [CanonicalCommandType.EnterLeftBase] = new() { Aliases = ["ELB"], Format = "{verb}" },
                [CanonicalCommandType.EnterRightBase] = new() { Aliases = ["ERB"], Format = "{verb}" },
                [CanonicalCommandType.EnterFinal] = new() { Aliases = ["EF"], Format = "{verb}" },
                [CanonicalCommandType.MakeLeftTraffic] = new() { Aliases = ["MLT"], Format = "{verb}" },
                [CanonicalCommandType.MakeRightTraffic] = new() { Aliases = ["MRT"], Format = "{verb}" },
                [CanonicalCommandType.TurnCrosswind] = new() { Aliases = ["TC"], Format = "{verb}" },
                [CanonicalCommandType.TurnDownwind] = new() { Aliases = ["TD"], Format = "{verb}" },
                [CanonicalCommandType.TurnBase] = new() { Aliases = ["TB"], Format = "{verb}" },
                [CanonicalCommandType.ExtendDownwind] = new() { Aliases = ["EXT"], Format = "{verb}" },
                // Hold
                [CanonicalCommandType.HoldPresentPosition360Left] = new() { Aliases = ["HPP360L"], Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPosition360Right] = new() { Aliases = ["HPP360R"], Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPositionHover] = new() { Aliases = ["HPP"], Format = "{verb}" },
                [CanonicalCommandType.HoldAtFixLeft] = new() { Aliases = ["HFIXL"], Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixRight] = new() { Aliases = ["HFIXR"], Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixHover] = new() { Aliases = ["HFIX"], Format = "{verb} {arg}" },
                // Sim control
                [CanonicalCommandType.Delete] = new() { Aliases = ["DEL"], Format = "{verb}" },
                [CanonicalCommandType.Pause] = new() { Aliases = ["PAUSE", "P"], Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new() { Aliases = ["UNPAUSE", "U", "UN", "UNP", "UP"], Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new() { Aliases = ["SIMRATE"], Format = "{verb} {arg}" },
                [CanonicalCommandType.SpawnNow] = new() { Aliases = ["SPAWN"], Format = "{verb}" },
                [CanonicalCommandType.SpawnDelay] = new() { Aliases = ["DELAY"], Format = "{verb} {arg}" },
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
                [CanonicalCommandType.FlyHeading] = new() { Aliases = ["H"], Format = "{verb}{arg}" },
                [CanonicalCommandType.TurnLeft] = new() { Aliases = ["L"], Format = "{verb}{arg}" },
                [CanonicalCommandType.TurnRight] = new() { Aliases = ["R"], Format = "{verb}{arg}" },
                [CanonicalCommandType.RelativeLeft] = new() { Aliases = ["T"], Format = "{verb}{arg}L" },
                [CanonicalCommandType.RelativeRight] = new() { Aliases = ["T"], Format = "{verb}{arg}R" },
                [CanonicalCommandType.FlyPresentHeading] = new() { Aliases = ["H"], Format = "{verb}" },
                // Altitude / Speed
                [CanonicalCommandType.ClimbMaintain] = new() { Aliases = ["C"], Format = "{verb}{arg}" },
                [CanonicalCommandType.DescendMaintain] = new() { Aliases = ["D"], Format = "{verb}{arg}" },
                [CanonicalCommandType.Speed] = new() { Aliases = ["S"], Format = "{verb}{arg}" },
                // Transponder
                [CanonicalCommandType.Squawk] = new() { Aliases = ["SQ"], Format = "{verb}{arg}" },
                [CanonicalCommandType.SquawkIdent] = new() { Aliases = ["SQI"], Format = "{verb}" },
                [CanonicalCommandType.SquawkVfr] = new() { Aliases = ["SQVFR"], Format = "{verb}" },
                [CanonicalCommandType.SquawkNormal] = new() { Aliases = ["SQNORM", "SQA", "SQON"], Format = "{verb}" },
                [CanonicalCommandType.SquawkStandby] = new() { Aliases = ["SQSBY", "SQS"], Format = "{verb}" },
                [CanonicalCommandType.Ident] = new() { Aliases = ["IDENT", "ID"], Format = "{verb}" },
                // Navigation
                [CanonicalCommandType.DirectTo] = new() { Aliases = ["DCT"], Format = "{verb} {arg}" },
                // Tower (VICE has no tower commands; these use ATCTrainer verbs)
                [CanonicalCommandType.LineUpAndWait] = new() { Aliases = ["LUAW"], Format = "{verb}" },
                [CanonicalCommandType.ClearedForTakeoff] = new() { Aliases = ["CTO"], Format = "{verb}" },
                [CanonicalCommandType.CancelTakeoffClearance] = new() { Aliases = ["CTOC"], Format = "{verb}" },
                [CanonicalCommandType.GoAround] = new() { Aliases = ["GA"], Format = "{verb}" },
                [CanonicalCommandType.ClearedToLand] = new() { Aliases = ["CTL", "FS"], Format = "{verb}" },
                [CanonicalCommandType.CancelLandingClearance] = new() { Aliases = ["CLC", "CTLC"], Format = "{verb}" },
                [CanonicalCommandType.TouchAndGo] = new() { Aliases = ["TG"], Format = "{verb}" },
                [CanonicalCommandType.StopAndGo] = new() { Aliases = ["SG"], Format = "{verb}" },
                [CanonicalCommandType.LowApproach] = new() { Aliases = ["LA"], Format = "{verb}" },
                [CanonicalCommandType.ClearedForOption] = new() { Aliases = ["COPT"], Format = "{verb}" },
                // Pattern
                [CanonicalCommandType.EnterLeftDownwind] = new() { Aliases = ["ELD"], Format = "{verb}" },
                [CanonicalCommandType.EnterRightDownwind] = new() { Aliases = ["ERD"], Format = "{verb}" },
                [CanonicalCommandType.EnterLeftBase] = new() { Aliases = ["ELB"], Format = "{verb}" },
                [CanonicalCommandType.EnterRightBase] = new() { Aliases = ["ERB"], Format = "{verb}" },
                [CanonicalCommandType.EnterFinal] = new() { Aliases = ["EF"], Format = "{verb}" },
                [CanonicalCommandType.MakeLeftTraffic] = new() { Aliases = ["MLT"], Format = "{verb}" },
                [CanonicalCommandType.MakeRightTraffic] = new() { Aliases = ["MRT"], Format = "{verb}" },
                [CanonicalCommandType.TurnCrosswind] = new() { Aliases = ["TC"], Format = "{verb}" },
                [CanonicalCommandType.TurnDownwind] = new() { Aliases = ["TD"], Format = "{verb}" },
                [CanonicalCommandType.TurnBase] = new() { Aliases = ["TB"], Format = "{verb}" },
                [CanonicalCommandType.ExtendDownwind] = new() { Aliases = ["EXT"], Format = "{verb}" },
                // Hold
                [CanonicalCommandType.HoldPresentPosition360Left] = new() { Aliases = ["HPP360L"], Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPosition360Right] = new() { Aliases = ["HPP360R"], Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPositionHover] = new() { Aliases = ["HPP"], Format = "{verb}" },
                [CanonicalCommandType.HoldAtFixLeft] = new() { Aliases = ["HFIXL"], Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixRight] = new() { Aliases = ["HFIXR"], Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixHover] = new() { Aliases = ["HFIX"], Format = "{verb} {arg}" },
                // Sim control
                [CanonicalCommandType.Delete] = new() { Aliases = ["X"], Format = "{verb}" },
                [CanonicalCommandType.Pause] = new() { Aliases = ["PAUSE"], Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new() { Aliases = ["UNPAUSE"], Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new() { Aliases = ["SIMRATE"], Format = "{verb} {arg}" },
                [CanonicalCommandType.SpawnNow] = new() { Aliases = ["SPAWN"], Format = "{verb}" },
                [CanonicalCommandType.SpawnDelay] = new() { Aliases = ["DELAY"], Format = "{verb} {arg}" },
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

            if (!string.Equals(p.PrimaryVerb, presetPattern.PrimaryVerb, StringComparison.Ordinal))
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
