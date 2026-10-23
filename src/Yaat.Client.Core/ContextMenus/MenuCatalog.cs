using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Every context-menu action the catalog knows, one <see cref="MenuCatalogEntry"/> per <see cref="MenuIds"/>
/// identifier. A leaf's builder sends its command text through <see cref="IMenuHost.SendAsync"/>; an input leaf
/// opens the host's input popup and formats the submitted text into the command; a list or filtered-list picker opens
/// the host's list popup over values the catalog computes (fixes, runways, STARs, airways) and formats the
/// pick into the command, choosing its form from the data present when the menu is built; a heading entry opens the heading
/// flyout preset to its verb (<see cref="HeadingFlyout"/>); Maintain altitude, Temporary altitude and Cruise open the altitude
/// flyouts (<see cref="AltitudeFlyout"/>); Assign speed and Speed… open the speed flyout (<see cref="SpeedFlyout"/>); an
/// approach entry opens its verb's approach flyout
/// (<see cref="ApproachFlyout"/>); a host leaf asks the host for the item itself,
/// for the entries that open a host surface or act on the surface — the Warp flyout, the flight-plan editor, route and
/// push-route drawing, and the ground's hold-short, follow, give-way, push-back-to and preset-taxi submenus over the
/// choices the host answers (the pushback faces are flat items of a companion helper, <see cref="BuildPushbackFaces"/>),
/// and so is the delayed spawn's Change spawn
/// delay submenu (<see cref="BuildSpawnDelay"/>), whose free-text box closes the menu it sits in; and the Cleared for
/// takeoff submenu is the same on every view: the default clearance and runway heading, the VFR departure instructions
/// when the aircraft and the controller's VFR-for-IFR setting allow them, and a free-text item last. A pattern entry is a leaf naming the
/// assigned runway, else a runway picker, else free text, and each pattern maneuver applies only on the legs it fits.
/// Line up and wait names the held runway, else the assigned one, on every view
/// (<see cref="HoldShortMenuHelper.HeldRunway(IMenuAircraft?)"/>). Line up and wait, Cleared for takeoff and its
/// runway-bearing header, cancel takeoff, resume taxi, cross runway and the release-window
/// check are the same on every view. The relative ground items send as the previous selection. A delayed spawn instead
/// offers Spawn now, the Change spawn delay submenu and Delete (<see cref="SharedMenuGroups.AddDelayedSpawn"/>), and a
/// selection of two or more assumable shadows adds "Assume selected live traffic (N)" after Delete
/// (<see cref="SharedMenuGroups.AddAssumeSelected"/>, built by <see cref="BuildAssumeSelected"/>).
/// </summary>
public static class MenuCatalog
{
    /// <summary>The approach entries, in Approach submenu order, each opening its verb's approach flyout (<see cref="ApproachSpec"/>).</summary>
    internal static IReadOnlyList<string> ApproachPickerIds { get; } =
    [
        MenuIds.ApproachCleared,
        MenuIds.ApproachJoin,
        MenuIds.ApproachClearedStraightIn,
        MenuIds.ApproachJoinStraightIn,
        MenuIds.ApproachClearedForce,
        MenuIds.ApproachJoinForce,
        MenuIds.ApproachJoinFinalCourse,
        MenuIds.ApproachExpect,
    ];

