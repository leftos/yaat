using Yaat.Sim.Data.Airport;
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
            ResumeNormalSpeedCommand => CanonicalCommandType.ResumeNormalSpeed,
            ReduceToFinalApproachSpeedCommand => CanonicalCommandType.ReduceToFinalApproachSpeed,
            DeleteSpeedRestrictionsCommand => CanonicalCommandType.DeleteSpeedRestrictions,
            ExpediteCommand => CanonicalCommandType.Expedite,
            NormalRateCommand => CanonicalCommandType.NormalRate,
            MachCommand => CanonicalCommandType.Mach,
            ForceHeadingCommand => CanonicalCommandType.ForceHeading,
            ForceAltitudeCommand => CanonicalCommandType.ForceAltitude,
            ForceSpeedCommand => CanonicalCommandType.ForceSpeed,
            WarpCommand => CanonicalCommandType.Warp,
            WarpGroundCommand => CanonicalCommandType.WarpGround,
            DirectToCommand => CanonicalCommandType.DirectTo,
            ForceDirectToCommand => CanonicalCommandType.ForceDirectTo,
            AppendDirectToCommand => CanonicalCommandType.AppendDirectTo,
            AppendForceDirectToCommand => CanonicalCommandType.AppendForceDirectTo,
            ExpectApproachCommand => CanonicalCommandType.ExpectApproach,
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
            LandAndHoldShortCommand => CanonicalCommandType.LandAndHoldShort,
            GoAroundCommand => CanonicalCommandType.GoAround,
            EnterLeftDownwindCommand => CanonicalCommandType.EnterLeftDownwind,
            EnterRightDownwindCommand => CanonicalCommandType.EnterRightDownwind,
            EnterLeftCrosswindCommand => CanonicalCommandType.EnterLeftCrosswind,
            EnterRightCrosswindCommand => CanonicalCommandType.EnterRightCrosswind,
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
            PatternSizeCommand => CanonicalCommandType.PatternSize,
            MakeNormalApproachCommand => CanonicalCommandType.MakeNormalApproach,
            Cancel270Command => CanonicalCommandType.Cancel270,
            MakeLeftSTurnsCommand => CanonicalCommandType.MakeLeftSTurns,
            MakeRightSTurnsCommand => CanonicalCommandType.MakeRightSTurns,
            Plan270Command => CanonicalCommandType.Plan270,
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
            AirTaxiCommand => CanonicalCommandType.AirTaxi,
            LandCommand => CanonicalCommandType.Land,
            ClearedTakeoffPresentCommand => CanonicalCommandType.ClearedTakeoffPresent,
            PushbackCommand => CanonicalCommandType.Pushback,
            TaxiCommand => CanonicalCommandType.Taxi,
            HoldPositionCommand => CanonicalCommandType.HoldPosition,
            ResumeCommand => CanonicalCommandType.Resume,
            CrossRunwayCommand => CanonicalCommandType.CrossRunway,
            HoldShortCommand => CanonicalCommandType.HoldShort,
            FollowCommand => CanonicalCommandType.Follow,
            GiveWayCommand => CanonicalCommandType.GiveWay,
            ExitLeftCommand => CanonicalCommandType.ExitLeft,
            ExitRightCommand => CanonicalCommandType.ExitRight,
            ExitTaxiwayCommand => CanonicalCommandType.ExitTaxiway,
            TaxiAllCommand => CanonicalCommandType.TaxiAll,
            BreakConflictCommand => CanonicalCommandType.BreakConflict,
            GoCommand => CanonicalCommandType.Go,
            SayCommand => CanonicalCommandType.Say,
            SaySpeedCommand => CanonicalCommandType.SaySpeed,
            SayMachCommand => CanonicalCommandType.SayMach,
            ClearedApproachCommand cmd => cmd.Force ? CanonicalCommandType.ClearedApproachForce : CanonicalCommandType.ClearedApproach,
            JoinApproachCommand cmd => cmd.Force ? CanonicalCommandType.JoinApproachForce : CanonicalCommandType.JoinApproach,
            ClearedApproachStraightInCommand => CanonicalCommandType.ClearedApproachStraightIn,
            JoinApproachStraightInCommand => CanonicalCommandType.JoinApproachStraightIn,
            JoinFinalApproachCourseCommand => CanonicalCommandType.JoinFinalApproachCourse,
            JoinStarCommand => CanonicalCommandType.JoinStar,
            JoinAirwayCommand => CanonicalCommandType.JoinAirway,
            JoinRadialOutboundCommand => CanonicalCommandType.JoinRadialOutbound,
            JoinRadialInboundCommand => CanonicalCommandType.JoinRadialInbound,
            HoldingPatternCommand => CanonicalCommandType.HoldingPattern,
            PositionTurnAltitudeClearanceCommand => CanonicalCommandType.PositionTurnAltitudeClearance,
            ClimbViaCommand => CanonicalCommandType.ClimbVia,
            DescendViaCommand => CanonicalCommandType.DescendVia,
            CrossFixCommand => CanonicalCommandType.CrossFix,
            DepartFixCommand => CanonicalCommandType.DepartFix,
            ListApproachesCommand => CanonicalCommandType.ListApproaches,
            ClearedVisualApproachCommand => CanonicalCommandType.ClearedVisualApproach,
            ReportFieldInSightCommand => CanonicalCommandType.ReportFieldInSight,
            ReportTrafficInSightCommand => CanonicalCommandType.ReportTrafficInSight,
            WaitCommand => CanonicalCommandType.Wait,
            WaitDistanceCommand => CanonicalCommandType.WaitDistance,
            DeleteQueuedCommand => CanonicalCommandType.DeleteQueuedCommands,
            ShowQueuedCommand => CanonicalCommandType.ShowQueuedCommands,
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
            ResumeNormalSpeedCommand => TrackedCommandType.Immediate,
            ReduceToFinalApproachSpeedCommand => TrackedCommandType.Speed,
            DeleteSpeedRestrictionsCommand => TrackedCommandType.Immediate,
            ExpediteCommand => TrackedCommandType.Immediate,
            NormalRateCommand => TrackedCommandType.Immediate,
            MachCommand => TrackedCommandType.Speed,
            ForceHeadingCommand => TrackedCommandType.Immediate,
            ForceAltitudeCommand => TrackedCommandType.Immediate,
            ForceSpeedCommand => TrackedCommandType.Immediate,
            WarpCommand => TrackedCommandType.Immediate,
            WarpGroundCommand => TrackedCommandType.Immediate,
            DirectToCommand => TrackedCommandType.Navigation,
            ForceDirectToCommand => TrackedCommandType.Navigation,
            AppendDirectToCommand => TrackedCommandType.Navigation,
            AppendForceDirectToCommand => TrackedCommandType.Navigation,
            ExpectApproachCommand => TrackedCommandType.Immediate,
            WaitCommand => TrackedCommandType.Wait,
            WaitDistanceCommand => TrackedCommandType.Wait,
            ClearedApproachCommand => TrackedCommandType.Immediate,
            JoinApproachCommand => TrackedCommandType.Immediate,
            ClearedApproachStraightInCommand => TrackedCommandType.Immediate,
            JoinApproachStraightInCommand => TrackedCommandType.Immediate,
            JoinFinalApproachCourseCommand => TrackedCommandType.Immediate,
            JoinStarCommand => TrackedCommandType.Navigation,
            JoinAirwayCommand => TrackedCommandType.Navigation,
            JoinRadialOutboundCommand => TrackedCommandType.Navigation,
            JoinRadialInboundCommand => TrackedCommandType.Navigation,
            HoldingPatternCommand => TrackedCommandType.Navigation,
            PositionTurnAltitudeClearanceCommand => TrackedCommandType.Immediate,
            ClimbViaCommand => TrackedCommandType.Altitude,
            DescendViaCommand => TrackedCommandType.Altitude,
            CrossFixCommand => TrackedCommandType.Navigation,
            DepartFixCommand => TrackedCommandType.Navigation,
            ListApproachesCommand => TrackedCommandType.Immediate,
            ClearedVisualApproachCommand => TrackedCommandType.Immediate,
            ReportFieldInSightCommand => TrackedCommandType.Immediate,
            ReportTrafficInSightCommand => TrackedCommandType.Immediate,
            DeleteQueuedCommand => TrackedCommandType.Immediate,
            ShowQueuedCommand => TrackedCommandType.Immediate,
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
            SpeedCommand cmd => FormatSpeedCanonical(cmd),
            ResumeNormalSpeedCommand => "RNS",
            ReduceToFinalApproachSpeedCommand => "RFAS",
            DeleteSpeedRestrictionsCommand => "DSR",
            ExpediteCommand exp => exp.UntilAltitude is not null ? $"EXP {exp.UntilAltitude / 100}" : "EXP",
            NormalRateCommand => "NORM",
            MachCommand mach => $"MACH {mach.MachNumber:F2}",
            ForceHeadingCommand cmd => $"FHN {cmd.Heading:000}",
            ForceAltitudeCommand cmd => $"CMN {cmd.Altitude}",
            ForceSpeedCommand cmd => $"SPDN {cmd.Speed}",
            WarpCommand cmd => $"WARP {cmd.PositionLabel} {cmd.Heading:000} {cmd.Altitude} {cmd.Speed}",
            WarpGroundCommand cmd => cmd.NodeId is int nid ? $"WARPG #{nid}"
            : cmd.ParkingName is string p ? $"WARPG @{p}"
            : $"WARPG {cmd.Taxiway1} {cmd.Taxiway2}",
            DirectToCommand cmd => $"DCT {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            ForceDirectToCommand cmd => $"DCTF {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            AppendDirectToCommand cmd => $"ADCT {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            AppendForceDirectToCommand cmd => $"ADCTF {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            ExpectApproachCommand cmd => $"EAPP {cmd.ApproachId}{(cmd.AirportCode is not null ? $" {cmd.AirportCode}" : "")}",
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
            ClearedToLandCommand => "CLAND",
            LandAndHoldShortCommand cmd => $"LAHSO {cmd.CrossingRunwayId}",
            CancelLandingClearanceCommand => "CLC",
            GoAroundCommand ga => FormatGaCanonical(ga),
            EnterLeftDownwindCommand eld => eld.RunwayId is not null ? $"ELD {eld.RunwayId}" : "ELD",
            EnterRightDownwindCommand erd => erd.RunwayId is not null ? $"ERD {erd.RunwayId}" : "ERD",
            EnterLeftCrosswindCommand elc => elc.RunwayId is not null ? $"ELC {elc.RunwayId}" : "ELC",
            EnterRightCrosswindCommand erc => erc.RunwayId is not null ? $"ERC {erc.RunwayId}" : "ERC",
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
            PatternSizeCommand ps => $"PS {ps.SizeNm:G}",
            MakeNormalApproachCommand => "MNA",
            Cancel270Command => "NO270",
            MakeLeftSTurnsCommand mls => mls.Count != 2 ? $"MLS {mls.Count}" : "MLS",
            MakeRightSTurnsCommand mrs => mrs.Count != 2 ? $"MRS {mrs.Count}" : "MRS",
            Plan270Command => "P270",
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
            AirTaxiCommand atxi => atxi.Destination is not null ? $"ATXI {atxi.Destination}" : "ATXI",
            LandCommand land => land.IsTaxiway ? $"LAND {land.SpotName}" : $"LAND @{land.SpotName}",
            ClearedTakeoffPresentCommand => "CTOPP",
            PushbackCommand push => FormatPushCanonical(push),
            TaxiCommand taxi => FormatTaxiCanonical(taxi),
            HoldPositionCommand => "HOLD",
            ResumeCommand => "RES",
            CrossRunwayCommand cross => $"CROSS {cross.RunwayId}",
            HoldShortCommand hs => $"HS {hs.Target}",
            FollowCommand follow => $"FOLLOW {follow.TargetCallsign}",
            GiveWayCommand gw => $"GIVEWAY {gw.TargetCallsign}",
            ExitLeftCommand => "EL",
            ExitRightCommand => "ER",
            ExitTaxiwayCommand et => $"EXIT {et.Taxiway}",
            TaxiAllCommand taxiAll => FormatTaxiAllCanonical(taxiAll),
            BreakConflictCommand => "BREAK",
            GoCommand => "GO",
            SayCommand say => $"SAY {say.Text}",
            SaySpeedCommand => "SSPD",
            SayMachCommand => "SMACH",
            ClearedApproachCommand cmd => FormatCappCanonical(cmd),
            JoinApproachCommand cmd => $"JAPP {cmd.ApproachId}{(cmd.AirportCode is not null ? $" {cmd.AirportCode}" : "")}",
            ClearedApproachStraightInCommand cmd => $"CAPPSI {cmd.ApproachId}{(cmd.AirportCode is not null ? $" {cmd.AirportCode}" : "")}",
            JoinApproachStraightInCommand cmd => $"JAPPSI {cmd.ApproachId}{(cmd.AirportCode is not null ? $" {cmd.AirportCode}" : "")}",
            JoinFinalApproachCourseCommand cmd => cmd.ApproachId is not null ? $"JFAC {cmd.ApproachId}" : "JFAC",
            JoinStarCommand cmd => cmd.Transition is not null ? $"JARR {cmd.StarId} {cmd.Transition}" : $"JARR {cmd.StarId}",
            JoinAirwayCommand cmd => $"JAWY {cmd.AirwayId}",
            JoinRadialOutboundCommand cmd => $"JRADO {cmd.FixName}{cmd.Radial:000}",
            JoinRadialInboundCommand cmd => $"JRADI {cmd.FixName}{cmd.Radial:000}",
            HoldingPatternCommand cmd => FormatHoldCanonical(cmd),
            PositionTurnAltitudeClearanceCommand cmd => $"PTAC {cmd.Heading:000} {cmd.Altitude:000} {cmd.ApproachId}",
            ClimbViaCommand cmd => cmd.Altitude is not null ? $"CVIA {cmd.Altitude}" : "CVIA",
            DescendViaCommand cmd => cmd.Speed is not null ? $"DVIA SPD {cmd.Speed} {cmd.SpeedFixName}"
            : cmd.Altitude is not null ? $"DVIA {cmd.Altitude}"
            : "DVIA",
            CrossFixCommand cmd => FormatCfixCanonical(cmd),
            DepartFixCommand cmd => $"DEPART {cmd.FixName} {cmd.Heading:000}",
            ListApproachesCommand cmd => cmd.AirportCode is not null ? $"APPS {cmd.AirportCode}" : "APPS",
            ClearedVisualApproachCommand cmd => FormatCvaCanonical(cmd),
            ReportFieldInSightCommand => "RFIS",
            ReportTrafficInSightCommand cmd => cmd.TargetCallsign is not null ? $"RTIS {cmd.TargetCallsign}" : "RTIS",
            DeleteQueuedCommand del => del.BlockNumber is not null ? $"DELAT {del.BlockNumber}" : "DELAT",
            ShowQueuedCommand => "SHOWAT",
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
            SpeedCommand cmd => FormatSpeedNatural(cmd),
            ResumeNormalSpeedCommand => "Resume normal speed",
            ReduceToFinalApproachSpeedCommand => "Reduce to final approach speed",
            DeleteSpeedRestrictionsCommand => "Delete speed restrictions",
            ExpediteCommand exp => exp.UntilAltitude is not null ? $"Expedite through {exp.UntilAltitude:N0}" : "Expedite climb/descent",
            NormalRateCommand => "Resume normal rate",
            MachCommand mach => $"Maintain Mach {mach.MachNumber:F2}",
            ForceHeadingCommand cmd => $"Force heading {cmd.Heading:000}",
            ForceAltitudeCommand cmd => $"Force altitude {cmd.Altitude:N0}",
            ForceSpeedCommand cmd => $"Force speed {cmd.Speed}",
            WarpCommand cmd => $"Warp to {cmd.PositionLabel}, heading {cmd.Heading:000}, {cmd.Altitude:N0} ft, {cmd.Speed} kts",
            WarpGroundCommand cmd => cmd.NodeId is int nid2 ? $"Warp to node #{nid2}"
            : cmd.ParkingName is string p2 ? $"Warp to parking {p2}"
            : $"Warp to {cmd.Taxiway1}/{cmd.Taxiway2} intersection",
            DirectToCommand cmd => $"Proceed direct {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            ForceDirectToCommand cmd => $"Force direct {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            AppendDirectToCommand cmd => $"Then direct {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            AppendForceDirectToCommand cmd => $"Then direct {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            ExpectApproachCommand cmd => $"Expect {cmd.ApproachId} approach{(cmd.AirportCode is not null ? $" at {cmd.AirportCode}" : "")}",
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
            LandAndHoldShortCommand cmd => $"Cleared to land, hold short runway {cmd.CrossingRunwayId}",
            CancelLandingClearanceCommand => "Cancel landing clearance",
            GoAroundCommand ga => DescribeGaNatural(ga),
            EnterLeftDownwindCommand eld => DescribePatternEntryNatural("left downwind", eld.RunwayId, null),
            EnterRightDownwindCommand erd => DescribePatternEntryNatural("right downwind", erd.RunwayId, null),
            EnterLeftCrosswindCommand elc => DescribePatternEntryNatural("left crosswind", elc.RunwayId, null),
            EnterRightCrosswindCommand erc => DescribePatternEntryNatural("right crosswind", erc.RunwayId, null),
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
            PatternSizeCommand ps => $"Pattern size {ps.SizeNm:G} NM",
            MakeNormalApproachCommand => "Make normal approach",
            Cancel270Command => "Cancel 270",
            MakeLeftSTurnsCommand mls => $"S-turns, initial left, {mls.Count}",
            MakeRightSTurnsCommand mrs => $"S-turns, initial right, {mrs.Count}",
            Plan270Command => "Plan 270 at next turn",
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
            AirTaxiCommand atxi => atxi.Destination is not null ? $"Air taxi to {atxi.Destination}" : "Air taxi",
            LandCommand land => land.IsTaxiway ? $"Land on taxiway {land.SpotName}" : $"Land at {land.SpotName}",
            ClearedTakeoffPresentCommand => "Cleared for takeoff, present position",
            PushbackCommand push => FormatPushNatural(push),
            TaxiCommand taxi => FormatTaxiNatural(taxi),
            HoldPositionCommand => "Hold position",
            ResumeCommand => "Resume taxi",
            CrossRunwayCommand cross => $"Cross runway {cross.RunwayId}",
            HoldShortCommand hs => $"Hold short of {hs.Target}",
            FollowCommand follow => $"Follow {follow.TargetCallsign}",
            GiveWayCommand gw => $"Give way to {gw.TargetCallsign}",
            ExitLeftCommand => "Exit left",
            ExitRightCommand => "Exit right",
            ExitTaxiwayCommand et => $"Exit at {et.Taxiway}",
            TaxiAllCommand taxiAll => FormatTaxiAllNatural(taxiAll),
            BreakConflictCommand => "Break conflict",
            GoCommand => "Begin takeoff roll",
            SayCommand say => say.Text,
            SaySpeedCommand => "Say speed",
            SayMachCommand => "Say mach",
            UnsupportedCommand cmd => cmd.RawText,
            ClearedApproachCommand cmd => FormatCappNatural(cmd),
            JoinApproachCommand cmd => $"Join {cmd.ApproachId} approach{(cmd.AirportCode is not null ? $" at {cmd.AirportCode}" : "")}",
            ClearedApproachStraightInCommand cmd => $"Cleared straight-in {cmd.ApproachId} approach",
            JoinApproachStraightInCommand cmd => $"Join straight-in {cmd.ApproachId} approach",
            JoinFinalApproachCourseCommand cmd => cmd.ApproachId is not null
                ? $"Join final approach course, {cmd.ApproachId}"
                : "Join final approach course",
            JoinStarCommand cmd => cmd.Transition is not null ? $"Join {cmd.StarId} arrival via {cmd.Transition}" : $"Join {cmd.StarId} arrival",
            JoinAirwayCommand cmd => $"Join airway {cmd.AirwayId}",
            JoinRadialOutboundCommand cmd => $"Join {cmd.FixName} {cmd.Radial:000} radial outbound",
            JoinRadialInboundCommand cmd => $"Join {cmd.FixName} {cmd.Radial:000} radial inbound",
            HoldingPatternCommand cmd => FormatHoldNatural(cmd),
            PositionTurnAltitudeClearanceCommand cmd =>
                $"Fly heading {cmd.Heading:000}, maintain {cmd.Altitude:N0}, cleared {cmd.ApproachId} approach",
            ClimbViaCommand cmd => cmd.Altitude is not null ? $"Climb via SID, except maintain {cmd.Altitude:N0}" : "Climb via SID",
            DescendViaCommand cmd => cmd.Speed is not null ? $"Descend via STAR, {cmd.Speed} knots at {cmd.SpeedFixName}"
            : cmd.Altitude is not null ? $"Descend via STAR, except maintain {cmd.Altitude:N0}"
            : "Descend via STAR",
            CrossFixCommand cmd => FormatCfixNatural(cmd),
            DepartFixCommand cmd => $"Depart {cmd.FixName}, fly heading {cmd.Heading:000}",
            ListApproachesCommand cmd => cmd.AirportCode is not null ? $"List approaches for {cmd.AirportCode}" : "List approaches",
            ClearedVisualApproachCommand cmd => FormatCvaNatural(cmd),
            ReportFieldInSightCommand => "Report field in sight",
            ReportTrafficInSightCommand cmd => cmd.TargetCallsign is not null
                ? $"Report traffic in sight, {cmd.TargetCallsign}"
                : "Report traffic in sight",
            DeleteQueuedCommand del => del.BlockNumber is not null ? $"Delete queued block {del.BlockNumber}" : "Delete all queued commands",
            ShowQueuedCommand => "Show queued commands",
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
                or LandAndHoldShortCommand
                or CancelLandingClearanceCommand
                or GoAroundCommand
                or EnterLeftDownwindCommand
                or EnterRightDownwindCommand
                or EnterLeftCrosswindCommand
                or EnterRightCrosswindCommand
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
                or MakeNormalApproachCommand
                or Cancel270Command
                or MakeLeftSTurnsCommand
                or MakeRightSTurnsCommand
                or Plan270Command
                or PatternSizeCommand
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
                or ExitTaxiwayCommand
                or ClearedApproachCommand
                or JoinApproachCommand
                or ClearedApproachStraightInCommand
                or JoinApproachStraightInCommand
                or JoinFinalApproachCourseCommand
                or PositionTurnAltitudeClearanceCommand
                or ClearedVisualApproachCommand;
    }

    internal static bool IsGroundCommand(ParsedCommand command)
    {
        return command
            is PushbackCommand
                or TaxiCommand
                or TaxiAllCommand
                or HoldPositionCommand
                or ResumeCommand
                or CrossRunwayCommand
                or HoldShortCommand
                or FollowCommand
                or GiveWayCommand
                or BreakConflictCommand;
    }

    private static string FormatSpeedCanonical(SpeedCommand cmd)
    {
        return cmd.Modifier switch
        {
            SpeedModifier.Floor => $"SPD {cmd.Speed}+",
            SpeedModifier.Ceiling => $"SPD {cmd.Speed}-",
            _ => $"SPD {cmd.Speed}",
        };
    }

    private static string FormatSpeedNatural(SpeedCommand cmd)
    {
        return cmd.Modifier switch
        {
            SpeedModifier.Floor => $"Maintain {cmd.Speed} knots or greater",
            SpeedModifier.Ceiling => $"Do not exceed {cmd.Speed} knots",
            _ => $"Speed {cmd.Speed} knots",
        };
    }

    private static string FormatPushCanonical(PushbackCommand push)
    {
        if (push.DestinationParking is not null || push.DestinationSpot is not null)
        {
            var prefix = push.DestinationSpot is not null ? "$" : "@";
            var name = push.DestinationSpot ?? push.DestinationParking;
            var result = $"PUSH {prefix}{name}";
            if (push.FacingTaxiway is not null)
            {
                result += $" {push.FacingTaxiway}";
            }
            else if (push.Heading is not null)
            {
                result += $" {push.Heading:000}";
            }

            return result;
        }

        if (push.Taxiway is not null && push.Heading is not null)
        {
            return $"PUSH {push.Taxiway} {push.Heading:000}";
        }

        if (push.Taxiway is not null)
        {
            return $"PUSH {push.Taxiway}";
        }

        if (push.Heading is not null)
        {
            return $"PUSH {push.Heading:000}";
        }

        return "PUSH";
    }

    private static string FormatPushNatural(PushbackCommand push)
    {
        if (push.DestinationParking is not null || push.DestinationSpot is not null)
        {
            var name = push.DestinationSpot ?? push.DestinationParking;
            var msg = $"Push to {name}";
            if (push.FacingTaxiway is not null)
            {
                msg += $" facing {push.FacingTaxiway}";
            }
            else if (push.Heading is not null)
            {
                msg += $", heading {push.Heading:000}";
            }

            return msg;
        }

        var result = "Pushback";
        if (push.Taxiway is not null)
        {
            result += $" onto {push.Taxiway}";
        }

        if (push.Heading is not null)
        {
            result += $", face heading {push.Heading:000}";
        }

        return result;
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
            ClosedTrafficDeparture { Direction: PatternDirection.Right, RunwayId: { } rwyR } => $" MRT {rwyR}",
            ClosedTrafficDeparture { Direction: PatternDirection.Right } => " MRT",
            ClosedTrafficDeparture { RunwayId: { } rwyL } => $" MLT {rwyL}",
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
            ClosedTrafficDeparture ct when ct.RunwayId is not null =>
                $", make {(ct.Direction == PatternDirection.Left ? "left" : "right")} traffic runway {ct.RunwayId}",
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

    private static string FormatCappCanonical(ClearedApproachCommand cmd)
    {
        var parts = new List<string> { cmd.Force ? "CAPPF" : "CAPP" };
        if (cmd.AtFix is not null)
        {
            parts.Add($"AT {cmd.AtFix}");
        }
        if (cmd.DctFix is not null)
        {
            parts.Add($"DCT {cmd.DctFix}");
        }
        if (cmd.CrossFixAltitude is not null && cmd.CrossFixAltType is not null)
        {
            string prefix = cmd.CrossFixAltType switch
            {
                CrossFixAltitudeType.AtOrAbove => "A",
                CrossFixAltitudeType.AtOrBelow => "B",
                _ => "",
            };
            parts.Add($"CFIX {prefix}{cmd.CrossFixAltitude / 100:000}");
        }
        if (cmd.ApproachId is not null)
        {
            parts.Add(cmd.ApproachId);
        }
        return string.Join(' ', parts);
    }

    private static string FormatCappNatural(ClearedApproachCommand cmd)
    {
        var msg = "Cleared";
        if (cmd.AtFix is not null)
        {
            msg += $" at {cmd.AtFix},";
        }
        if (cmd.DctFix is not null)
        {
            msg += $" direct {cmd.DctFix},";
        }
        if (cmd.CrossFixAltitude is not null && cmd.CrossFixAltType is not null)
        {
            string altWord = cmd.CrossFixAltType switch
            {
                CrossFixAltitudeType.AtOrAbove => " at or above",
                CrossFixAltitudeType.AtOrBelow => " at or below",
                _ => "",
            };
            msg += $" cross{altWord} {cmd.CrossFixAltitude:N0},";
        }
        msg += $" {cmd.ApproachId} approach";
        return msg;
    }

    private static string FormatHoldCanonical(HoldingPatternCommand cmd)
    {
        var unit = cmd.IsMinuteBased ? "M" : "";
        var dir = cmd.Direction == TurnDirection.Left ? "L" : "R";
        var entry = cmd.Entry switch
        {
            HoldingEntry.Direct => " D",
            HoldingEntry.Teardrop => " T",
            HoldingEntry.Parallel => " P",
            _ => "",
        };
        return $"HOLDP {cmd.FixName} {cmd.InboundCourse:000} {cmd.LegLength}{unit} {dir}{entry}";
    }

    private static string FormatHoldNatural(HoldingPatternCommand cmd)
    {
        var dir = cmd.Direction == TurnDirection.Left ? "left" : "right";
        var unit = cmd.IsMinuteBased ? "minute" : "nm";
        var entry = cmd.Entry switch
        {
            HoldingEntry.Direct => ", direct entry",
            HoldingEntry.Teardrop => ", teardrop entry",
            HoldingEntry.Parallel => ", parallel entry",
            _ => "",
        };
        return $"Hold at {cmd.FixName}, {cmd.InboundCourse:000} inbound, {cmd.LegLength}{unit} legs, {dir} turns{entry}";
    }

    private static string FormatCfixCanonical(CrossFixCommand cmd)
    {
        string prefix = cmd.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => "A",
            CrossFixAltitudeType.AtOrBelow => "B",
            _ => "",
        };
        var alt = $"{prefix}{cmd.Altitude / 100:000}";
        return cmd.Speed is not null ? $"CFIX {cmd.FixName} {alt} {cmd.Speed}" : $"CFIX {cmd.FixName} {alt}";
    }

    private static string FormatCfixNatural(CrossFixCommand cmd)
    {
        string altWord = cmd.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => "at or above",
            CrossFixAltitudeType.AtOrBelow => "at or below",
            _ => "at",
        };
        var msg = $"Cross {cmd.FixName} {altWord} {cmd.Altitude:N0}";
        if (cmd.Speed is not null)
        {
            msg += $", {cmd.Speed} knots";
        }
        return msg;
    }

    private static string FormatTaxiCanonical(TaxiCommand taxi)
    {
        var parts = new List<string> { "TAXI" };
        parts.AddRange(taxi.Path);
        if (taxi.DestinationSpot is not null)
        {
            parts.Add($"${taxi.DestinationSpot}");
        }
        else if (taxi.DestinationParking is not null)
        {
            parts.Add($"@{taxi.DestinationParking}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatTaxiNatural(TaxiCommand taxi)
    {
        var displayPath = taxi.Path.Where(t => !TaxiPathfinder.IsNodeReference(t)).ToList();

        if (taxi.DestinationSpot is not null)
        {
            return displayPath.Count > 0
                ? $"Taxi via {string.Join(" ", displayPath)} to spot {taxi.DestinationSpot}"
                : $"Taxi to spot {taxi.DestinationSpot}";
        }

        if (taxi.DestinationParking is not null)
        {
            return displayPath.Count > 0
                ? $"Taxi via {string.Join(" ", displayPath)} to parking {taxi.DestinationParking}"
                : $"Taxi to parking {taxi.DestinationParking}";
        }

        return displayPath.Count > 0 ? $"Taxi via {string.Join(" ", displayPath)}" : "Taxi";
    }

    private static string FormatTaxiAllCanonical(TaxiAllCommand taxiAll)
    {
        if (taxiAll.DestinationSpot is not null)
        {
            return $"TAXIALL ${taxiAll.DestinationSpot}";
        }

        if (taxiAll.DestinationParking is not null)
        {
            return $"TAXIALL @{taxiAll.DestinationParking}";
        }

        return taxiAll.DestinationRunway is not null ? $"TAXIALL {taxiAll.DestinationRunway}" : "TAXIALL";
    }

    private static string FormatTaxiAllNatural(TaxiAllCommand taxiAll)
    {
        if (taxiAll.DestinationSpot is not null)
        {
            return $"Taxi all to spot {taxiAll.DestinationSpot}";
        }

        if (taxiAll.DestinationParking is not null)
        {
            return $"Taxi all to parking {taxiAll.DestinationParking}";
        }

        return taxiAll.DestinationRunway is not null ? $"Taxi all to runway {taxiAll.DestinationRunway}" : "Taxi all";
    }

    private static string FormatCvaCanonical(ClearedVisualApproachCommand cmd)
    {
        var parts = new List<string> { "CVA", cmd.RunwayId };
        if (cmd.AirportCode is not null)
        {
            parts.Add(cmd.AirportCode);
        }
        if (cmd.TrafficDirection is PatternDirection.Left)
        {
            parts.Add("LEFT");
        }
        else if (cmd.TrafficDirection is PatternDirection.Right)
        {
            parts.Add("RIGHT");
        }
        if (cmd.FollowCallsign is not null)
        {
            parts.Add($"FOLLOW {cmd.FollowCallsign}");
        }
        return string.Join(' ', parts);
    }

    private static string FormatCvaNatural(ClearedVisualApproachCommand cmd)
    {
        var msg = $"Cleared visual approach runway {cmd.RunwayId}";
        if (cmd.TrafficDirection is PatternDirection.Left)
        {
            msg += ", left traffic";
        }
        else if (cmd.TrafficDirection is PatternDirection.Right)
        {
            msg += ", right traffic";
        }
        if (cmd.FollowCallsign is not null)
        {
            msg += $", follow {cmd.FollowCallsign}";
        }
        return msg;
    }
}
