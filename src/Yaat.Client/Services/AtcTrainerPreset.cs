using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

internal static class AtcTrainerPreset
{
    public static CommandScheme Create()
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
                [CanonicalCommandType.EnterLeftDownwind] = new() { Aliases = ["ELD"], Format = "{verb} {arg?}" },
                [CanonicalCommandType.EnterRightDownwind] = new() { Aliases = ["ERD"], Format = "{verb} {arg?}" },
                [CanonicalCommandType.EnterLeftBase] = new() { Aliases = ["ELB"], Format = "{verb} {arg?}" },
                [CanonicalCommandType.EnterRightBase] = new() { Aliases = ["ERB"], Format = "{verb} {arg?}" },
                [CanonicalCommandType.EnterFinal] = new() { Aliases = ["EF"], Format = "{verb} {arg?}" },
                [CanonicalCommandType.MakeLeftTraffic] = new() { Aliases = ["MLT"], Format = "{verb}" },
                [CanonicalCommandType.MakeRightTraffic] = new() { Aliases = ["MRT"], Format = "{verb}" },
                [CanonicalCommandType.TurnCrosswind] = new() { Aliases = ["TC"], Format = "{verb}" },
                [CanonicalCommandType.TurnDownwind] = new() { Aliases = ["TD"], Format = "{verb}" },
                [CanonicalCommandType.TurnBase] = new() { Aliases = ["TB"], Format = "{verb}" },
                [CanonicalCommandType.ExtendDownwind] = new() { Aliases = ["EXT"], Format = "{verb}" },
                // Hold
                [CanonicalCommandType.HoldPresentPosition360Left] = new() { Aliases = ["HPPL"], Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPosition360Right] = new() { Aliases = ["HPPR"], Format = "{verb}" },
                [CanonicalCommandType.HoldPresentPositionHover] = new() { Aliases = ["HPP"], Format = "{verb}" },
                [CanonicalCommandType.HoldAtFixLeft] = new() { Aliases = ["HFIXL"], Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixRight] = new() { Aliases = ["HFIXR"], Format = "{verb} {arg}" },
                [CanonicalCommandType.HoldAtFixHover] = new() { Aliases = ["HFIX"], Format = "{verb} {arg}" },
                // Sim control
                [CanonicalCommandType.Delete] = new() { Aliases = ["DEL"], Format = "{verb}" },
                [CanonicalCommandType.Pause] = new() { Aliases = ["PAUSE", "P"], Format = "{verb}" },
                [CanonicalCommandType.Unpause] = new() { Aliases = ["UNPAUSE", "U", "UN", "UNP", "UP"], Format = "{verb}" },
                [CanonicalCommandType.SimRate] = new() { Aliases = ["SIMRATE"], Format = "{verb} {arg}" },
                [CanonicalCommandType.Wait] = new() { Aliases = ["WAIT"], Format = "{verb} {arg}" },
                [CanonicalCommandType.WaitDistance] = new() { Aliases = ["WAITD"], Format = "{verb} {arg}" },
                [CanonicalCommandType.Add] = new() { Aliases = ["ADD"], Format = "{verb} {arg}" },
                [CanonicalCommandType.SpawnNow] = new() { Aliases = ["SPAWN"], Format = "{verb}" },
                [CanonicalCommandType.SpawnDelay] = new() { Aliases = ["DELAY"], Format = "{verb} {arg}" },
            },
        };
    }
}