    /// <summary>Every catalog entry, in menu order within each group; reads <see cref="ApproachPickerIds"/>, declared first.</summary>
    public static IReadOnlyList<MenuCatalogEntry> All { get; } =
    [
        new(
            MenuIds.FavoritesMenu,
            "Favorite Commands",
            MenuFlightRules.Both,
            Always,
            (aircraft, context, host) => host.BuildFavorites(aircraft, context)
        ),
        Leaf(MenuIds.LiveTrafficAssume, "Assume control", "ASSUME", (ac, _) => AircraftCommandApplicability.CanAssume(ac)),
        new(
            MenuIds.LiveTrafficAssumeAndTrack,
            "Assume and track",
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanAssume(ac),
            (_, context, host) => BuildAssumeAndTrack(context, host)
        ),
        Leaf(MenuIds.LiveTrafficUnassume, "Release to live feed", "UNASSUME", (ac, _) => AircraftCommandApplicability.CanUnassume(ac)),
        Leaf(MenuIds.TrackTrack, "Initiate Track", "TRACK", (ac, _) => AircraftCommandApplicability.IsUntracked(ac)),
        Leaf(MenuIds.TrackDrop, "Drop track", "DROP", (ac, _) => AircraftCommandApplicability.IsOwned(ac)),
        Leaf(MenuIds.TrackAcceptHandoff, "Accept handoff", "ACCEPT", (ac, _) => AircraftCommandApplicability.HasHandoffInProgress(ac)),
        FlyoutEntry(
            MenuIds.TrackInitiateHandoff,
            "Initiate handoff…",
            (ac, _) => AircraftCommandApplicability.IsOwned(ac),
            (label, ac, context, host) => CoordinationFlyout.Build(label, CoordinationVerb.Handoff, ac, context, host)
        ),
        Leaf(MenuIds.TrackCancelHandoff, "Cancel handoff", "CANCEL", (ac, _) => AircraftCommandApplicability.HasHandoffInProgress(ac)),
        FlyoutEntry(
            MenuIds.TrackPointOut,
            "Point out…",
            (ac, _) => AircraftCommandApplicability.IsOwned(ac),
            (label, ac, context, host) => CoordinationFlyout.Build(label, CoordinationVerb.PointOut, ac, context, host)
        ),
        Leaf(MenuIds.TrackAcknowledgePointout, "Acknowledge pointout", "OK", (ac, _) => AircraftCommandApplicability.HasPendingPointout(ac)),
        HostLeaf(MenuIds.SquawkCode, "Squawk…", Always, SharedMenuGroups.SquawkFlyout),
        Leaf(MenuIds.SquawkAssigned, "Squawk assigned code", "SQ", (ac, _) => AircraftCommandApplicability.HasAssignedBeaconCode(ac)),
        Leaf(MenuIds.SquawkRandom, "Squawk random", "RANDSQ", Always),
        Leaf(MenuIds.SquawkVfr, "Squawk VFR", "SQVFR", Always),
        Leaf(MenuIds.SquawkNormal, "Squawk normal", "SQNORM", Always),
        Leaf(MenuIds.SquawkStandby, "Squawk standby", "SQSBY", Always),
        Leaf(MenuIds.SquawkIdent, "Ident", "IDENT", Always),
        Leaf(MenuIds.AskPilotAltitude, "Altitude", "SALT", CanAskPilot),
        Leaf(MenuIds.AskPilotHeading, "Heading", "SHDG", CanAskPilot),
        Leaf(MenuIds.AskPilotSpeed, "Speed", "SSPD", CanAskPilot),
        Leaf(MenuIds.AskPilotMach, "Mach", "SMACH", CanAskPilot),
        Leaf(MenuIds.AskPilotPosition, "Position", "SPOS", CanAskPilot),
        Leaf(MenuIds.AskPilotExpectedApproach, "Expected approach", "SEAPP", CanAskPilot),
        HostLeaf(MenuIds.AskPilotCustom, "Custom…", CanAskPilot, SharedMenuGroups.AskPilotFlyout),
        Leaf(MenuIds.CoordinationRelease, "Release", "RD", Always),
        Leaf(MenuIds.CoordinationHold, "Hold", "RDH", Always),
        Leaf(MenuIds.CoordinationRecall, "Recall", "RDR", Always),
        Leaf(MenuIds.CoordinationAcknowledge, "Acknowledge release", "RDACK", Always),
        new(
            MenuIds.CoordinationReleaseHeld,
            ReleaseHeldLabel,
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanReleaseHeld(ac),
            (_, context, host) => BuildSend(ReleaseHeldLabel, $"REL {context.Callsign}", context, host)
        ),
        Leaf(
            MenuIds.CoordinationCheckReleaseWindow,
            "Check release window",
            "CFR CHECK",
            (ac, _) => AircraftCommandApplicability.CanCheckReleaseWindow(ac)
        ),
        HostLeaf(MenuIds.DataBlockScratchpad, "Scratchpad…", Always, SharedMenuGroups.ScratchpadFlyout),
        Leaf(MenuIds.DataBlockScratchpadClear1, "Clear Scratchpad 1", "SP1", (ac, _) => !string.IsNullOrEmpty(ac?.Scratchpad1)),
        Leaf(MenuIds.DataBlockScratchpadClear2, "Clear Scratchpad 2", "SP2", (ac, _) => !string.IsNullOrEmpty(ac?.Scratchpad2)),
        AltitudeBoxEntry(MenuIds.DataBlockTempAltitude, "Temporary altitude…", AltitudeBoxKind.TemporaryAltitude),
        AltitudeBoxEntry(MenuIds.DataBlockCruise, "Cruise…", AltitudeBoxKind.Cruise),
        Leaf(MenuIds.DataBlockAnnotate, "Annotate", "ANNOTATE", Always),
        HostLeaf(MenuIds.SimControlWarp, "Warp…", (ac, _) => CanWarp(ac), BuildWarp),
        Leaf(MenuIds.SimControlDelete, "Delete", "DEL", Always),
        HostLeaf(MenuIds.AircraftEditFlightPlan, "Edit flight plan", CanEditFlightPlan, BuildEditFlightPlan),
        HostLeaf(MenuIds.AircraftCommand, "Command…", Always, BuildCommand),
        HostLeaf(MenuIds.AircraftNote, "Note…", Always, BuildNote),
        Leaf(MenuIds.HeadingPresent, "Present heading", "FPH", Always),
        HeadingEntry(MenuIds.HeadingFly, new HeadingFlyoutPreset("Fly heading", HeadingVerb.FlyHeading, HeadingFlyoutFocus.Box)),
        HeadingEntry(MenuIds.HeadingTurnLeft, new HeadingFlyoutPreset("Turn left", HeadingVerb.TurnLeft, HeadingFlyoutFocus.Box)),
        HeadingEntry(MenuIds.HeadingTurnRight, new HeadingFlyoutPreset("Turn right", HeadingVerb.TurnRight, HeadingFlyoutFocus.Box)),
        HeadingEntry(
            MenuIds.HeadingTurnLeftDegrees,
            new HeadingFlyoutPreset("Turn left (degrees)", HeadingVerb.FlyHeading, HeadingFlyoutFocus.LeftChips)
        ),
        HeadingEntry(
            MenuIds.HeadingTurnRightDegrees,
            new HeadingFlyoutPreset("Turn right (degrees)", HeadingVerb.FlyHeading, HeadingFlyoutFocus.RightChips)
        ),
        Picker(MenuIds.AltitudeMaintain, "Maintain altitude", AltitudeFlyout.BuildMaintain),
        Picker(MenuIds.SpeedAssign, "Assign speed", SpeedFlyout.Build),
        Picker(MenuIds.SpeedCustom, "Speed…", SpeedFlyout.Build),
        Leaf(MenuIds.SpeedNormal, "Resume normal speed", "RNS", Always),
        Picker(MenuIds.SpeedFinalApproach, "Reduce to final approach speed", BuildFinalApproachSpeed),
        DirectToEntry(MenuIds.NavigationDirectTo, "Direct to…", DirectToVerb.DirectTo, Always),
        DirectToEntry(MenuIds.NavigationAppendDirectTo, "Append direct to…", DirectToVerb.AppendDirectTo, IsNavigatingToFix),
        Leaf(MenuIds.HoldPresentLeft, "Hold present position (left)", "HPPL", Always),
        Leaf(MenuIds.HoldPresentRight, "Hold present position (right)", "HPPR", Always),
        HoldTurnsEntry(MenuIds.HoldFixLeft, "Left 360s at fix…", HoldTurns.Left),
        HoldTurnsEntry(MenuIds.HoldFixRight, "Right 360s at fix…", HoldTurns.Right),
        .. ApproachPickerIds.Select(ApproachPicker),
        new(
            MenuIds.ApproachClearedVisual,
            ClearedVisualLabel,
            MenuFlightRules.IfrOnly,
            (ac, _) => AircraftCommandApplicability.IsIfr(ac),
            (ac, context, host) => BuildClearedVisual(ClearedVisualLabel, ac, context, host)
        ),
        FlyoutEntry(MenuIds.ApproachReportFieldInSight, "Report field in sight…", Always, FieldAdvisoryFlyout.Build),
        Picker(
            MenuIds.ApproachReportTrafficInSight,
            "Report traffic in sight…",
            (label, ac, context, host) => AirborneTrafficFlyout.BuildReportInSight(label, ac, context, host)
        ),
        ReportWhenEntry(MenuIds.ApproachReportWhen, "Report when…", ReportWhenFocus.MilesBox),
        ReportWhenEntry(MenuIds.ApproachReportNMileFinal, "At N-mile final…", ReportWhenFocus.MilesBox),
        ReportWhenEntry(MenuIds.ApproachReportAtFix, "At fix…", ReportWhenFocus.FixBox),
        Picker(MenuIds.ProceduresJoinStar, JoinStarLabel, BuildJoinStar),
        Leaf(MenuIds.ProceduresClimbViaSid, "Climb via SID", "CVIA", Always),
        Leaf(MenuIds.ProceduresDescendViaStar, "Descend via STAR", "DVIA", Always),
        FlyoutEntry(MenuIds.ProceduresCrossFix, "Cross fix…", Always, FixValueFlyout.BuildCross),
        FlyoutEntry(MenuIds.ProceduresDepartFix, "Depart fix…", Always, FixValueFlyout.BuildDepart),
        FlyoutEntry(MenuIds.ProceduresPtac, "PTAC…", Always, PtacFlyout.Build),
        FlyoutEntry(MenuIds.ProceduresJoinAirway, "Join airway…", Always, JoinAirwayFlyout.Build),
        JoinRadialEntry(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound…", RadialSide.Outbound),
        JoinRadialEntry(MenuIds.ProceduresJoinRadialInbound, "Join radial inbound…", RadialSide.Inbound),
        new(
            MenuIds.TowerLineUpAndWait,
            LineUpAndWaitLabel,
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanLineUpAndWait(ac),
            BuildLineUpAndWait
        ),
        new(
            MenuIds.TowerClearedForTakeoff,
            ClearedForTakeoffLabel,
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanClearForTakeoff(ac),
            BuildClearedForTakeoff
        ),
        Leaf(MenuIds.TowerCancelTakeoff, "Cancel takeoff clearance", "CTOC", (ac, _) => AircraftCommandApplicability.CanCancelTakeoff(ac)),
        RunwayLeaf(MenuIds.TowerClearedToLand, "Cleared to land", "CLAND", CanClearToLand),
        RunwayLeaf(MenuIds.TowerForceLanding, "Force landing", "CLANDF", CanForceLanding),
        RunwayLeaf(MenuIds.TowerClearedOption, "Cleared for the option", "COPT", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerTouchAndGo, "Touch and go", "TG", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerStopAndGo, "Stop and go", "SG", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerLowApproach, "Low approach", "LA", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerGoAround, "Go around", "GA", (ac, _) => AircraftCommandApplicability.CanGoAround(ac)),
        Leaf(MenuIds.TowerCancelLanding, "Cancel landing clearance", "CLC", (ac, _) => AircraftCommandApplicability.CanCancelLandingClearance(ac)),
        ExitEntry(MenuIds.TowerExitLeft, ExitSide.Left),
        ExitEntry(MenuIds.TowerExitRight, ExitSide.Right),
        PatternEntry(MenuIds.PatternEnterLeftDownwind, CanEnterPattern),
        PatternEntry(MenuIds.PatternEnterRightDownwind, CanEnterPattern),
        PatternEntry(MenuIds.PatternEnterLeftBase, CanEnterPattern),
        PatternEntry(MenuIds.PatternEnterRightBase, CanEnterPattern),
        PatternEntry(MenuIds.PatternEnterFinal, (ac, context) => AircraftCommandApplicability.CanEnterFinal(ac, context.VfrCommandsForIfr)),
        Leaf(MenuIds.PatternTurnCrosswind, "Turn crosswind", "TC", OnLeg("Upwind")),
        Leaf(MenuIds.PatternTurnDownwind, "Turn downwind", "TD", OnLeg("Crosswind")),
        Leaf(MenuIds.PatternTurnBase, "Turn base", "TB", OnLeg("Downwind")),
        Leaf(MenuIds.PatternExtend, "Extend pattern leg", "EXT", OnLeg("Upwind", "Crosswind", "Downwind")),
        Leaf(MenuIds.PatternShortApproach, "Make short approach", "MSA", OnLeg("Downwind", "Base")),
        Leaf(MenuIds.PatternNormalApproach, "Make normal approach", "MNA", OnLeg("Downwind", "Base")),
        Leaf(MenuIds.PatternLeft360, "Make left 360", "L360", OnAnyPatternLeg),
        Leaf(MenuIds.PatternRight360, "Make right 360", "R360", OnAnyPatternLeg),
        Leaf(MenuIds.PatternLeft270, "Make left 270", "L270", OnAnyPatternLeg),
        Leaf(MenuIds.PatternRight270, "Make right 270", "R270", OnAnyPatternLeg),
        Leaf(MenuIds.PatternPlan270, "Plan 270 at next turn", "P270", OnLeg("Upwind", "Crosswind", "Downwind", "Base")),
        Leaf(MenuIds.PatternCancel270, "Cancel 270", "NO270", OnLeg("Upwind", "Crosswind", "Downwind", "Base")),
        Leaf(MenuIds.PatternCircleAirport, "Circle airport", "CA", OnAnyPatternLeg),
        Leaf(MenuIds.GroundPushback, "Push back", "PUSH", (ac, _) => AircraftCommandApplicability.CanPushBack(ac)),
        HostLeaf(
            MenuIds.GroundPushbackFace,
            "Push back, face",
            (ac, _) => AircraftCommandApplicability.CanPushBack(ac),
            (_, _, _, _) =>
                throw new InvalidOperationException(
                    $"The '{MenuIds.GroundPushbackFace}' entry builds no item; MenuCatalog.BuildPushbackFaces builds its flat face items."
                )
        ),
        // Push back to… follows a list the background live plan fills: as a submenu through IsSubMenuOpen, as the strip
        // button's flyout through QuickCommandStrip.FollowInFlyout, since the flyout never raises IsSubMenuOpen.
        HostLeaf(
            MenuIds.GroundPushbackTo,
            "Push back to…",
            (ac, _) => AircraftCommandApplicability.CanPushBack(ac),
            (label, _, context, host) => PushbackToMenu.Build(label, host.GetPushbackTargets(context.Callsign), context, host)
        ),
        HostLeaf(MenuIds.GroundPushRoute, "Push route…", (ac, _) => AircraftCommandApplicability.CanPushBack(ac), BuildPushRoute),
        Leaf(MenuIds.GroundHoldPosition, "Hold position", "HOLD", (ac, _) => AircraftCommandApplicability.CanHoldPosition(ac)),
        Leaf(
            MenuIds.GroundResumeTaxi,
            "Resume taxi",
            "RES",
            (ac, _) => (AircraftCommandApplicability.CanResumeFromHoldShort(ac)) || (AircraftCommandApplicability.CanResumeTaxi(ac))
        ),
        new(
            MenuIds.GroundCrossRunway,
            CrossRunwayLabel,
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanCrossRunway(ac),
            BuildCrossRunway
        ),
        Leaf(MenuIds.GroundBreakConflict, "Ignore ground conflicts (15 s)", "BREAK", (ac, _) => AircraftCommandApplicability.CanBreakConflict(ac)),
        HostLeaf(
            MenuIds.GroundHoldShort,
            "Hold short of…",
            (ac, _) => AircraftCommandApplicability.CanHoldShort(ac),
            (label, _, context, host) => HoldShortLadder.Build(label, context, host)
        ),
        HostLeaf(
            MenuIds.GroundFollow,
            "Follow…",
            AircraftCommandApplicability.CanFollowBehind,
            (label, _, context, host) => GroundTrafficFlyout.BuildFollow(label, context, host)
        ),
        HostLeaf(
            MenuIds.GroundGiveWay,
            "Give way to…",
            AircraftCommandApplicability.CanGiveWayTo,
            (label, _, context, host) => GroundTrafficFlyout.BuildGiveWay(label, context, host)
        ),
        HostLeaf(
            MenuIds.GroundTaxiPreset,
            "Preset taxi route",
            (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac),
            (label, _, context, host) => TaxiRouteFlyout.BuildPreset(label, context, host)
        ),
        HostLeaf(
            MenuIds.GroundTaxiToRunway,
            "Taxi to runway",
            (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac),
            (label, _, context, host) => TaxiToRunwayFlyout.BuildTaxiToRunway(label, context, host)
        ),
        HostLeaf(
            MenuIds.GroundAirTaxi,
            "Air taxi",
            (ac, _) => AircraftCommandApplicability.CanAirTaxi(ac),
            (label, ac, context, host) => AirTaxiFlyout.Build(label, AirTaxiVerb.AirTaxi, ac, context, host)
        ),
        HostLeaf(
            MenuIds.GroundLandHelicopter,
            "Land",
            (ac, _) => AircraftCommandApplicability.CanLandHelicopter(ac),
            (label, ac, context, host) => AirTaxiFlyout.Build(label, AirTaxiVerb.Land, ac, context, host)
        ),
        HostLeaf(MenuIds.GroundDrawTaxiRoute, "Draw taxi route…", (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac), BuildDrawRoute),
        Relative(
            MenuIds.RelativeReportInSight,
            "Selected aircraft: report in sight",
            "RTIS",
            RelativeTraffic.OffersAirborneRelative,
            _ => "Report in sight"
        ),
        Relative(MenuIds.RelativeFollow, "Selected aircraft: follow traffic", "FOLLOW", RelativeTraffic.OffersAirborneFollow, _ => "Follow"),
        Relative(
            MenuIds.GroundRelativeGiveWay,
            "Selected aircraft: give way to",
            "GW",
            RelativeTraffic.OffersGroundRelative,
            clicked => $"Give way to {clicked}"
        ),
        Relative(
            MenuIds.GroundRelativeFollow,
            "Selected aircraft: follow",
            "FOLLOWG",
            RelativeTraffic.OffersGroundRelative,
            clicked => $"Follow {clicked}"
        ),
        Leaf(MenuIds.SpawnNow, "Spawn now", "SPAWN", Always),
        HostLeaf(
            MenuIds.SpawnDelay,
            SpawnDelayLabel,
            Always,
            (_, _, _, _) =>
                throw new InvalidOperationException(
                    $"The '{MenuIds.SpawnDelay}' entry builds no item; SharedMenuGroups.AddDelayedSpawn builds its submenu "
                        + "through MenuCatalog.BuildSpawnDelay, which takes the menu its free-text box closes."
                )
        ),
        HostLeaf(
            MenuIds.LiveTrafficAssumeSelected,
            AssumeSelectedLabel,
            Always,
            (_, _, _, _) =>
                throw new InvalidOperationException(
                    $"The '{MenuIds.LiveTrafficAssumeSelected}' entry builds no item; SharedMenuGroups.AddAssumeSelected builds it on every view "
                        + "through MenuCatalog.BuildAssumeSelected, which takes the live-traffic shadows the click's selection holds."
                )
        ),
        PointEntry(MenuIds.PointFlyHeading, "Fly heading", AtPointAirborne, BuildPointFlyHeading),
        PointEntry(
            MenuIds.PointDirectTo,
            "Direct to",
            AtPointAirborne,
            (_, point, context, host) => BuildPointFrd(point, "Direct to", "DCT", context, host)
        ),
        PointEntry(
            MenuIds.PointAppendDirectTo,
            "Append direct to",
            (ac, context) => AtPointAirborne(ac, context) && IsNavigatingToFix(ac, context),
            (_, point, context, host) => BuildPointFrd(point, "Append direct to", "ADCT", context, host)
        ),
        PointEntry(
            MenuIds.PointHoldLeft,
            "Hold (left)",
            AtPointAirborne,
            (_, point, context, host) => BuildPointHold(point, "left", "HFIXL", context, host)
        ),
        PointEntry(
            MenuIds.PointHoldRight,
            "Hold (right)",
            AtPointAirborne,
            (_, point, context, host) => BuildPointHold(point, "right", "HFIXR", context, host)
        ),
        PointEntry(MenuIds.PointTaxiHere, "Taxi here", AtNodeTaxiable, BuildPointTaxiHere),
        PointEntry(
            MenuIds.PointTaxiToRunway,
            "Taxi to runway",
            AtRunwaySurfaceTaxiable,
            (_, point, context, host) => PointTaxiFlyout.BuildTaxiToRunway(point, context, host)
        ),
        PointEntry(MenuIds.PointPushTo, "Push to", AtNodePushable, BuildPointPushTo),
        PointEntry(MenuIds.PointCustomTaxi, "Custom taxi…", AtNodeTaxiable, BuildPointCustomTaxi),
        PointEntry(MenuIds.PointWarpHere, "Warp here", CanWarpHere, BuildPointWarp),
        new(
            MenuIds.PatternMakeLeftTraffic,
            "Make left closed traffic",
            MenuFlightRules.VfrOnly,
            CanFlyVfrManeuverAloft,
            (_, context, host) => BuildSend("Make left closed traffic", "MLT", context, host)
        ),
        new(
            MenuIds.PatternMakeRightTraffic,
            "Make right closed traffic",
            MenuFlightRules.VfrOnly,
            CanFlyVfrManeuverAloft,
            (_, context, host) => BuildSend("Make right closed traffic", "MRT", context, host)
        ),
        new(
            MenuIds.PatternFollow,
            "Follow traffic…",
            MenuFlightRules.VfrOnly,
            CanFlyVfrManeuverAloft,
            (ac, context, host) => AirborneTrafficFlyout.BuildFollow("Follow traffic…", ac, context, host)
        ),
        new(
            MenuIds.NavigationOnCourse,
            "Proceed on course",
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.IsAirborneControllable(ac),
            (_, context, host) => BuildSend("Proceed on course", "OC", context, host)
        ),
        new(
            MenuIds.HoldPattern,
            "Hold at fix…",
            MenuFlightRules.IfrOnly,
            (ac, _) => AircraftCommandApplicability.IsAirborneControllable(ac),
            (ac, context, host) => HoldFlyout.BuildPattern("Hold at fix…", ac, context, host)
        ),
    ];

    /// <summary>
    /// A VFR maneuver in the air: make closed traffic (<c>MLT</c> / <c>MRT</c>, which puts the aircraft into the pattern) and
    /// left or right 360s at a fix (<c>HFIXL</c> / <c>HFIXR</c>), and follow traffic (<c>FOLLOW</c>, whose flyout greys the
    /// traffic not reported in sight). Offered to an IFR aircraft only under the controller's full VFR setting. On the ground
    /// closed traffic only records the pattern side, which the takeoff clearance's closed-traffic modifiers already cover.
    /// </summary>
    private static Func<IMenuAircraft?, MenuContext, bool> CanFlyVfrManeuverAloft =>
        (ac, context) => AircraftCommandApplicability.CanIssuePatternManeuvers(ac, context.VfrCommandsForIfr) && (ac is { IsOnGround: false });

    /// <summary>
    /// A pattern entry's label and the verb it sends, with the runway after it when one is given. A method rather than
    /// a table so that <see cref="All"/>'s initializer can read it whatever the declaration order.
    /// </summary>
    private static (string Label, string Command) PatternEntrySpec(string id) =>
        id switch
        {
            MenuIds.PatternEnterLeftDownwind => ("Enter left downwind", "ELD"),
            MenuIds.PatternEnterRightDownwind => ("Enter right downwind", "ERD"),
            MenuIds.PatternEnterLeftBase => ("Enter left base", "ELB"),
            MenuIds.PatternEnterRightBase => ("Enter right base", "ERB"),
            MenuIds.PatternEnterFinal => ("Make straight-in", "EF"),
            _ => throw new ArgumentException($"'{id}' is not a pattern-entry menu id", nameof(id)),
        };

    /// <summary>The Cleared for takeoff entry's label, the header of the submenu it builds.</summary>
    private const string ClearedForTakeoffLabel = "Cleared for takeoff";

    /// <summary>The Line up and wait entry's label, which the runway it names follows.</summary>
    private const string LineUpAndWaitLabel = "Line up and wait";

    /// <summary>The Cross runway entry's label, which the held runway follows.</summary>
    private const string CrossRunwayLabel = "Cross";

    /// <summary>The Change spawn delay submenu's label, which the delayed-spawn group heads it with.</summary>
    private const string SpawnDelayLabel = "Change spawn delay";

    /// <summary>The assume-selected live-traffic item's label, which the number of the click's selected shadows follows.</summary>
    private const string AssumeSelectedLabel = "Assume selected live traffic";

    /// <summary>The Release (HFR) entry's label, which the header shows under the title on every view.</summary>
    private const string ReleaseHeldLabel = "Release (HFR)";

    /// <summary>The visual-approach entry's label.</summary>
    private const string ClearedVisualLabel = "Cleared visual approach";

    /// <summary>The Join STAR entry's label.</summary>
    private const string JoinStarLabel = "Join STAR";

    /// <summary>The separators a filed route's tokens are split on.</summary>
    private static readonly char[] RouteSeparators = [' ', '.'];

    /// <summary>The step the point menu's Fly heading rounds to.</summary>
    private const int HeadingStep = 5;

    /// <summary>The trailing ellipsis a picker label carries while it opens a popup that is not the plain route-fix list.</summary>
    internal const string Ellipsis = "…";

    private static readonly Dictionary<string, MenuCatalogEntry> ById = All.ToDictionary(e => e.Id, StringComparer.Ordinal);

    /// <summary>The entry for <paramref name="id"/>; throws <see cref="KeyNotFoundException"/> naming it when the catalog has none.</summary>
    public static MenuCatalogEntry Get(string id) =>
        ById.TryGetValue(id, out MenuCatalogEntry? entry)
            ? entry
            : throw new KeyNotFoundException(
                $"The context-menu catalog has no entry with id '{id}'; add it to MenuCatalog.All alongside its MenuIds constant."
            );

    /// <summary>A menu item labelled <paramref name="label"/> that sends <paramref name="command"/> for the menu's aircraft when clicked.</summary>
    internal static MenuItem BuildSend(string label, string command, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        MenuCommandText.SetCommand(item, command);
        item.Click += async (_, _) => await host.SendAsync(context.Callsign, command, context.Initials);
        return item;
    }

    /// <summary>
    /// A <see cref="BuildSend"/> item whose header is <paramref name="header"/> drawn by <paramref name="template"/>, its
    /// UI Automation name the header's one-line text. The caller adds any hover behaviour.
    /// </summary>
    internal static MenuItem BuildTemplatedSend<THeader>(
        THeader header,
        FuncDataTemplate<THeader> template,
        string command,
        MenuContext context,
        IMenuHost host
    )
        where THeader : class
    {
        string text = HeaderText(header);
        MenuItem item = BuildSend(text, command, context, host);
        item.Header = header;
        item.HeaderTemplate = template;
        AutomationProperties.SetName(item, text);
        return item;
    }

    /// <summary>A templated menu row header's one-line text; throws when the header gives none.</summary>
    private static string HeaderText<THeader>(THeader header)
        where THeader : class =>
        header.ToString() ?? throw new InvalidOperationException($"The menu row header {typeof(THeader).Name} gave no one-line text.");

    /// <summary>
    /// A menu item labelled <paramref name="label"/> that sends the controller-authored text <paramref name="command"/>
    /// gives, read again when clicked, for the menu's aircraft through the host's VFR gate
    /// (<see cref="IMenuHost.SendGatedAsync"/>); the item records the text it gives when built.
    /// </summary>
    internal static MenuItem BuildGatedSend(string label, Func<string> command, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        MenuCommandText.SetCommand(item, command());
        item.Click += async (_, _) => await host.SendGatedAsync(context.Callsign, command(), context.Initials);
        return item;
    }

    private static Func<IMenuAircraft?, MenuContext, bool> Always => (_, _) => true;

    private static Func<IMenuAircraft?, MenuContext, bool> CanAskPilot => (ac, _) => AircraftCommandApplicability.CanAskPilot(ac);

    private static Func<IMenuAircraft?, MenuContext, bool> CanEditFlightPlan => (ac, _) => AircraftCommandApplicability.CanEditFlightPlan(ac);

    private static Func<IMenuAircraft?, MenuContext, bool> CanClearToLand => (ac, _) => AircraftCommandApplicability.CanClearToLand(ac);

    /// <summary>
    /// Force landing is an RPO-only override that touches down regardless of the energy state, so it is hidden in solo
    /// training, where the server rejects it.
    /// </summary>
    private static Func<IMenuAircraft?, MenuContext, bool> CanForceLanding =>
        (ac, context) => AircraftCommandApplicability.CanClearToLand(ac) && !context.SoloTrainingMode;

    /// <summary>The option clearances are VFR operations, offered to an IFR aircraft only under the controller's full VFR setting.</summary>
    private static Func<IMenuAircraft?, MenuContext, bool> CanIssueVfrOption =>
        (ac, context) => AircraftCommandApplicability.CanIssueVfrOption(ac, context.VfrCommandsForIfr);

    /// <summary>The circuit-leg entries put the aircraft on a full VFR circuit, offered to an IFR aircraft only under the full VFR setting.</summary>
    private static Func<IMenuAircraft?, MenuContext, bool> CanEnterPattern =>
        (ac, context) => AircraftCommandApplicability.CanEnterPattern(ac, context.VfrCommandsForIfr);

    /// <summary>
    /// A pattern maneuver valid only from the legs named in <paramref name="legs"/> (a leg turn only from the leg before it),
    /// and only while <see cref="AircraftCommandApplicability.CanIssuePatternManeuvers"/> allows maneuvers at all.
    /// </summary>
    private static Func<IMenuAircraft?, MenuContext, bool> OnLeg(params string[] legs) =>
        (ac, context) =>
            AircraftCommandApplicability.CanIssuePatternManeuvers(ac, context.VfrCommandsForIfr)
            && legs.Contains(ac?.CurrentPhase ?? "", StringComparer.Ordinal);

    /// <summary>A pattern maneuver valid from any leg of the circuit (<see cref="AircraftCommandApplicability.IsPatternPhase"/>).</summary>
    private static Func<IMenuAircraft?, MenuContext, bool> OnAnyPatternLeg =>
        (ac, context) =>
            AircraftCommandApplicability.CanIssuePatternManeuvers(ac, context.VfrCommandsForIfr)
            && AircraftCommandApplicability.IsPatternPhase(ac?.CurrentPhase ?? "");

    private static MenuCatalogEntry Leaf(string id, string label, string command, Func<IMenuAircraft?, MenuContext, bool> isApplicable) =>
        new(id, label, MenuFlightRules.Both, isApplicable, (_, context, host) => BuildSend(label, command, context, host));

    /// <summary>A leaf whose text names the aircraft's assigned runway after <paramref name="label"/> when it has one.</summary>
    private static MenuCatalogEntry RunwayLeaf(string id, string label, string command, Func<IMenuAircraft?, MenuContext, bool> isApplicable) =>
        new(id, label, MenuFlightRules.Both, isApplicable, (ac, context, host) => BuildSend(label + RunwaySuffix(ac), command, context, host));

    /// <summary>The assigned runway as a label suffix (" 28R"), or empty when the aircraft has none.</summary>
    private static string RunwaySuffix(IMenuAircraft? aircraft) => DisplaySuffix(aircraft?.AssignedRunway);

    /// <summary><paramref name="runway"/> in display form as a label suffix (" 28R"), or empty when there is none.</summary>
    private static string DisplaySuffix(string? runway) => string.IsNullOrEmpty(runway) ? "" : $" {RunwayIdentifier.ToDisplayDesignator(runway)}";

    /// <summary>Line up and wait, sending the bare verb, its label naming the held runway, else the assigned one.</summary>
    private static MenuItem BuildLineUpAndWait(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildSend(LineUpAndWaitLabel + DisplaySuffix(HoldShortMenuHelper.HeldRunway(aircraft)), "LUAW", context, host);

    /// <summary>
    /// Cross a runway, named in display form and sent as resolved: at a hold-short the held runway, else the assigned one
    /// (<see cref="HoldShortMenuHelper.HeldRunway(IMenuAircraft?)"/>); off a hold-short the runway to cross next
    /// (<see cref="IMenuAircraft.NextCrossingRunway"/>), never the assigned one. Null when there is no runway to name.
    /// </summary>
    private static MenuItem? BuildCrossRunway(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        RunwayToCross(aircraft) is { } runway ? BuildSend(CrossRunwayLabel + DisplaySuffix(runway), $"CROSS {runway}", context, host) : null;

    private static string? RunwayToCross(IMenuAircraft? aircraft) =>
        (aircraft is not null) && (!aircraft.CurrentPhase.StartsWith(HoldShortMenuHelper.HoldingShortPrefix, StringComparison.Ordinal))
            ? aircraft.NextCrossingRunway
            : HoldShortMenuHelper.HeldRunway(aircraft);

    /// <summary>
    /// The Change spawn delay submenu: one item per <see cref="SpawnDelay.Presets"/> entry, then a free-text box that
    /// sends the delay it parses. The box closes the menu it sits in, so this companion builder takes that menu rather
    /// than the entry's own builder shape.
    /// </summary>
    internal static MenuItem BuildSpawnDelay(ContextMenu menu, MenuContext context, IMenuHost host)
    {
        var delayMenu = new MenuItem { Header = SpawnDelayLabel };
        foreach ((string label, int seconds) in SpawnDelay.Presets)
        {
            delayMenu.Items.Add(BuildSend(label, $"SPAWNDELAY {seconds}", context, host));
        }

        delayMenu.Items.Add(new Separator());
        delayMenu.Items.Add(BuildCustomDelayInput(menu, context, host));
        return delayMenu;
    }

    /// <summary>
    /// The assume-selected live-traffic item: it assumes every shadow named in <paramref name="selectedShadows"/>, in the
    /// order given, and its label carries how many there are. The selection belongs to the click rather than to the menu
    /// context, so this companion builder takes that list instead of the entry's own builder shape.
    /// </summary>
    internal static MenuItem BuildAssumeSelected(IReadOnlyList<string> selectedShadows, IMenuHost host)
    {
        var item = new MenuItem { Header = $"{AssumeSelectedLabel} ({selectedShadows.Count})" };
        item.Click += async (_, _) => await host.AssumeSelectedLiveTrafficAsync(selectedShadows);
        return item;
    }

    /// <summary>
    /// The delay submenu's free-text box: Enter parses the text as a delay and, when it names one, closes
    /// <paramref name="parentMenu"/> and sends <c>SPAWNDELAY {seconds}</c>; text that names no delay sends nothing.
    /// </summary>
    private static TextBox BuildCustomDelayInput(ContextMenu parentMenu, MenuContext context, IMenuHost host)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "Custom (e.g. 90, 2m15s, 1h)",
            FontSize = 12,
            MinWidth = 180,
        };
        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            int? seconds = SpawnDelay.ParseDelayInput(textBox.Text);
            if (seconds is null)
            {
                return;
            }

            parentMenu.Close();
            await host.SendAsync(context.Callsign, $"SPAWNDELAY {seconds.Value}", context.Initials);
        };
        return textBox;
    }

