using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Commands;

public static class CommandDescriber
{
    internal static CanonicalCommandType ToCanonicalType(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand => CanonicalCommandType.FlyHeading,
            TurnLeftCommand => CanonicalCommandType.TurnLeft,
            TurnRightCommand => CanonicalCommandType.TurnRight,
            LeftTurnCommand => CanonicalCommandType.RelativeLeft,
            RightTurnCommand => CanonicalCommandType.RelativeRight,
            FlyPresentHeadingCommand => CanonicalCommandType.FlyPresentHeading,
            ClimbMaintainCommand => CanonicalCommandType.ClimbMaintain,
            DescendMaintainCommand => CanonicalCommandType.DescendMaintain,
            SpeedCommand => CanonicalCommandType.Speed,
            DirectToCommand => CanonicalCommandType.DirectTo,
            SquawkCommand => CanonicalCommandType.Squawk,
            SquawkResetCommand => CanonicalCommandType.Squawk,
            SquawkVfrCommand => CanonicalCommandType.SquawkVfr,
            SquawkNormalCommand => CanonicalCommandType.SquawkNormal,
            SquawkStandbyCommand => CanonicalCommandType.SquawkStandby,
            IdentCommand => CanonicalCommandType.Ident,
            RandomSquawkCommand => CanonicalCommandType.RandomSquawk,
            SquawkAllCommand => CanonicalCommandType.SquawkAll,
            SquawkNormalAllCommand => CanonicalCommandType.SquawkNormalAll,
            SquawkStandbyAllCommand => CanonicalCommandType.SquawkStandbyAll,
            LineUpAndWaitCommand => CanonicalCommandType.LineUpAndWait,
            ClearedForTakeoffCommand => CanonicalCommandType.ClearedForTakeoff,
            CancelTakeoffClearanceCommand => CanonicalCommandType.CancelTakeoffClearance,
            ClearedToLandCommand => CanonicalCommandType.ClearedToLand,
            GoAroundCommand => CanonicalCommandType.GoAround,
            EnterLeftDownwindCommand => CanonicalCommandType.EnterLeftDownwind,
            EnterRightDownwindCommand => CanonicalCommandType.EnterRightDownwind,
            EnterLeftBaseCommand => CanonicalCommandType.EnterLeftBase,
            EnterRightBaseCommand => CanonicalCommandType.EnterRightBase,
            EnterFinalCommand => CanonicalCommandType.EnterFinal,
            MakeLeftTrafficCommand => CanonicalCommandType.MakeLeftTraffic,
            MakeRightTrafficCommand => CanonicalCommandType.MakeRightTraffic,
            TurnCrosswindCommand => CanonicalCommandType.TurnCrosswind,
            TurnDownwindCommand => CanonicalCommandType.TurnDownwind,
            TurnBaseCommand => CanonicalCommandType.TurnBase,
            ExtendDownwindCommand => CanonicalCommandType.ExtendDownwind,
            MakeShortApproachCommand => CanonicalCommandType.MakeShortApproach,
            MakeLeft360Command => CanonicalCommandType.MakeLeft360,
            MakeRight360Command => CanonicalCommandType.MakeRight360,
            MakeLeft270Command => CanonicalCommandType.MakeLeft270,
            MakeRight270Command => CanonicalCommandType.MakeRight270,
            CircleAirportCommand => CanonicalCommandType.CircleAirport,
            SequenceCommand => CanonicalCommandType.Sequence,
            TouchAndGoCommand => CanonicalCommandType.TouchAndGo,
            StopAndGoCommand => CanonicalCommandType.StopAndGo,
            LowApproachCommand => CanonicalCommandType.LowApproach,
            ClearedForOptionCommand => CanonicalCommandType.ClearedForOption,
            HoldPresentPosition360Command cmd => cmd.Direction == TurnDirection.Left
                ? CanonicalCommandType.HoldPresentPosition360Left
                : CanonicalCommandType.HoldPresentPosition360Right,
            HoldPresentPositionHoverCommand => CanonicalCommandType.HoldPresentPositionHover,
            HoldAtFixOrbitCommand cmd => cmd.Direction == TurnDirection.Left
                ? CanonicalCommandType.HoldAtFixLeft
                : CanonicalCommandType.HoldAtFixRight,
            HoldAtFixHoverCommand => CanonicalCommandType.HoldAtFixHover,
            PushbackCommand => CanonicalCommandType.Pushback,
            TaxiCommand => CanonicalCommandType.Taxi,
            HoldPositionCommand => CanonicalCommandType.HoldPosition,
            ResumeCommand => CanonicalCommandType.Resume,
            CrossRunwayCommand => CanonicalCommandType.CrossRunway,
            HoldShortCommand => CanonicalCommandType.HoldShort,
            FollowCommand => CanonicalCommandType.Follow,
            ExitLeftCommand => CanonicalCommandType.ExitLeft,
            ExitRightCommand => CanonicalCommandType.ExitRight,
            ExitTaxiwayCommand => CanonicalCommandType.ExitTaxiway,
            SayCommand => CanonicalCommandType.Say,
            _ => CanonicalCommandType.FlyHeading, // fallback
        };
    }

    internal static TrackedCommandType ClassifyCommand(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand => TrackedCommandType.Heading,
            TurnLeftCommand => TrackedCommandType.Heading,
            TurnRightCommand => TrackedCommandType.Heading,
            LeftTurnCommand => TrackedCommandType.Heading,
            RightTurnCommand => TrackedCommandType.Heading,
            FlyPresentHeadingCommand => TrackedCommandType.Immediate,
            ClimbMaintainCommand => TrackedCommandType.Altitude,
            DescendMaintainCommand => TrackedCommandType.Altitude,
            SpeedCommand => TrackedCommandType.Speed,
            DirectToCommand => TrackedCommandType.Navigation,
            WaitCommand => TrackedCommandType.Wait,
            WaitDistanceCommand => TrackedCommandType.Wait,
            _ => TrackedCommandType.Immediate,
        };
    }

    public static string DescribeCommand(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand cmd => $"FH {cmd.Heading:000}",
            TurnLeftCommand cmd => $"TL {cmd.Heading:000}",
            TurnRightCommand cmd => $"TR {cmd.Heading:000}",
            LeftTurnCommand cmd => $"LT {cmd.Degrees}",
            RightTurnCommand cmd => $"RT {cmd.Degrees}",
            FlyPresentHeadingCommand => "FPH",
            ClimbMaintainCommand cmd => $"CM {cmd.Altitude}",
            DescendMaintainCommand cmd => $"DM {cmd.Altitude}",
            SpeedCommand cmd => cmd.Speed == 0 ? "Resume speed" : $"SPD {cmd.Speed}",
            DirectToCommand cmd => $"DCT {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            SquawkCommand cmd => $"SQ {cmd.Code:D4}",
            SquawkResetCommand => "SQ",
            SquawkVfrCommand => "SQVFR",
            SquawkNormalCommand => "SQNORM",
            SquawkStandbyCommand => "SQSBY",
            IdentCommand => "IDENT",
            RandomSquawkCommand => "RANDSQ",
            SquawkAllCommand => "SQALL",
            SquawkNormalAllCommand => "SNALL",
            SquawkStandbyAllCommand => "SSALL",
            LineUpAndWaitCommand => "LUAW",
            ClearedForTakeoffCommand cto => FormatCtoCanonical(cto),
            CancelTakeoffClearanceCommand => "CTOC",
            ClearedToLandCommand => "CTL",
            CancelLandingClearanceCommand => "CLC",
            GoAroundCommand ga => FormatGaCanonical(ga),
            EnterLeftDownwindCommand eld => eld.RunwayId is not null ? $"ELD {eld.RunwayId}" : "ELD",
            EnterRightDownwindCommand erd => erd.RunwayId is not null ? $"ERD {erd.RunwayId}" : "ERD",
            EnterLeftBaseCommand elb => DescribePatternBase("ELB", elb.RunwayId, elb.FinalDistanceNm),
            EnterRightBaseCommand erb => DescribePatternBase("ERB", erb.RunwayId, erb.FinalDistanceNm),
            EnterFinalCommand ef => ef.RunwayId is not null ? $"EF {ef.RunwayId}" : "EF",
            MakeLeftTrafficCommand => "MLT",
            MakeRightTrafficCommand => "MRT",
            TurnCrosswindCommand => "TC",
            TurnDownwindCommand => "TD",
            TurnBaseCommand => "TB",
            ExtendDownwindCommand => "EXT",
            MakeShortApproachCommand => "SA",
            MakeLeft360Command => "L360",
            MakeRight360Command => "R360",
            MakeLeft270Command => "L270",
            MakeRight270Command => "R270",
            CircleAirportCommand => "CA",
            SequenceCommand seq => seq.FollowCallsign is not null ? $"SEQ {seq.Number} {seq.FollowCallsign}" : $"SEQ {seq.Number}",
            TouchAndGoCommand => "TG",
            StopAndGoCommand => "SG",
            LowApproachCommand => "LA",
            ClearedForOptionCommand => "COPT",
            HoldPresentPosition360Command cmd => cmd.Direction == TurnDirection.Left ? "HPPL" : "HPPR",
            HoldPresentPositionHoverCommand => "HPP",
            HoldAtFixOrbitCommand cmd => $"HFIX{(cmd.Direction == TurnDirection.Left ? "L" : "R")} {cmd.FixName}",
            HoldAtFixHoverCommand cmd => $"HFIX {cmd.FixName}",
            WaitCommand cmd => $"WAIT {cmd.Seconds}",
            WaitDistanceCommand cmd => $"WAITD {cmd.DistanceNm}",
            PushbackCommand push => push.Heading is not null ? $"PUSH {push.Heading:000}" : "PUSH",
            TaxiCommand taxi => $"TAXI {string.Join(" ", taxi.Path)}",
            HoldPositionCommand => "HOLD",
            ResumeCommand => "RES",
            CrossRunwayCommand cross => $"CROSS {cross.RunwayId}",
            HoldShortCommand hs => $"HS {hs.Target}",
            FollowCommand follow => $"FOLLOW {follow.TargetCallsign}",
            ExitLeftCommand => "EL",
            ExitRightCommand => "ER",
            ExitTaxiwayCommand et => $"EXIT {et.Taxiway}",
            SayCommand say => $"SAY {say.Text}",
            _ => command.ToString() ?? "?",
        };
    }

    internal static string DescribeNatural(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand cmd => $"Fly heading {cmd.Heading:000}",
            TurnLeftCommand cmd => $"Turn left heading {cmd.Heading:000}",
            TurnRightCommand cmd => $"Turn right heading {cmd.Heading:000}",
            LeftTurnCommand cmd => $"Turn {cmd.Degrees} degrees left",
            RightTurnCommand cmd => $"Turn {cmd.Degrees} degrees right",
            FlyPresentHeadingCommand => "Fly present heading",
            ClimbMaintainCommand cmd => $"Climb and maintain {cmd.Altitude:N0}",
            DescendMaintainCommand cmd => $"Descend and maintain {cmd.Altitude:N0}",
            SpeedCommand cmd => cmd.Speed == 0 ? "Resume normal speed" : $"Speed {cmd.Speed} knots",
            DirectToCommand cmd => $"Proceed direct {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            SquawkCommand cmd => $"Squawk {cmd.Code:D4}",
            SquawkResetCommand => "Squawk assigned",
            SquawkVfrCommand => "Squawk VFR",
            SquawkNormalCommand => "Squawk normal",
            SquawkStandbyCommand => "Squawk standby",
            IdentCommand => "Ident",
            RandomSquawkCommand => "Random squawk",
            SquawkAllCommand => "Squawk all assigned",
            SquawkNormalAllCommand => "Squawk normal all",
            SquawkStandbyAllCommand => "Squawk standby all",
            ClearedForTakeoffCommand cto => DescribeCtoNatural(cto),
            CancelTakeoffClearanceCommand => "Cancel takeoff clearance",
            ClearedToLandCommand => "Cleared to land",
            CancelLandingClearanceCommand => "Cancel landing clearance",
            GoAroundCommand ga => DescribeGaNatural(ga),
            EnterLeftDownwindCommand eld => DescribePatternEntryNatural("left downwind", eld.RunwayId, null),
            EnterRightDownwindCommand erd => DescribePatternEntryNatural("right downwind", erd.RunwayId, null),
            EnterLeftBaseCommand elb => DescribePatternEntryNatural("left base", elb.RunwayId, elb.FinalDistanceNm),
            EnterRightBaseCommand erb => DescribePatternEntryNatural("right base", erb.RunwayId, erb.FinalDistanceNm),
            EnterFinalCommand ef => DescribePatternEntryNatural("straight-in final", ef.RunwayId, null),
            MakeLeftTrafficCommand => "Make left traffic",
            MakeRightTrafficCommand => "Make right traffic",
            TurnCrosswindCommand => "Turn crosswind",
            TurnDownwindCommand => "Turn downwind",
            TurnBaseCommand => "Turn base",
            ExtendDownwindCommand => "Extend downwind",
            MakeShortApproachCommand => "Make short approach",
            MakeLeft360Command => "Make left 360",
            MakeRight360Command => "Make right 360",
            MakeLeft270Command => "Make left 270",
            MakeRight270Command => "Make right 270",
            CircleAirportCommand => "Circle airport",
            SequenceCommand seq => seq.FollowCallsign is not null
                ? $"Number {seq.Number}, follow {seq.FollowCallsign}"
                : $"Number {seq.Number} in sequence",
            TouchAndGoCommand => "Cleared touch-and-go",
            StopAndGoCommand => "Cleared stop-and-go",
            LowApproachCommand => "Cleared low approach",
            ClearedForOptionCommand => "Cleared for the option",
            HoldPresentPosition360Command cmd => cmd.Direction == TurnDirection.Left
                ? "Hold present position, left 360s"
                : "Hold present position, right 360s",
            HoldPresentPositionHoverCommand => "Hold present position",
            HoldAtFixOrbitCommand cmd => $"Hold at {cmd.FixName}, {(cmd.Direction == TurnDirection.Left ? "left" : "right")} orbits",
            HoldAtFixHoverCommand cmd => $"Hold at {cmd.FixName}",
            WaitCommand cmd => $"Wait {cmd.Seconds} seconds",
            WaitDistanceCommand cmd => $"Wait {cmd.DistanceNm} nm",
            PushbackCommand push => push.Heading is not null ? $"Pushback, face heading {push.Heading:000}" : "Pushback",
            TaxiCommand taxi => $"Taxi via {string.Join(" ", taxi.Path)}",
            HoldPositionCommand => "Hold position",
            ResumeCommand => "Resume taxi",
            CrossRunwayCommand cross => $"Cross runway {cross.RunwayId}",
            HoldShortCommand hs => $"Hold short of {hs.Target}",
            FollowCommand follow => $"Follow {follow.TargetCallsign}",
            ExitLeftCommand => "Exit left",
            ExitRightCommand => "Exit right",
            ExitTaxiwayCommand et => $"Exit at {et.Taxiway}",
            SayCommand say => say.Text,
            UnsupportedCommand cmd => cmd.RawText,
            _ => command.ToString() ?? "?",
        };
    }

    internal static bool IsTowerCommand(ParsedCommand command)
    {
        return command
            is ClearedForTakeoffCommand
                or CancelTakeoffClearanceCommand
                or LineUpAndWaitCommand
                or ClearedToLandCommand
                or CancelLandingClearanceCommand
                or GoAroundCommand
                or EnterLeftDownwindCommand
                or EnterRightDownwindCommand
                or EnterLeftBaseCommand
                or EnterRightBaseCommand
                or EnterFinalCommand
                or MakeLeftTrafficCommand
                or MakeRightTrafficCommand
                or TurnCrosswindCommand
                or TurnDownwindCommand
                or TurnBaseCommand
                or ExtendDownwindCommand
                or MakeShortApproachCommand
                or MakeLeft360Command
                or MakeRight360Command
                or MakeLeft270Command
                or MakeRight270Command
                or CircleAirportCommand
                or SequenceCommand
                or TouchAndGoCommand
                or StopAndGoCommand
                or LowApproachCommand
                or ClearedForOptionCommand
                or ExitLeftCommand
                or ExitRightCommand
                or ExitTaxiwayCommand;
    }

    internal static bool IsGroundCommand(ParsedCommand command)
    {
        return command
            is PushbackCommand
                or TaxiCommand
                or HoldPositionCommand
                or ResumeCommand
                or CrossRunwayCommand
                or HoldShortCommand
                or FollowCommand;
    }

    private static string FormatCtoCanonical(ClearedForTakeoffCommand cto)
    {
        var suffix = cto.Departure switch
        {
            DefaultDeparture => "",
            RunwayHeadingDeparture => " MRH",
            RelativeTurnDeparture { Direction: TurnDirection.Right } rel => $" MR{rel.Degrees}",
            RelativeTurnDeparture rel => $" ML{rel.Degrees}",
            FlyHeadingDeparture { Direction: TurnDirection.Right } fh => $" RH{fh.Heading:000}",
            FlyHeadingDeparture { Direction: TurnDirection.Left } fh => $" LH{fh.Heading:000}",
            FlyHeadingDeparture fh => $" H{fh.Heading:000}",
            OnCourseDeparture => " OC",
            DirectFixDeparture dfd => $" DCT {dfd.FixName}",
            ClosedTrafficDeparture { Direction: PatternDirection.Right } => " MRT",
            ClosedTrafficDeparture => " MLT",
            _ => "",
        };

        var alt = cto.AssignedAltitude is not null ? $" {cto.AssignedAltitude}" : "";

        return $"CTO{suffix}{alt}";
    }

    private static string DescribeCtoNatural(ClearedForTakeoffCommand cto)
    {
        var msg = "Cleared for takeoff";
        msg += cto.Departure switch
        {
            DefaultDeparture => "",
            RunwayHeadingDeparture => ", fly runway heading",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Right } => ", right crosswind departure",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Left } => ", left crosswind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Right } => ", right downwind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Left } => ", left downwind departure",
            RelativeTurnDeparture rel => $", turn {(rel.Direction == TurnDirection.Right ? "right" : "left")} {rel.Degrees} degrees",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Right => $", turn right heading {fh.Heading:000}",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Left => $", turn left heading {fh.Heading:000}",
            FlyHeadingDeparture fh => $", fly heading {fh.Heading:000}",
            OnCourseDeparture => ", on course",
            DirectFixDeparture dfd => $", direct {dfd.FixName}",
            ClosedTrafficDeparture ct => $", make {(ct.Direction == PatternDirection.Left ? "left" : "right")} traffic",
            _ => "",
        };
        if (cto.AssignedAltitude is not null)
        {
            msg += $", climb and maintain {cto.AssignedAltitude:N0}";
        }
        return msg;
    }

    private static string FormatGaCanonical(GoAroundCommand ga)
    {
        if (ga.TrafficPattern is PatternDirection.Left)
        {
            return "GA MLT";
        }
        if (ga.TrafficPattern is PatternDirection.Right)
        {
            return "GA MRT";
        }
        if (ga.AssignedHeading is not null || ga.TargetAltitude is not null)
        {
            return $"GA {(ga.AssignedHeading?.ToString("000") ?? "RH")} {ga.TargetAltitude}";
        }
        return "GA";
    }

    private static string DescribeGaNatural(GoAroundCommand ga)
    {
        var msg = "Go around";
        if (ga.TrafficPattern is PatternDirection.Left)
        {
            msg += ", make left traffic";
        }
        else if (ga.TrafficPattern is PatternDirection.Right)
        {
            msg += ", make right traffic";
        }
        if (ga.AssignedHeading is not null)
        {
            msg += $", fly heading {ga.AssignedHeading:000}";
        }
        if (ga.TargetAltitude is not null)
        {
            msg += $", climb to {ga.TargetAltitude:N0}";
        }
        return msg;
    }

    private static string DescribePatternBase(string verb, string? runwayId, double? distNm)
    {
        var parts = new List<string> { verb };
        if (runwayId is not null)
        {
            parts.Add(runwayId);
        }

        if (distNm is not null)
        {
            parts.Add(distNm.Value.ToString("G"));
        }

        return string.Join(' ', parts);
    }

    private static string DescribePatternEntryNatural(string legName, string? runwayId, double? distNm)
    {
        var msg = $"Enter {legName}";
        if (runwayId is not null)
        {
            msg += $", Runway {runwayId}";
        }

        if (distNm is not null)
        {
            msg += $", {distNm:G}nm final";
        }

        return msg;
    }
}