    /// <summary>
    /// A relative item of the For section, offered while <paramref name="isApplicable"/> holds: sent as the previous
    /// selection with the right-clicked callsign after <paramref name="verb"/>, labelled by <paramref name="label"/> from
    /// the right-clicked callsign.
    /// </summary>
    private static MenuCatalogEntry Relative(
        string id,
        string entryLabel,
        string verb,
        Func<IMenuAircraft?, MenuContext, bool> isApplicable,
        Func<string, string> label
    ) => new(id, entryLabel, MenuFlightRules.Both, isApplicable, (_, context, host) => BuildRelative(verb, label, context, host));

    /// <summary>The relative item for the context's previous selection, or null when there is none.</summary>
    private static MenuItem? BuildRelative(string verb, Func<string, string> label, MenuContext context, IMenuHost host)
    {
        if (context.PreviousSelection is not { } selected)
        {
            return null;
        }

        string sender = selected.Callsign;
        var item = new MenuItem { Header = label(context.Callsign) };
        item.Click += async (_, _) => await host.SendAsync(sender, $"{verb} {context.Callsign}", context.Initials);
        return item;
    }

    /// <summary>
    /// The pushback-face entry's companion, which the ground group places right after Push back: one flat item per
    /// facing the host answers, sending its finished <c>PUSH FACE</c> command. Building the entry itself throws, and a
    /// host that answers no pushback faces gets no items.
    /// </summary>
    internal static IReadOnlyList<MenuItem> BuildPushbackFaces(MenuContext context, IMenuHost host) =>
        [.. host.GetPushbackFaceChoices(context.Callsign).Select(choice => BuildSend(choice.Label, FaceCommand(choice), context, host))];

    /// <summary>A pushback facing's <c>PUSH FACE</c> command, which every face choice the host answers carries.</summary>
    private static string FaceCommand(MenuCommandChoice choice) =>
        choice.Command
        ?? throw new InvalidOperationException($"The host answered the pushback face choice '{choice.Label}' without a PUSH FACE command.");

    /// <summary>The Push route item, which puts the host into drawing a tug move for the aircraft.</summary>
    private static MenuItem BuildPushRoute(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.EnterPushRoute(context.Callsign);
        return item;
    }

    /// <summary>
    /// An Exit left / Exit right entry: the designed exit flyout over the aircraft's exits ahead on its side
    /// (<see cref="ExitFlyout"/>), applicable on the rollout and while the aircraft has an exits-ahead list; on the rollout
    /// only with an exit listed on its side, on final whenever the forecast or the runway has an exit there
    /// (<see cref="AircraftCommandApplicability.CanExitRunway(IMenuAircraft?, ExitSide)"/>, <see cref="ExitFlyout.Build"/>).
    /// </summary>
    private static MenuCatalogEntry ExitEntry(string id, ExitSide side) =>
        new(
            id,
            ExitFlyout.Label(side),
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanExitRunway(ac, side),
            (ac, context, host) => ExitFlyout.Build(side, ac, context, host)
        );

    /// <summary>
    /// The Cleared for takeoff flyout, the same on the quick-command strip and in All Commands › Tower on every view:
    /// headed with the held runway, else the assigned one (<see cref="HoldShortMenuHelper.HeldRunway(IMenuAircraft?)"/>),
    /// then the IFR or VFR rows and the initial-altitude box (<see cref="TakeoffFlyout"/>), then the trailing separator and
    /// the Custom box row, always open (<see cref="TakeoffFlyout.CustomItem"/>).
    /// </summary>
    private static MenuItem BuildClearedForTakeoff(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = ClearedForTakeoffLabel + DisplaySuffix(HoldShortMenuHelper.HeldRunway(aircraft)) };
        foreach (Control item in TakeoffFlyout.Items(aircraft, context, host))
        {
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(TakeoffFlyout.CustomItem(context, host));
        return menu;
    }

    /// <summary>
    /// A pattern entry: the flyout over the aircraft's airport's runway ends (<see cref="BuildPatternRunwayFlyout"/>), an
    /// assigned runway among them; without runway ends there, a leaf naming the assigned runway that sends the verb for it as
    /// assigned; with neither, disabled with the reason beside its label and its strip button inert.
    /// </summary>
    private static MenuCatalogEntry PatternEntry(string id, Func<IMenuAircraft?, MenuContext, bool> isApplicable) =>
        new(id, PatternEntrySpec(id).Label, MenuFlightRules.Both, isApplicable, (ac, context, host) => BuildPatternEntry(id, ac, context, host));

    private static MenuItem BuildPatternEntry(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        (string label, string command) = PatternEntrySpec(id);
        if (BuildPatternRunwayFlyout(id, label, aircraft, context, host) is { } flyout)
        {
            return flyout;
        }

        if (aircraft is { AssignedRunway.Length: > 0 })
        {
            string runway = aircraft.AssignedRunway;
            return BuildSend($"{label} {RunwayIdentifier.ToDisplayDesignator(runway)}", $"{command} {runway}", context, host);
        }

        return Unavailable(label, (PatternRunwayChoices.AirportOf(aircraft) is { } airport) ? NoRunwayDataReason(airport) : NoAirportReason);
    }

    /// <summary>
    /// A submenu labelled <paramref name="label"/> over every runway end at the aircraft's airport
    /// (<see cref="PatternRunwayChoices.For"/>): the room's arrival or both-way active ends, a separator, then the rest,
    /// the ends too short for the physical type (<see cref="IMenuAircraft.BaseAircraftType"/>) last in each group, dimmed
    /// with a "short" note and still clickable. Each row draws the entry's glyph turned to the end
    /// (<see cref="PatternRunwayChoices.Glyph"/>) and sends entry <paramref name="id"/>'s command for it. The aircraft's
    /// assigned runway row, when it has one here, takes the focus as the submenu or the strip's flyout opens, so Enter sends
    /// that row's command. Null when the aircraft has no airport or the navigation data no runways there.
    /// </summary>
    private static MenuItem? BuildPatternRunwayFlyout(string id, string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if ((aircraft is null) || (PatternRunwayChoices.AirportOf(aircraft) is not { } airport))
        {
            return null;
        }

        IReadOnlyList<PatternRunwayChoice> choices = host.GetPatternRunwayChoices(airport, aircraft.BaseAircraftType);
        if (choices.Count == 0)
        {
            return null;
        }

        string command = PatternEntrySpec(id).Command;
        var flyout = new MenuItem { Header = label };
        MenuItem? assigned = null;
        for (int i = 0; i < choices.Count; i++)
        {
            if ((i > 0) && choices[i - 1].IsActive && !choices[i].IsActive)
            {
                flyout.Items.Add(new Separator());
            }

            MenuItem row = BuildPatternRunwayRow(id, command, choices[i], context, host);
            flyout.Items.Add(row);
            if (IsAssignedRunway(aircraft, choices[i]))
            {
                assigned = row;
            }
        }

        if (assigned is not null)
        {
            QuickCommandStrip.FollowInFlyout(flyout, new AssignedRunwayFocus(assigned));
            flyout.SubmenuOpened += (_, _) =>
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (flyout.IsSubMenuOpen)
                        {
                            assigned.Focus(NavigationMethod.Directional);
                        }
                    },
                    DispatcherPriority.Loaded
                );
        }

        return flyout;
    }

    /// <summary>
    /// Focuses the assigned runway's row when the quick-command strip opens the entry's flyout, where the submenu
    /// (<see cref="MenuItem.IsSubMenuOpen"/>) never opens, unless the flyout closed before the focus ran.
    /// </summary>
    private sealed class AssignedRunwayFocus(MenuItem row) : IStripFlyoutContent
    {
        private bool _open;

        public void Opened(ItemCollection items, Action close)
        {
            _open = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_open)
                    {
                        row.Focus(NavigationMethod.Directional);
                    }
                },
                DispatcherPriority.Loaded
            );
        }

        public void PointerInside(bool inside) { }

        public void Closed() => _open = false;
    }

    /// <summary>Whether <paramref name="choice"/> is the runway <paramref name="aircraft"/> is assigned.</summary>
    private static bool IsAssignedRunway(IMenuAircraft? aircraft, PatternRunwayChoice choice) =>
        (aircraft?.AssignedRunway is { Length: > 0 } assigned)
        && string.Equals(RunwayIdentifier.ToDisplayDesignator(assigned), choice.Designator, StringComparison.OrdinalIgnoreCase);

    private static MenuItem BuildPatternRunwayRow(string id, string command, PatternRunwayChoice choice, MenuContext context, IMenuHost host)
    {
        string send = $"{command} {choice.Designator}";
        var row = new MenuGlyphRow
        {
            Glyph = PatternRunwayChoices.Glyph(id, choice.End),
            Label = $"Runway {choice.Designator}",
            Note = choice.IsLandable ? null : "short",
            IsDimmed = !choice.IsLandable,
            Command = send,
        };
        return BuildTemplatedSend(row, MenuGlyphRowTemplate.Instance, send, context, host);
    }

    /// <summary>
    /// A Report when… entry: the Report when… flyout (<see cref="ReportWhenFlyout"/>), its menu row reading
    /// <paramref name="label"/>, opening with the focus in the box <paramref name="focus"/> names.
    /// </summary>
    private static MenuCatalogEntry ReportWhenEntry(string id, string label, ReportWhenFocus focus) =>
        new(id, label, MenuFlightRules.Both, Always, (ac, context, host) => ReportWhenFlyout.Build(label, ac, focus, context, host));

    /// <summary>
    /// An entry whose item the host builds for a surface of its own rather than from a command text: the Warp flyout,
    /// the Squawk and Say flyouts with their box rows, the flight-plan editor, the display toggles and measure item that read
    /// and drive the surface's own state, and the ground submenus whose choices the host answers (hold short, follow, give
    /// way, push back to, preset taxi, taxi to runway).
    /// <paramref name="build"/> receives the entry's own label, so the item's text lives in one place, though a
    /// state-dependent item overrides it. A builder returns null for an item the surface's state hides.
    /// </summary>
    private static MenuCatalogEntry HostLeaf(
        string id,
        string label,
        Func<IMenuAircraft?, MenuContext, bool> isApplicable,
        Func<string, IMenuAircraft?, MenuContext, IMenuHost, MenuItem?> build
    ) => new(id, label, MenuFlightRules.Both, isApplicable, (aircraft, context, host) => build(label, aircraft, context, host));

    /// <summary>
    /// A picker or label-computing entry, offered always: <paramref name="build"/> receives the entry's own label, so
    /// the item's text lives in one place, and may override it from the aircraft (the final-approach speed) or return
    /// null when it has nothing to offer.
    /// </summary>
    private static MenuCatalogEntry Picker(string id, string label, Func<string, IMenuAircraft?, MenuContext, IMenuHost, MenuItem?> build) =>
        new(id, label, MenuFlightRules.Both, Always, (aircraft, context, host) => build(label, aircraft, context, host));

    /// <summary>The reason the Squawk box gives for text that is not four octal digits.</summary>
    internal const string InvalidSquawkCode = "Not a valid squawk code (0000-7777)";

    /// <summary>
    /// The Squawk flyout's code box row, always open, <c>SQ</c> drawn in its box: four octal digits are sent as typed
    /// (<c>SQ 0123</c>); other text is refused inline; a blank box sends nothing (a bare <c>SQ</c> is Squawk assigned code,
    /// which keeps its own row).
    /// </summary>
    internal static InlineBoxRow SquawkCodeRow(MenuContext context, IMenuHost host) =>
        InlineBoxRow.Create(
            new InlineBoxSpec
            {
                Label = "Code",
                Glyph = QuickCommandGlyphs.For(MenuIds.SquawkCode),
                BlankGlyphSlot = false,
                Verb = "SQ",
                Placeholder = "0000-7777",
                BoxName = "SquawkCodeBox",
                BoxWidth = 120,
                Seed = "",
                FixSuggestions = null,
                Read = ReadSquawkCode,
            },
            command => host.SendAsync(context.Callsign, command, context.Initials)
        );

    /// <summary>
    /// The Say flyout's Custom box row, always open, <c>SAY</c> drawn in its box: the text is sent trimmed, case kept; a blank
    /// box sends nothing (a bare <c>SAY</c> parses as an unsupported command).
    /// </summary>
    internal static InlineBoxRow SayCustomRow(MenuContext context, IMenuHost host) =>
        InlineBoxRow.Create(
            new InlineBoxSpec
            {
                Label = "Custom",
                Glyph = null,
                BlankGlyphSlot = false,
                Verb = "SAY",
                Placeholder = "words the pilot says",
                BoxName = "SayCustomBox",
                BoxWidth = 260,
                Seed = "",
                FixSuggestions = null,
                Read = ReadSayText,
            },
            command => host.SendAsync(context.Callsign, command, context.Initials)
        );

    /// <summary>
    /// The Scratchpad flyout's box row, always open, <c>SP</c> drawn in its box, opening on the aircraft's present Scratchpad 1
    /// (never the destination the scope shows in its place), selected: new text is sent trimmed and upper-cased (<c>SP I30</c>);
    /// text longer than the room's limit (<see cref="IMenuHost.ScratchpadMaxLength"/>) is refused inline; a blank box, or the
    /// present value again, sends nothing (a bare <c>SP</c> clears, and <c>SP</c> with the present value toggles back to the
    /// previous scratchpad).
    /// </summary>
    internal static InlineBoxRow ScratchpadRow(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string present = aircraft?.Scratchpad1 ?? "";
        int maxLength = host.ScratchpadMaxLength;
        return InlineBoxRow.Create(
            new InlineBoxSpec
            {
                Label = "Scratchpad",
                Glyph = QuickCommandGlyphs.For(MenuIds.DataBlockScratchpad),
                BlankGlyphSlot = false,
                Verb = "SP",
                Placeholder = $"up to {maxLength} characters",
                BoxName = "ScratchpadBox",
                BoxWidth = 160,
                Seed = present,
                FixSuggestions = null,
                Read = text => ReadScratchpad(text, present, maxLength),
            },
            command => host.SendAsync(context.Callsign, command, context.Initials)
        );
    }

    /// <summary>The reason the Scratchpad box gives for text longer than <paramref name="maxLength"/>.</summary>
    internal static string ScratchpadTooLong(int maxLength) => $"Scratchpad holds at most {maxLength} characters";

    /// <summary>
    /// The Scratchpad box's reading: the trimmed text, upper-cased after <c>SP</c>; blank, or the <paramref name="present"/>
    /// scratchpad in any case, waits; longer than <paramref name="maxLength"/> is refused.
    /// </summary>
    private static InlineBoxReading ReadScratchpad(string text, string present, int maxLength)
    {
        string typed = text.Trim();
        if ((typed.Length == 0) || string.Equals(typed, present.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return InlineBoxReading.Blank;
        }

        return (typed.Length > maxLength)
            ? InlineBoxReading.Refuses(ScratchpadTooLong(maxLength))
            : InlineBoxReading.Sends($"SP {typed.ToUpperInvariant()}");
    }

    /// <summary>The Squawk box's reading: four octal digits, trimmed, sent as typed; blank waits; anything else is refused.</summary>
    private static InlineBoxReading ReadSquawkCode(string text)
    {
        string code = text.Trim();
        if (code.Length == 0)
        {
            return InlineBoxReading.Blank;
        }

        return ((code.Length == 4) && code.All(digit => digit is >= '0' and <= '7'))
            ? InlineBoxReading.Sends($"SQ {code}")
            : InlineBoxReading.Refuses(InvalidSquawkCode);
    }

    /// <summary>The Say Custom box's reading: the trimmed text after <c>SAY</c>; blank waits.</summary>
    private static InlineBoxReading ReadSayText(string text)
    {
        string said = text.Trim();
        return (said.Length == 0) ? InlineBoxReading.Blank : InlineBoxReading.Sends($"SAY {said}");
    }

    /// <summary>The <c>TEMPALT</c> command for the altitude the controller typed, or null when it is not one.</summary>
    internal static string? FormatTemporaryAltitude(string input) => AltitudeCommand("TEMPALT", input);

    /// <summary>The <c>CRUISE</c> command for the altitude the controller typed, or null when it is not one.</summary>
    internal static string? FormatCruiseAltitude(string input) => AltitudeCommand("CRUISE", input);

    /// <summary>The highest the sim's altitude commands take: one hundred above it would wrap the argument into feet.</summary>
    private const int MaxAltitudeHundreds = 999;

    /// <summary>
    /// The <paramref name="verb"/> command for a typed altitude (<see cref="ParseAltitudeFeet"/>), its argument in the hundreds
    /// the sim's altitude commands take (<c>AltitudeHundreds</c>); null for text that is not an altitude.
    /// </summary>
    private static string? AltitudeCommand(string verb, string input) => (ParseAltitudeFeet(input) is { } feet) ? $"{verb} {feet / 100}" : null;

    /// <summary>
    /// The feet of a typed altitude: <see cref="AltitudeResolver.Resolve"/> reads the shorthand hundreds ("230", "050"), the
    /// full feet ("23000", "23,000"), a flight level ("FL230") and the AGL notation ("KOAK+010") alike. Null for text that is
    /// not an altitude, one over 99,900 ft (whose hundreds the sim would read back as feet), a flight level of more than three
    /// digits, a comma that does not group a whole thousand, or an AGL altitude while the navigation data is not loaded.
    /// </summary>
    internal static int? ParseAltitudeFeet(string input)
    {
        string trimmed = input.Trim();
        if (StripFlightLevel(trimmed) is not { } text)
        {
            return null;
        }

        if (!IsThousandsGrouped(text))
        {
            return null;
        }

        int? feet = ResolveAltitude(text.Replace(",", "", StringComparison.Ordinal));
        return ((feet is null) || (feet.Value / 100 > MaxAltitudeHundreds)) ? null : feet;
    }

    /// <summary>
    /// The text without a flight-level <c>FL</c> prefix, or null for an <c>FL</c> that is not one: the prefix comes off
    /// only when one to three digits follow (FL180 to FL600), a longer number being no flight level, and an airport code
    /// that merely starts with those letters (<c>FLL+010</c>) keeps its prefix for the AGL path.
    /// </summary>
    private static string? StripFlightLevel(string trimmed)
    {
        if (!trimmed.StartsWith("FL", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        string rest = trimmed[2..].Trim();
        if (!rest.All(char.IsAsciiDigit))
        {
            return trimmed;
        }

        return (rest.Length is >= 1 and <= 3) ? rest : null;
    }

    /// <summary>
    /// The altitude's feet, or null when the text names an AGL altitude and the navigation data is not loaded:
    /// <see cref="AltitudeResolver.Resolve"/> reads the airport's elevation from it and throws without it.
    /// </summary>
    private static int? ResolveAltitude(string text) =>
        (text.Contains('+') && (NavigationDatabase.InstanceOrNull is null)) ? null : AltitudeResolver.Resolve(text);

    /// <summary>
    /// Whether every comma in <paramref name="text"/> groups digits three at a time (<c>5,000</c>, <c>23,000</c>); false
    /// for one that does not (<c>2,30</c>), which is not an altitude. Text with no comma is grouped.
    /// </summary>
    private static bool IsThousandsGrouped(string text)
    {
        string[] groups = text.Split(',');
        if (groups.Length == 1)
        {
            return true;
        }

        return (groups[0].Length is >= 1 and <= 3)
            && groups[0].All(char.IsAsciiDigit)
            && groups.Skip(1).All(group => (group.Length == 3) && group.All(char.IsAsciiDigit));
    }

    /// <summary>The <c>SPD</c> command for a positive whole number of knots, or null for anything else.</summary>
    internal static string? FormatAssignedSpeed(string input) =>
        (input.All(char.IsAsciiDigit) && int.TryParse(input, out int knots) && (knots > 0)) ? $"SPD {knots}" : null;

    /// <summary>
    /// Warp is offered only for a controllable airborne aircraft: the warp goes through the command path, which auto-assumes
    /// an airborne shadow but refuses an unassumable surface one, and WARP always leaves the aircraft airborne, so the ground
    /// warp is Warp here at a taxi node (<c>WARPG</c>).
    /// </summary>
    private static bool CanWarp([NotNullWhen(true)] IMenuAircraft? aircraft) => AircraftCommandApplicability.IsAirborneControllable(aircraft);

    /// <summary>
    /// The Warp flyout with an empty FRD box (<see cref="WarpFlyout.Build"/>), or null where Warp is not offered
    /// (<see cref="CanWarp"/>).
    /// </summary>
    private static MenuItem? BuildWarp(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        CanWarp(aircraft) ? WarpFlyout.Build(label, aircraft, WarpFrdSeed.Empty, context, host) : null;

    // --- Point menu ---------------------------------------------------------------------------

    /// <summary>
    /// A point-menu entry: <paramref name="build"/> receives the aircraft and the right-clicked point, and the entry builds
    /// nothing without either.
    /// </summary>
    private static MenuCatalogEntry PointEntry(
        string id,
        string label,
        Func<IMenuAircraft?, MenuContext, bool> isApplicable,
        Func<IMenuAircraft, MenuPoint, MenuContext, IMenuHost, MenuItem?> build
    ) =>
        new(
            id,
            label,
            MenuFlightRules.Both,
            isApplicable,
            (aircraft, context, host) => ((aircraft is not null) && (context.Click.Point is { } point)) ? build(aircraft, point, context, host) : null
        );

    /// <summary>A point click on a controllable airborne aircraft, which the heading, direct-to and hold items command.</summary>
    private static bool AtPointAirborne(IMenuAircraft? aircraft, MenuContext context) =>
        (context.Click.Point is not null) && AircraftCommandApplicability.IsAirborneControllable(aircraft);

    /// <summary>A taxi-node click on an aircraft that can be given a taxi route, which Taxi here and Custom taxi… route.</summary>
    private static bool AtNodeTaxiable(IMenuAircraft? aircraft, MenuContext context) =>
        (context.Click.Point?.Node is not null) && AircraftCommandApplicability.CanDrawTaxiRoute(aircraft);

    /// <summary>A runway-surface click on an aircraft that can be given a taxi route, which Taxi to runway routes.</summary>
    private static bool AtRunwaySurfaceTaxiable(IMenuAircraft? aircraft, MenuContext context) =>
        (context.Click.Point is { SurfaceRunways.Count: > 0 }) && AircraftCommandApplicability.CanDrawTaxiRoute(aircraft);

    /// <summary>A click on a named stand or spot with an aircraft that can push back, which Push to sends there.</summary>
    private static bool AtNodePushable(IMenuAircraft? aircraft, MenuContext context) =>
        (context.Click.Point?.Node is { Type: "Parking" or "Spot", Name: not null }) && AircraftCommandApplicability.CanPushBack(aircraft);

    /// <summary>Fly heading to the point (<see cref="PointFlyHeading"/>).</summary>
    private static MenuItem BuildPointFlyHeading(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        int heading = PointFlyHeading(aircraft, point);
        return BuildSend($"Fly heading {new MagneticHeading(heading).ToDisplayString()}", $"FH {heading}", context, host);
    }

    /// <summary>
    /// The heading the point menu's Fly heading sends: the true bearing from the aircraft to the point, made magnetic with
    /// the variation at the aircraft's position and rounded to five degrees, a zero or negative one flown as 360.
    /// </summary>
    internal static int PointFlyHeading(IMenuAircraft aircraft, MenuPoint point)
    {
        double trueBearing = GeoMath.BearingTo(aircraft.Position, point.Position);
        double magnetic = new TrueHeading(trueBearing).ToMagnetic(MagneticDeclination.GetDeclination(aircraft.Position)).Degrees;
        int heading = (int)(Math.Round(magnetic / HeadingStep) * HeadingStep);
        return (heading <= 0) ? 360 : heading;
    }

    /// <summary>An item naming the point by the host's fix-radial-distance and sending <paramref name="verb"/> with it; null without one.</summary>
    private static MenuItem? BuildPointFrd(MenuPoint point, string label, string verb, MenuContext context, IMenuHost host) =>
        host.DescribePoint(point.Position) is { } frd ? BuildSend($"{label} {frd}", $"{verb} {frd}", context, host) : null;

    /// <summary>Hold at the point's fix-radial-distance with <paramref name="side"/> turns; null without one.</summary>
    private static MenuItem? BuildPointHold(MenuPoint point, string side, string verb, MenuContext context, IMenuHost host) =>
        host.DescribePoint(point.Position) is { } frd ? BuildSend($"Hold at {frd} ({side})", $"{verb} {frd}", context, host) : null;

    /// <summary>The host's Taxi here choices for the clicked node as one item (<see cref="PointTaxiFlyout.BuildTaxiHere"/>).</summary>
    private static MenuItem? BuildPointTaxiHere(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host) =>
        (point.Node is { } node) ? PointTaxiFlyout.BuildTaxiHere(host.GetTaxiChoices(context.Callsign, node, point.RunwayEnd), context, host) : null;

    /// <summary>
    /// Push to the clicked named spot (<c>PUSH $SPOT</c>) or stand (<c>PUSH @STAND</c>); null when the tug cannot reach it
    /// (<see cref="IMenuHost.CanTugReach"/>).
    /// </summary>
    private static MenuItem? BuildPointPushTo(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host) =>
        (point.Node is { Type: "Parking" or "Spot", Name: { } name } node) && host.CanTugReach(context.Callsign, node)
            ? BuildSend($"Push to {name}", $"PUSH {((node.Type == "Spot") ? '$' : '@')}{name}", context, host)
            : null;

    /// <summary>Custom taxi…: the host's input popup, seeded for the clicked node, sending the trimmed text it submits.</summary>
    private static MenuItem? BuildPointCustomTaxi(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        if (point.Node is not { } node)
        {
            return null;
        }

        MenuTextSeed seed = host.GetCustomTaxiSeed(node, point.RunwayEnd);
        var item = new MenuItem { Header = "Custom taxi…", Tag = new MenuPickerDescriptor(MenuPickerDescriptor.Input, []) };
        item.Click += (_, _) =>
            host.ShowInputPopup("Taxi command", seed.Text, seed.Caret, text => host.SendAsync(context.Callsign, text.Trim(), context.Initials));
        return item;
    }

    /// <summary>
    /// Warp here is offered at a taxi node (the ground warp, <c>WARPG</c>) for any controllable aircraft, and at a map point
    /// only where Warp… is (<see cref="CanWarp"/>): never for an aircraft on the ground.
    /// </summary>
    private static bool CanWarpHere(IMenuAircraft? aircraft, MenuContext context) =>
        (context.Click.Point is { } point)
        && (((point.Node ?? point.WarpNode) is not null) ? AircraftCommandApplicability.IsControllable(aircraft) : CanWarp(aircraft));

    /// <summary>
    /// Warp here: to a clicked taxi node, <c>WARPG #node</c>; to a map point, the Warp flyout with the point's
    /// fix-radial-distance in its FRD box (<see cref="WarpFlyout.Build"/>); null at a map point the host cannot name, or for an
    /// aircraft Warp… is not offered for. Each records the alias of the one verb it sends.
    /// </summary>
    private static MenuItem? BuildPointWarp(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        if ((point.Node ?? point.WarpNode) is { } node)
        {
            return WithOwnAlias(BuildSend("Warp here", $"WARPG #{node.Id}", context, host), "WARPG", context);
        }

        if (!CanWarp(aircraft) || (host.DescribePoint(point.Position) is not { } frd))
        {
            return null;
        }

        return WithOwnAlias(WarpFlyout.Build($"Warp here ({frd})", aircraft, WarpFrdSeed.ClickedPoint(frd), context, host), "WARP", context);
    }

    /// <summary>
    /// Records on <paramref name="item"/> the alias of <paramref name="verb"/>, the one verb this build of an entry that can send
    /// several sends, so <see cref="MenuAlias.Apply"/> keeps it rather than listing them all.
    /// </summary>
    private static MenuItem WithOwnAlias(MenuItem item, string verb, MenuContext context)
    {
        MenuAlias.SetText(item, MenuAliases.Display([verb], context.Scheme));
        return item;
    }

    /// <summary>
    /// The header's Command… item, which asks the host to open its free-text command popup; the host sends what is typed.
    /// It takes the aircraft it has no use for so that it matches the <see cref="HostLeaf"/> builder shape.
    /// </summary>
    private static MenuItem BuildCommand(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.ShowCommandFlyout(context.Callsign, context.Initials);
        return item;
    }

    /// <summary>
    /// The header's Note… item, which asks the host to open its note popup prefilled with the aircraft's current note
    /// (empty with no aircraft model) and sends the <c>NOTE</c> command the popup builds.
    /// </summary>
    private static MenuItem BuildNote(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
            host.ShowNoteFlyout(context.Callsign, aircraft?.Note ?? "", command => host.SendAsync(context.Callsign, command, context.Initials));
        return item;
    }

    /// <summary>
    /// The Edit flight plan item, which asks the host to open its flight-plan editor for the menu's aircraft. It takes
    /// the aircraft it has no use for so that it matches the <see cref="HostLeaf"/> builder shape.
    /// </summary>
    private static MenuItem BuildEditFlightPlan(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.OpenFlightPlanEditor(context.Callsign);
        return item;
    }

    /// <summary>
    /// An altitude as the Maintain altitude flyout and the Altitude header show it: feet with thousands commas below 18,000 ft
    /// (<c>17,500</c>), a flight level from it (<c>FL180</c>).
    /// </summary>
    internal static string FormatAltitude(int altitude) =>
        (altitude >= 18000) ? $"FL{altitude / 100}" : altitude.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Whether the aircraft is navigating to a fix, which an appended direct-to follows.</summary>
    private static Func<IMenuAircraft?, MenuContext, bool> IsNavigatingToFix => (ac, _) => !string.IsNullOrEmpty(ac?.NavigatingTo);

    /// <summary>
    /// A data-block altitude entry: the one-box altitude flyout (<see cref="AltitudeFlyout.BuildBox"/>) of <paramref name="kind"/>.
    /// </summary>
    private static MenuCatalogEntry AltitudeBoxEntry(string id, string label, AltitudeBoxKind kind) =>
        new(id, label, MenuFlightRules.Both, Always, (ac, context, host) => AltitudeFlyout.BuildBox(label, kind, ac, context, host));

    /// <summary>A heading entry: the heading flyout (<see cref="HeadingFlyout"/>) opening as <paramref name="preset"/> says.</summary>
    private static MenuCatalogEntry HeadingEntry(string id, HeadingFlyoutPreset preset) =>
        new(id, preset.Label, MenuFlightRules.Both, Always, (ac, context, host) => HeadingFlyout.Build(preset, ac, context, host));

    /// <summary>
    /// A direct-to entry: the Direct to flyout (<see cref="DirectToFlyout"/>) opening on <paramref name="verb"/>, offered while
    /// <paramref name="isApplicable"/> holds.
    /// </summary>
    private static MenuCatalogEntry DirectToEntry(string id, string label, DirectToVerb verb, Func<IMenuAircraft?, MenuContext, bool> isApplicable) =>
        new(id, label, MenuFlightRules.Both, isApplicable, (ac, context, host) => DirectToFlyout.Build(label, verb, ac, context, host));

    /// <summary>
    /// A flyout entry offered where <paramref name="isApplicable"/> holds: the Cross fix or Depart fix flyout
    /// (<see cref="FixValueFlyout"/>), the PTAC flyout, the Join airway flyout (<see cref="JoinAirwayFlyout"/>), a coordination
    /// flyout (<see cref="CoordinationFlyout"/>) or the field advisory flyout (<see cref="FieldAdvisoryFlyout"/>) that
    /// <paramref name="build"/> makes, its menu row reading <paramref name="label"/>, ellipsis included.
    /// </summary>
    private static MenuCatalogEntry FlyoutEntry(
        string id,
        string label,
        Func<IMenuAircraft?, MenuContext, bool> isApplicable,
        Func<string, IMenuAircraft?, MenuContext, IMenuHost, MenuItem> build
    ) => new(id, label, MenuFlightRules.Both, isApplicable, (ac, context, host) => build(label, ac, context, host));

    /// <summary>
    /// A join-radial entry (<c>JRADO</c> / <c>JRADI</c>): the join radial flyout (<see cref="JoinRadialFlyout"/>) opening on
    /// <paramref name="side"/>, its menu row reading <paramref name="label"/>.
    /// </summary>
    private static MenuCatalogEntry JoinRadialEntry(string id, string label, RadialSide side) =>
        new(id, label, MenuFlightRules.Both, Always, (ac, context, host) => JoinRadialFlyout.Build(label, side, ac, context, host));

    /// <summary>
    /// A 360s-at-fix entry (<c>HFIXL</c> / <c>HFIXR</c>): the 360s flyout (<see cref="HoldFlyout.BuildTurns"/>) opening on
    /// <paramref name="preset"/>, a VFR maneuver in the air (<see cref="CanFlyVfrManeuverAloft"/>).
    /// </summary>
    private static MenuCatalogEntry HoldTurnsEntry(string id, string label, HoldTurns preset) =>
        new(
            id,
            label,
            MenuFlightRules.VfrOnly,
            CanFlyVfrManeuverAloft,
            (ac, context, host) => HoldFlyout.BuildTurns(label, preset, ac, context, host)
        );

    /// <summary>The altitude at and above which the picker lists Mach rows (7110.65 5-7-1.g).</summary>
    private const double MachRowFloorFeet = 24000;

    /// <summary>The owner's clamp on the picker's Mach rows: 0.60 at the lowest and 0.92 at the highest.</summary>
    private const double MachRowMinimum = 0.60;

    private const double MachRowMaximum = 0.92;

    /// <summary>The knots chips and the Mach chips (in hundredths of a Mach) the Assign speed flyout offers.</summary>
    /// <param name="Knots">Every tenth knot offered, lowest first.</param>
    /// <param name="MachHundredths">Every Mach offered, in hundredths (78 for M.78), lowest first; empty below FL240.</param>
    internal sealed record SpeedChoices(IReadOnlyList<int> Knots, IReadOnlyList<int> MachHundredths);

    /// <summary>
    /// The speeds the Assign speed flyout offers. A filed type gets every tenth knot from its approach speed rounded up through
    /// its knots ceiling — the highest of its cruise, climb and descent speeds at the aircraft's altitude and the profile's
    /// FL150/FL240 climb and FL100 descent knots, plus 20 kt (10 for a piston), floored to ten, capped at 250 below 10,000 ft
    /// unless the type waives the limit — then, at or above FL240, the Mach values its profile spans. Without a filed type the
    /// knots stay 150-350.
    /// </summary>
    internal static SpeedChoices SpeedValues(IMenuAircraft? aircraft)
    {
        if (aircraft is null || string.IsNullOrEmpty(aircraft.FiledAircraftType))
        {
            return new SpeedChoices(SpeedRange(150, 350), []);
        }

        string type = aircraft.FiledAircraftType;
        AircraftCategory category = AircraftCategorization.Categorize(type);
        double altitude = Math.Max(aircraft.AltitudeFeet, 0);
        int ceiling = KnotsCeiling(type, category, altitude);
        IReadOnlyList<double> machs = MachValues(type, category, altitude, ceiling);
        if (machs.Count > 0)
        {
            ceiling = Math.Min(ceiling, FloorToTen(WindInterpolator.MachToIas(machs[^1], altitude)));
        }

        int floor = (int)(Math.Ceiling(AircraftPerformance.ApproachSpeed(type, category) / 10.0) * 10);
        return new SpeedChoices(SpeedRange(floor, ceiling), [.. machs.Select(mach => (int)Math.Round(mach * 100, MidpointRounding.AwayFromZero))]);
    }

    /// <summary>The filed type's final approach speed rounded to the knot; null without a filed type or a positive speed.</summary>
    internal static int? FinalApproachKnots(IMenuAircraft? aircraft)
    {
        if (aircraft is null || string.IsNullOrEmpty(aircraft.FiledAircraftType))
        {
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.FiledAircraftType);
        int knots = (int)Math.Round(AircraftPerformance.ApproachSpeed(aircraft.FiledAircraftType, category), MidpointRounding.AwayFromZero);
        return (knots > 0) ? knots : null;
    }

    /// <summary>
    /// The picker's knots ceiling (kt): the highest of the type's cruise, climb and descent speeds at the altitude and the
    /// resolved profile's own FL150/FL240 climb and FL100 descent knots, plus the category's margin, floored to ten.
    /// Below 10,000 ft the 250 kt limit of 14 CFR 91.117 caps it unless the type waives the limit.
    /// </summary>
    private static int KnotsCeiling(string type, AircraftCategory category, double altitude)
    {
        double highest = Math.Max(
            AircraftPerformance.DefaultSpeed(type, category, altitude, null),
            Math.Max(AircraftPerformance.ClimbSpeed(type, category, altitude), AircraftPerformance.DescentSpeed(type, category, altitude))
        );

        if (AircraftProfileDatabase.Get(type) is { } profile)
        {
            highest = Math.Max(highest, Math.Max(profile.ClimbSpeedFl150, Math.Max(profile.ClimbSpeedFl240, profile.DescentSpeedFl100)));
        }

        int ceiling = FloorToTen(highest + (category == AircraftCategory.Piston ? 10 : 20));
        return ((altitude < 10000) && !AircraftPerformance.IsSpeedLimitWaived(type)) ? Math.Min(ceiling, 250) : ceiling;
    }

    /// <summary>
    /// The picker's Mach rows, each a hundredth of a Mach: only at or above FL240, and only for a type the resolved
    /// profile gives a Mach value (turboprops and pistons list knots only) — a jet whose type has none runs the jet
    /// category baseline instead. Spanned are the profile's final climb and initial descent speeds, its cruise speed when
    /// that is itself a Mach number, and the cruise Mach its TAS cruise speed gives at the cruise altitude; the rows run
    /// from the lowest less 0.04 through the highest plus 0.01, on the hundredth, clamped to 0.60-0.92, and a row whose
    /// IAS at the altitude exceeds the knots ceiling is dropped.
    /// </summary>
    private static IReadOnlyList<double> MachValues(string type, AircraftCategory category, double altitude, int knotsCeiling)
    {
        if (altitude < MachRowFloorFeet)
        {
            return [];
        }

        AircraftProfile? profile = AircraftProfileDatabase.Get(type);
        if (profile is null || !HasMachValue(profile))
        {
            if (category != AircraftCategory.Jet)
            {
                return [];
            }

            profile = CategoryPerformance.BaselineProfile(category);
        }

        var spanned = new List<double>();
        AddMachValue(spanned, profile.ClimbSpeedFinal);
        AddMachValue(spanned, profile.DescentSpeedInitial);
        AddMachValue(spanned, profile.CruiseSpeed);
        if (profile.CruiseSpeed >= 1.0)
        {
            spanned.Add(profile.CruiseSpeed / WindInterpolator.SpeedOfSoundKts(profile.CruiseAltitude));
        }

        double low = Math.Max(Math.Round((spanned.Min() - 0.04) * 100, MidpointRounding.AwayFromZero) / 100, MachRowMinimum);
        double high = Math.Min(Math.Round((spanned.Max() + 0.01) * 100, MidpointRounding.AwayFromZero) / 100, MachRowMaximum);
        var rows = new List<double>();
        for (
            int hundredths = (int)Math.Round(low * 100, MidpointRounding.AwayFromZero);
            hundredths <= (int)Math.Round(high * 100, MidpointRounding.AwayFromZero);
            hundredths++
        )
        {
            double mach = hundredths / 100.0;
            if (WindInterpolator.MachToIas(mach, altitude) <= knotsCeiling)
            {
                rows.Add(mach);
            }
        }

        return rows;
    }

    /// <summary>Whether the profile carries any Mach value, which is what earns a type Mach rows.</summary>
    private static bool HasMachValue(AircraftProfile profile) =>
        IsMachValue(profile.ClimbSpeedFinal) || IsMachValue(profile.DescentSpeedInitial) || IsMachValue(profile.CruiseSpeed);

    private static bool IsMachValue(double value) => (value > 0) && (value < 1.0);

    /// <summary>Adds <paramref name="value"/> to the spanned Mach values when it is itself a Mach number.</summary>
    private static void AddMachValue(List<double> spanned, double value)
    {
        if (IsMachValue(value))
        {
            spanned.Add(value);
        }
    }

    /// <summary>The value floored to the previous ten, which the picker's knots floor and ceiling round to.</summary>
    private static int FloorToTen(double value) => (int)(Math.Floor(value / 10.0) * 10);

    private static List<int> SpeedRange(int min, int max)
    {
        var items = new List<int>();
        for (int speed = min; speed <= max; speed += 10)
        {
            items.Add(speed);
        }

        return items;
    }

    /// <summary>The final-approach-speed leaf, which sends <c>RFAS</c> under the label <see cref="FinalApproachSpeedLabel"/> gives it.</summary>
    private static MenuItem BuildFinalApproachSpeed(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildSend(FinalApproachSpeedLabel(label, aircraft), "RFAS", context, host);

    /// <summary>
    /// The final-approach-speed label: "Reduce to final approach speed - 140 kt" with the filed type's approach speed, else
    /// the bare label. The radar speed flyout's item reads the same, so the two cannot drift.
    /// </summary>
    /// <param name="label">The entry's catalog label (<see cref="MenuIds.SpeedFinalApproach"/>).</param>
    /// <param name="aircraft">The aircraft whose filed type gives the speed, or null for none.</param>
    public static string FinalApproachSpeedLabel(string label, IMenuAircraft? aircraft) =>
        (FinalApproachKnots(aircraft) is { } knots) ? $"{label} - {knots.ToString(CultureInfo.InvariantCulture)} kt" : label;

    /// <summary>The Draw taxi route item, which puts the host into drawing a route for the aircraft.</summary>
    private static MenuItem BuildDrawRoute(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.EnterDrawRoute(context.Callsign);
        return item;
    }

    /// <summary>An approach entry: its verb's approach flyout (<see cref="ApproachSpec"/>, <see cref="ApproachFlyout.Build"/>).</summary>
    private static MenuCatalogEntry ApproachPicker(string id)
    {
        ApproachVerbSpec spec = ApproachSpec(id);
        return new(id, spec.Label, MenuFlightRules.Both, Always, (ac, context, host) => ApproachFlyout.Build(spec.Label, spec, ac, context, host));
    }

    /// <summary>
    /// The verb of each approach entry's flyout: its label, its command, whether its rows are forced, its visual rows, Force
    /// toggle and Cancel IFR, its glyph and its default row's label.
    /// </summary>
    internal static ApproachVerbSpec ApproachSpec(string id) =>
        id switch
        {
            MenuIds.ApproachCleared => ApproachVerb(id, "Cleared approach", "CAPP", name => $"Cleared {name}") with
            {
                Visuals = ApproachVisualRows.Clearance,
                ForceToggleVerb = "CAPPF",
                OffersCancelIfr = true,
            },
            MenuIds.ApproachJoin => ApproachVerb(id, "Join approach", "JAPP", name => $"Join {name}"),
            MenuIds.ApproachClearedStraightIn => ApproachVerb(id, "Cleared straight-in", "CAPPSI", name => $"Cleared straight-in {name}"),
            MenuIds.ApproachJoinStraightIn => ApproachVerb(id, "Join straight-in", "JAPPSI", name => $"Join straight-in {name}"),
            MenuIds.ApproachClearedForce => ApproachVerb(id, "Cleared approach (force)", "CAPPF", name => $"Cleared {name} (force)") with
            {
                IsForced = true,
            },
            MenuIds.ApproachJoinForce => ApproachVerb(id, "Join approach (force)", "JAPPF", name => $"Join {name} (force)") with { IsForced = true },
            MenuIds.ApproachJoinFinalCourse => ApproachVerb(id, "Join final approach course", "JFAC", name => $"Join final course {name}"),
            MenuIds.ApproachExpect => ApproachVerb(id, "Expect approach", "EAPP", name => $"Expect {name}") with
            {
                Visuals = ApproachVisualRows.Expectation,
                OffersCancelIfr = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Not an approach entry."),
        };

    /// <summary>
    /// The Cleared visual approach entry's flyout: Cleared approach's own (its rows, field-in-sight greying, Force toggle and
    /// Cancel IFR), opened on its visual row.
    /// </summary>
    internal static ApproachVerbSpec ClearedVisualSpec => ApproachSpec(MenuIds.ApproachCleared) with { OpensOnVisual = true };

    /// <summary>
    /// An approach verb with no visual rows, no Force toggle, no Cancel IFR and unforced rows, its glyph the entry
    /// <paramref name="id"/>'s.
    /// </summary>
    private static ApproachVerbSpec ApproachVerb(string id, string label, string verb, Func<string, string> defaultLabel) =>
        new()
        {
            Label = label,
            Verb = verb,
            IsForced = false,
            Visuals = ApproachVisualRows.None,
            ForceToggleVerb = null,
            OffersCancelIfr = false,
            GlyphId = id,
            DefaultLabel = defaultLabel,
            OpensOnVisual = false,
        };

    /// <summary>The destination's published approaches; none without an aircraft or a destination.</summary>
    internal static IReadOnlyList<CifpApproachProcedure> DestinationApproaches(IMenuAircraft? aircraft) =>
        aircraft is { Destination.Length: > 0 } ? NavigationDatabase.Instance.GetApproaches(aircraft.Destination) : [];

    /// <summary>The destination's runway designators, whose presence offers the Cleared visual approach flyout; none without a destination.</summary>
    private static IReadOnlyList<string> DestinationRunways(IMenuAircraft? aircraft) =>
        aircraft is { Destination.Length: > 0 } ? RunwayDesignators.ForAirport(aircraft.Destination) : [];

    /// <summary>
    /// The runway a visual approach defaults to: the assigned runway, else the runway of the active approach, else
    /// that of the expected one, each id resolved from shorthand (R28L for R28LY) and looked up among the destination's
    /// approaches, or a visual id's own runway (<c>VIS28R</c>, <see cref="ApproachIds.TryParseVisual"/>); null when none
    /// applies.
    /// </summary>
    internal static string? SmartVisualRunway(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(aircraft.AssignedRunway))
        {
            return aircraft.AssignedRunway;
        }

        IReadOnlyList<CifpApproachProcedure> approaches = DestinationApproaches(aircraft);
        return VisualOrApproachRunway(aircraft.Destination, approaches, aircraft.ActiveApproachId)
            ?? VisualOrApproachRunway(aircraft.Destination, approaches, aircraft.ExpectedApproach);
    }

    private static string? VisualOrApproachRunway(string destination, IReadOnlyList<CifpApproachProcedure> approaches, string? approachId) =>
        ApproachIds.TryParseVisual(approachId, out string? visualRunway) ? visualRunway : ApproachRunway(destination, approaches, approachId);

    /// <summary>
    /// The runway of the approach <paramref name="approachId"/> names at <paramref name="destination"/>, resolved from
    /// shorthand, among <paramref name="approaches"/>; null when it names none.
    /// </summary>
    private static string? ApproachRunway(string destination, IReadOnlyList<CifpApproachProcedure> approaches, string? approachId)
    {
        if ((approaches.Count == 0) || string.IsNullOrEmpty(approachId))
        {
            return null;
        }

        string? resolvedId = NavigationDatabase.Instance.ResolveApproachId(destination, approachId);
        if (resolvedId is null)
        {
            return null;
        }

        string? runway = approaches.FirstOrDefault(a => string.Equals(a.ApproachId, resolvedId, StringComparison.OrdinalIgnoreCase))?.Runway;
        return string.IsNullOrEmpty(runway) ? null : runway;
    }

    /// <summary>
    /// The Cleared visual approach entry: the Cleared approach flyout (<see cref="ClearedVisualSpec"/>) opened on the default
    /// runway's tab (<see cref="SmartVisualRunway"/>) with its visual row focused. Disabled with the reason beside its label,
    /// its strip button inert, when there is no runway to clear a visual approach to: no destination, or a destination without
    /// runway data. Offered to IFR aircraft only (the sim clears only an IFR one for a visual).
    /// </summary>
    private static MenuItem BuildClearedVisual(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string? reason = aircraft switch
        {
            not { Destination.Length: > 0 } => NoDestinationReason,
            _ when DestinationRunways(aircraft).Count == 0 => NoRunwayDataReason(aircraft.Destination),
            _ => null,
        };
        return (reason is null) ? ApproachFlyout.Build(label, ClearedVisualSpec, aircraft, context, host) : Unavailable(label, reason);
    }

    /// <summary>
    /// The Join STAR entry: the tabbed STAR flyout over the destination's STARs (<see cref="JoinStarFlyout"/>), the filed STAR
    /// (<see cref="FiledStar"/>) first. Disabled with the reason beside its label, its strip button inert, without a
    /// destination or at a destination with no STARs.
    /// </summary>
    private static MenuItem BuildJoinStar(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is not { Destination.Length: > 0 })
        {
            return Unavailable(label, NoDestinationReason);
        }

        IReadOnlyList<JoinStarTab> tabs = JoinStarFlyout.Tabs(aircraft.Destination, FiledStar(aircraft));
        return (tabs.Count > 0) ? JoinStarFlyout.Build(label, tabs, context, host) : Unavailable(label, $"no STARs at {aircraft.Destination}");
    }

    /// <summary>
    /// An entry with nothing to pick: disabled, headed with <paramref name="label"/> and <paramref name="reason"/> faint beside
    /// it (<see cref="MenuDisabledReasonRow"/>), its strip button faded and inert.
    /// </summary>
    private static MenuItem Unavailable(string label, string reason)
    {
        var item = new MenuItem { Header = label };
        MenuDisabledReasonRow.Apply(item, label, reason);
        QuickCommandStrip.MarkInert(item, $"{label} · {reason}");
        return item;
    }

    /// <summary>Why an entry that picks at the aircraft's destination has nothing to pick when it has none.</summary>
    internal const string NoDestinationReason = "no destination";

    /// <summary>Why a pattern entry has nothing to pick for an aircraft with neither a destination nor a departure airport.</summary>
    internal const string NoAirportReason = "no airport";

    /// <summary>Why an entry that picks a runway at <paramref name="airport"/> has nothing to pick there.</summary>
    internal static string NoRunwayDataReason(string airport) => $"no runway data at {airport}";

    /// <summary>
    /// The STAR the flight plan files: the first route token the navigation data resolves as a STAR into the
    /// destination; null without a destination, a route or such a token.
    /// </summary>
    private static string? FiledStar(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || string.IsNullOrEmpty(aircraft.Destination) || string.IsNullOrEmpty(aircraft.Route))
        {
            return null;
        }

        foreach (string token in RouteTokens(aircraft.Route))
        {
            if (NavigationDatabase.Instance.GetStar(aircraft.Destination, token) is { } star)
            {
                return star.ProcedureId;
            }
        }

        return null;
    }

    /// <summary>The non-blank tokens of a filed route, split on spaces and dots.</summary>
    private static IEnumerable<string> RouteTokens(string route) =>
        route.Split(RouteSeparators, StringSplitOptions.RemoveEmptyEntries).Select(token => token.Trim()).Where(token => token.Length > 0);

    /// <summary>
    /// The airways the flight plan files, in filed order without repeats; none without a route or the navigation data. The Join
    /// airway flyout lists them first (<see cref="JoinAirwayFlyout"/>).
    /// </summary>
    internal static IReadOnlyList<string> FiledAirways(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || string.IsNullOrEmpty(aircraft.Route) || (NavigationDatabase.InstanceOrNull is null))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var airways = new List<string>();
        foreach (string token in RouteTokens(aircraft.Route))
        {
            if (NavigationDatabase.Instance.IsAirway(token) && seen.Add(token))
            {
                airways.Add(token);
            }
        }

        return airways;
    }

    private static MenuItem BuildAssumeAndTrack(MenuContext context, IMenuHost host)
    {
        // Two commands on purpose: the server does not couple them, and TRACK is the same track command
        // the Track submenu sends, so a refused ASSUME leaves the track state untouched.
        var item = new MenuItem { Header = "Assume and track" };
        item.Click += async (_, _) =>
        {
            await host.SendAsync(context.Callsign, "ASSUME", context.Initials);
            await host.SendAsync(context.Callsign, "TRACK", context.Initials);
        };
        return item;
    }
}
