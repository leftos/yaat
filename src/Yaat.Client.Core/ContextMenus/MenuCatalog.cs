using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Every context-menu action the catalog knows, one <see cref="MenuCatalogEntry"/> per <see cref="MenuIds"/>
/// identifier. A leaf's builder sends its command text through <see cref="IMenuHost.SendAsync"/>; an input leaf
/// opens the host's input popup and formats the submitted text into the command; a list or filtered-list picker opens
/// the host's list popup over values the catalog computes (headings, altitudes, speeds, fixes, runways, STARs, airways)
/// and formats the pick into the command, choosing its form from the data present when the menu is built; an approach
/// picker is a leaf naming the default approach with a grouped "(other)" submenu beside it, else the grouped submenu
/// alone (<see cref="ApproachPickerBuilder"/>); a host leaf asks the host for the item itself, for the entries that open a host surface or act on the
/// surface — the warp popup, the flight-plan editor, route and push-route drawing, and the ground's hold-short,
/// follow, give-way, push-back-to and preset-taxi submenus over the choices the host answers (the pushback faces are
/// flat items of a companion helper, <see cref="BuildPushbackFaces"/>), and so is the delayed spawn's Change spawn
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
    /// <summary>
    /// The approach picker entries, in Approach submenu order. Each one offers its "(other)" companion
    /// (<see cref="BuildApproachOther"/>) beside a default approach, in the Approach submenu and in the quick list.
    /// </summary>
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
        new(
            MenuIds.TrackInitiateHandoff,
            "Initiate handoff…",
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.IsOwned(ac),
            (_, context, host) => BuildInput("Initiate handoff…", "Position ID", BlankInput.Closes, input => $"HO {input}", context, host)
        ),
        Leaf(MenuIds.TrackCancelHandoff, "Cancel handoff", "CANCEL", (ac, _) => AircraftCommandApplicability.HasHandoffInProgress(ac)),
        new(
            MenuIds.TrackPointOut,
            "Point out…",
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.IsOwned(ac),
            (_, context, host) => BuildInput("Point out…", "Position ID", BlankInput.Closes, input => $"PO {input}", context, host)
        ),
        Leaf(MenuIds.TrackAcknowledgePointout, "Acknowledge pointout", "OK", (ac, _) => AircraftCommandApplicability.HasPendingPointout(ac)),
        InputLeaf(MenuIds.SquawkCode, "Squawk…", "Code (0000-7777)", BlankInput.Closes, input => $"SQ {int.Parse(input)}"),
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
        new(
            MenuIds.AskPilotCustom,
            "Custom…",
            MenuFlightRules.Both,
            CanAskPilot,
            (_, context, host) => BuildInput("Custom…", "Text", BlankInput.Closes, input => $"SAY {input}", context, host)
        ),
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
        InputLeaf(MenuIds.DataBlockScratchpad, "Scratchpad…", "Text", BlankInput.Closes, input => $"SP {input}"),
        InputLeaf(MenuIds.DataBlockTempAltitude, "Temporary altitude…", "Altitude", BlankInput.Closes, input => $"TEMPALT {int.Parse(input)}"),
        InputLeaf(MenuIds.DataBlockCruise, "Cruise…", "Altitude", BlankInput.Closes, input => $"CRUISE {int.Parse(input)}"),
        Leaf(MenuIds.DataBlockAnnotate, "Annotate", "ANNOTATE", Always),
        HostLeaf(MenuIds.SimControlWarp, "Warp…", Always, BuildWarp),
        Leaf(MenuIds.SimControlDelete, "Delete", "DEL", Always),
        HostLeaf(MenuIds.AircraftEditFlightPlan, "Edit flight plan", CanEditFlightPlan, BuildEditFlightPlan),
        HostLeaf(MenuIds.AircraftCommand, "Command…", Always, BuildCommand),
        HostLeaf(MenuIds.AircraftNote, "Note…", Always, BuildNote),
        Leaf(MenuIds.HeadingPresent, "Present heading", "FPH", Always),
        HeadingList(MenuIds.HeadingFly, "Fly heading", "FH"),
        HeadingList(MenuIds.HeadingTurnLeft, "Turn left", "TL"),
        HeadingList(MenuIds.HeadingTurnRight, "Turn right", "TR"),
        RelativeTurnList(MenuIds.HeadingTurnLeftDegrees, "Turn left (degrees)", "LT"),
        RelativeTurnList(MenuIds.HeadingTurnRightDegrees, "Turn right (degrees)", "RT"),
        Picker(MenuIds.AltitudeMaintain, "Maintain altitude", BuildMaintainAltitude),
        Picker(MenuIds.SpeedAssign, "Assign speed", BuildAssignSpeed),
        InputLeaf(MenuIds.SpeedCustom, "Speed…", "Speed (knots)", BlankInput.Closes, input => $"SPD {int.Parse(input)}"),
        Leaf(MenuIds.SpeedNormal, "Resume normal speed", "RNS", Always),
        Picker(MenuIds.SpeedFinalApproach, "Reduce to final approach speed", BuildFinalApproachSpeed),
        FixPicker(MenuIds.NavigationDirectTo, "Direct to…", "DCT", Always, DirectToFixes),
        FixPicker(MenuIds.NavigationAppendDirectTo, "Append direct to…", "ADCT", IsNavigatingToFix, RouteFixes),
        Leaf(MenuIds.HoldPresentLeft, "Hold present position (left)", "HPPL", Always),
        Leaf(MenuIds.HoldPresentRight, "Hold present position (right)", "HPPR", Always),
        FixPicker(MenuIds.HoldFixLeft, "Hold at fix (left)…", "HFIXL", Always, NoRouteFixes),
        FixPicker(MenuIds.HoldFixRight, "Hold at fix (right)…", "HFIXR", Always, NoRouteFixes),
        .. ApproachPickerIds.Select(ApproachPicker),
        Picker(MenuIds.ApproachClearedVisual, ClearedVisualLabel, BuildClearedVisual),
        Leaf(MenuIds.ApproachReportFieldInSight, "Report field in sight", "RFIS", Always),
        Picker(MenuIds.ApproachReportTrafficInSight, "Report traffic in sight…", BuildReportTrafficInSight),
        Leaf(MenuIds.ApproachReportBase, "Turning base", "REPORT BASE", Always),
        Leaf(MenuIds.ApproachReportFinal, "Turning final", "REPORT FINAL", Always),
        Leaf(MenuIds.ApproachReportCrosswind, "Turning crosswind", "REPORT CROSSWIND", Always),
        Leaf(MenuIds.ApproachReportDownwind, "Turning downwind", "REPORT DOWNWIND", Always),
        InputLeaf(MenuIds.ApproachReportNMileFinal, "At N-mile final…", "Distance (NM)", BlankInput.Closes, input => $"REPORT {input} FINAL"),
        InputLeaf(MenuIds.ApproachReportAtFix, "At fix…", "Fix name", BlankInput.Closes, input => $"REPORT {input}"),
        Leaf(MenuIds.ApproachReportOffBase, "Base", "REPORT OFF BASE", Always),
        Leaf(MenuIds.ApproachReportOffFinal, "Final", "REPORT OFF FINAL", Always),
        Leaf(MenuIds.ApproachReportOffCrosswind, "Crosswind", "REPORT OFF CROSSWIND", Always),
        Leaf(MenuIds.ApproachReportOffDownwind, "Downwind", "REPORT OFF DOWNWIND", Always),
        Leaf(MenuIds.ApproachReportOffAll, "All reports", "REPORT OFF", Always),
        Picker(MenuIds.ProceduresJoinStar, JoinStarLabel, BuildJoinStar),
        Leaf(MenuIds.ProceduresClimbViaSid, "Climb via SID", "CVIA", Always),
        Leaf(MenuIds.ProceduresDescendViaStar, "Descend via STAR", "DVIA", Always),
        RouteFixPicker(MenuIds.ProceduresCrossFix, CrossFixLabel, CrossFixCommand),
        RouteFixPicker(MenuIds.ProceduresDepartFix, DepartFixLabel, DepartFixCommand),
        InputLeaf(MenuIds.ProceduresPtac, "PTAC…", "PTAC arguments", BlankInput.Closes, input => $"PTAC {input}"),
        Picker(MenuIds.ProceduresJoinAirway, JoinAirwayLabel, BuildJoinAirway),
        RadialPicker(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound…", "JRADO", fix => $"Bearing from {fix} (0-360)"),
        RadialPicker(MenuIds.ProceduresJoinRadialInbound, "Join radial inbound…", "JRADI", fix => $"Bearing to {fix} (0-360)"),
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
        ExitFlyout(MenuIds.TowerExitLeft, ExitSide.Left),
        ExitFlyout(MenuIds.TowerExitRight, ExitSide.Right),
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
        HostLeaf(
            MenuIds.GroundPushbackTo,
            "Push back to…",
            (ac, _) => AircraftCommandApplicability.CanPushBack(ac),
            (label, _, context, host) => BuildChoiceSubmenu(label, host.GetPushbackToChoices(context.Callsign), context, host)
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
        HostLeaf(MenuIds.GroundHoldShort, "Hold short of…", (ac, _) => AircraftCommandApplicability.CanHoldShort(ac), BuildHoldShort),
        HostLeaf(
            MenuIds.GroundFollow,
            "Follow…",
            AircraftCommandApplicability.CanFollowBehind,
            (label, _, context, host) => BuildGroundTraffic(label, "FOLLOWG", includeSurfaceShadows: true, context, host)
        ),
        HostLeaf(
            MenuIds.GroundGiveWay,
            "Give way to…",
            AircraftCommandApplicability.CanGiveWayTo,
            (label, _, context, host) => BuildGroundTraffic(label, "GW", includeSurfaceShadows: false, context, host)
        ),
        HostLeaf(
            MenuIds.GroundTaxiPreset,
            "Preset taxi route",
            (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac),
            (label, _, context, host) => BuildTaxiRouteSubmenu(label, host.GetPresetTaxiChoices(context.Callsign), context, host)
        ),
        HostLeaf(
            MenuIds.GroundTaxiToRunway,
            "Taxi to runway",
            (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac),
            (label, _, context, host) =>
                LazySubmenu.Create(
                    label,
                    TaxiToRunwayPlaceholder,
                    () => TaxiToRunwayItems(host.GetTaxiToRunwayChoices(context.Callsign), context, host)
                )
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
                    $"The '{MenuIds.LiveTrafficAssumeSelected}' entry builds no item; SharedMenuGroups.AddAssumeSelected builds it for "
                        + "the aircraft list through MenuCatalog.BuildAssumeSelected, which takes the shadows the list has selected."
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
            (_, _, _, _) =>
                throw new InvalidOperationException(
                    $"The '{MenuIds.PointTaxiToRunway}' entry builds no item; SharedMenuGroups.AddTaxiToRunwayEnds adds one item per "
                        + "runway end through MenuCatalog.BuildTaxiToRunwayEnds."
                )
        ),
        PointEntry(MenuIds.PointPushTo, "Push to", AtNodePushable, BuildPointPushTo),
        PointEntry(MenuIds.PointCustomTaxi, "Custom taxi…", AtNodeTaxiable, BuildPointCustomTaxi),
        PointEntry(
            MenuIds.PointWarpHere,
            "Warp here",
            (ac, context) => (context.Click.Point is not null) && AircraftCommandApplicability.IsControllable(ac),
            BuildPointWarp
        ),
        new(
            MenuIds.PatternMakeLeftTraffic,
            "Make left closed traffic",
            MenuFlightRules.VfrOnly,
            CanMakeClosedTraffic,
            (_, context, host) => BuildSend("Make left closed traffic", "MLT", context, host)
        ),
        new(
            MenuIds.PatternMakeRightTraffic,
            "Make right closed traffic",
            MenuFlightRules.VfrOnly,
            CanMakeClosedTraffic,
            (_, context, host) => BuildSend("Make right closed traffic", "MRT", context, host)
        ),
        new(
            MenuIds.PatternFollow,
            "Follow traffic…",
            MenuFlightRules.VfrOnly,
            CanFollowTraffic,
            (_, context, host) => BuildInput("Follow traffic…", "Traffic callsign (optional)", BlankInput.Submits, FormatFollow, context, host)
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
            (_, context, host) => BuildInput("Hold at fix…", HoldPatternPlaceholder, BlankInput.Closes, input => $"HOLDP {input}", context, host)
        ),
    ];

    /// <summary>The placeholder of the holding-pattern input, naming the <c>HOLDP</c> arguments in order.</summary>
    private const string HoldPatternPlaceholder = "Fix, inbound course, legs, turns (e.g. SUNOL 180 1M R)";

    /// <summary>
    /// Make closed traffic in the air (<c>MLT</c> / <c>MRT</c>), which puts the aircraft into the pattern: a VFR command,
    /// offered to an IFR aircraft only under the controller's full VFR setting. On the ground the verb only records the
    /// pattern side, which the takeoff clearance's closed-traffic modifiers already cover.
    /// </summary>
    private static Func<IMenuAircraft?, MenuContext, bool> CanMakeClosedTraffic =>
        (ac, context) => AircraftCommandApplicability.CanIssuePatternManeuvers(ac, context.VfrCommandsForIfr) && (ac is { IsOnGround: false });

    /// <summary>
    /// Follow traffic in the air (<c>FOLLOW</c>): a VFR command the sim takes only after the aircraft reported traffic in
    /// sight (<see cref="IMenuAircraft.LastReportedTrafficCallsign"/>), which a bare <c>FOLLOW</c> follows.
    /// </summary>
    private static Func<IMenuAircraft?, MenuContext, bool> CanFollowTraffic =>
        (ac, context) =>
            AircraftCommandApplicability.CanIssuePatternManeuvers(ac, context.VfrCommandsForIfr)
            && (ac is { IsOnGround: false })
            && !string.IsNullOrEmpty(ac.LastReportedTrafficCallsign);

    /// <summary><c>FOLLOW</c> with the typed callsign, or bare (the last traffic reported in sight) when the input is blank.</summary>
    private static string FormatFollow(string input) => string.IsNullOrWhiteSpace(input) ? "FOLLOW" : $"FOLLOW {input.Trim()}";

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

    /// <summary>The aircraft list's multi-selection assume item's label, which the number of selected shadows follows.</summary>
    private const string AssumeSelectedLabel = "Assume selected live traffic";

    /// <summary>The Release (HFR) entry's label, which the header shows under the title on every view.</summary>
    private const string ReleaseHeldLabel = "Release (HFR)";

    /// <summary>The placeholder of the Cleared for takeoff submenu's free-text item.</summary>
    private const string ClearedForTakeoffPlaceholder = "CTO arg (e.g. RH 3000, LT 270, DCT BERKS)";

    /// <summary>
    /// The VFR-only departure instructions the Cleared for takeoff submenu offers after the default and runway heading:
    /// each item's text and the argument it sends after <c>CTO</c>.
    /// </summary>
    private static readonly (string Label, string Argument)[] VfrTakeoffModifiers =
    [
        ("Fly on course", "OC"),
        ("Make left traffic", "MLT"),
        ("Make right traffic", "MRT"),
        ("Turn left crosswind", "MLC"),
        ("Turn right crosswind", "MRC"),
        ("Turn left downwind", "MLD"),
        ("Turn right downwind", "MRD"),
        ("Left 270", "ML270"),
        ("Right 270", "MR270"),
        ("360 overhead", "360"),
    ];

    /// <summary>The visual-approach entry's label, which its smart-default leaf and its "(other)" companion extend.</summary>
    private const string ClearedVisualLabel = "Cleared visual approach";

    /// <summary>The Join STAR entry's label, which its filed-STAR leaf and its "(other)" companion extend.</summary>
    private const string JoinStarLabel = "Join STAR";

    /// <summary>The Cross fix entry's label, which its single-fix leaf and its "(other)" companion extend.</summary>
    private const string CrossFixLabel = "Cross fix";

    private const string CrossFixCommand = "CFIX";

    /// <summary>The Depart fix entry's label, which its single-fix leaf and its "(other)" companion extend.</summary>
    private const string DepartFixLabel = "Depart fix";

    private const string DepartFixCommand = "DEPART";

    /// <summary>The Join airway entry's label, which its single-airway leaf and its "(other)" companion extend.</summary>
    private const string JoinAirwayLabel = "Join airway";

    private const string JoinAirwayCommand = "JAWY";

    /// <summary>The placeholder of the free-text fix inputs.</summary>
    private const string FixNamePlaceholder = "Fix name";

    /// <summary>The placeholder of the free-text airway inputs.</summary>
    private const string AirwayIdPlaceholder = "Airway ID";

    /// <summary>The separators a filed route's tokens are split on.</summary>
    private static readonly char[] RouteSeparators = [' ', '.'];

    /// <summary>The headings the heading pickers list, 005 to 360 in fives.</summary>
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

    /// <summary>
    /// A submenu over <paramref name="children"/> whose header is <paramref name="header"/> drawn by
    /// <paramref name="template"/>, its UI Automation name the header's one-line text. Clicking it sends nothing; the caller
    /// adds any hover behaviour.
    /// </summary>
    internal static MenuItem BuildTemplatedSubmenu<THeader>(THeader header, FuncDataTemplate<THeader> template, IReadOnlyList<Control> children)
        where THeader : class
    {
        var item = new MenuItem { Header = header, HeaderTemplate = template };
        AutomationProperties.SetName(item, HeaderText(header));
        foreach (Control child in children)
        {
            item.Items.Add(child);
        }

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
    /// The aircraft list's multi-selection assume item: it assumes every shadow named in
    /// <paramref name="selectedShadows"/>, in the order given, and its label carries how many there are. The selection
    /// belongs to the list rather than to the menu context, so this companion builder takes that list instead of the
    /// entry's own builder shape.
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
    /// The Hold short of… submenu: the host's route line (<c>route S T V W4 · RWY 30</c>) as a disabled row over a
    /// separator, then one row per bar along the route, nearest first, sending its finished <c>HS</c> command and
    /// previewing the route to the bar when the pointer enters it. Null when the route offers no bar.
    /// </summary>
    private static MenuItem? BuildHoldShort(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        HoldShortMenu holdShort = host.GetHoldShortChoices(context.Callsign);
        if (holdShort.Rows.Count == 0)
        {
            return null;
        }

        var menu = new MenuItem { Header = label };
        if (holdShort.RouteLine is { } routeLine)
        {
            var line = new MenuItem
            {
                Header = routeLine,
                HeaderTemplate = new FuncDataTemplate<string>((text, _) => HoldShortRouteLineView(text)),
                IsEnabled = false,
            };
            menu.Items.Add(line);
            menu.Items.Add(new Separator());
        }

        foreach (HoldShortChoice row in holdShort.Rows)
        {
            menu.Items.Add(BuildHoldShortRow(row, context, host));
        }

        return menu;
    }

    private static readonly IImmutableSolidColorBrush TaxiwayBadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#4FB8A8"));

    private static readonly IImmutableSolidColorBrush RunwayBadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));

    /// <summary>
    /// One hold-short row: the TW/RW badge, the name followed by the dimmed "where", then the distance right-aligned
    /// and the command. Sends its command, and previews the route to its bar on hover.
    /// </summary>
    private static MenuItem BuildHoldShortRow(HoldShortChoice row, MenuContext context, IMenuHost host)
    {
        HoldShortRowLabel label = row.Label;
        IImmutableSolidColorBrush badgeBrush = (label.Badge == HoldShortChoice.RunwayBadge) ? RunwayBadgeBrush : TaxiwayBadgeBrush;
        var header = new MenuCommandRow
        {
            Badge = new MenuDetailBadge(label.Badge, badgeBrush),
            Name = label.Name,
            EmphasizeName = true,
            IsHighlighted = false,
            Detail = label.Where,
            DetailPlacement = MenuDetailPlacement.Inline,
            Distance = $"~{label.DistanceFt.ToString("N0", CultureInfo.InvariantCulture)} ft",
            Command = row.Command,
        };
        MenuItem item = BuildTemplatedSend(header, MenuCommandRowTemplate.Instance, row.Command, context, host);
        item.PointerEntered += (_, _) => host.SetRoutePreview(row.Preview);
        return item;
    }

    private static readonly IImmutableSolidColorBrush PresetBadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#7FD1B9"));

    /// <summary>A submenu of one command row per taxi route (<see cref="BuildTaxiRouteRow"/>); null when there are none.</summary>
    private static MenuItem? BuildTaxiRouteSubmenu(string label, IReadOnlyList<TaxiRouteRow> rows, MenuContext context, IMenuHost host)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var menu = new MenuItem { Header = label };
        foreach (TaxiRouteRow row in rows)
        {
            menu.Items.Add(BuildTaxiRouteRow(row, context, host));
        }

        return menu;
    }

    /// <summary>What Taxi to runway shows until it first opens and searches the routes (<see cref="LazySubmenu"/>).</summary>
    internal const string TaxiToRunwayPlaceholder = "Finding runway entries…";

    /// <summary>What Taxi to runway shows when no runway entry or preset is reachable.</summary>
    internal const string NoRunwayEntry = "No runway entry found";

    /// <summary>
    /// The Taxi to runway submenu's items, built when it first opens: each inline group's title as a disabled row over its
    /// rows (<see cref="BuildTaxiRouteRow"/>), the groups separated, then Other runways with one submenu per end over its
    /// rows. One disabled <see cref="NoRunwayEntry"/> row when there is no group.
    /// Other runways whose ends are still to be searched (<see cref="TaxiToRunwayMenu.FindOther"/>) is always there and
    /// searches them when it first opens (<see cref="LazySubmenu"/>); found ones show only when some end has a row.
    /// </summary>
    private static List<Control> TaxiToRunwayItems(TaxiToRunwayMenu taxiToRunway, MenuContext context, IMenuHost host)
    {
        List<Control> items = [];
        foreach (TaxiToRunwayGroup group in taxiToRunway.Inline)
        {
            if (items.Count > 0)
            {
                items.Add(new Separator());
            }

            items.Add(new MenuItem { Header = group.Title, IsEnabled = false });
            items.AddRange(group.Rows.Select(row => BuildTaxiRouteRow(row, context, host)));
        }

        MenuItem? other =
            (taxiToRunway.FindOther is { } findOther)
                ? LazySubmenu.Create(OtherRunwaysHeader, TaxiToRunwayPlaceholder, () => OtherRunwayItems(findOther(), context, host))
                : OtherRunwaysSubmenu(taxiToRunway.Other, context, host);
        if (other is not null)
        {
            if (items.Count > 0)
            {
                items.Add(new Separator());
            }

            items.Add(other);
        }

        return (items.Count > 0) ? items : [NoRunwayEntryRow()];
    }

    /// <summary>The header of Taxi to runway's submenu of the runway ends not shown inline.</summary>
    internal const string OtherRunwaysHeader = "Other runways";

    /// <summary>Other runways over the ends already found; null when none has a row.</summary>
    private static MenuItem? OtherRunwaysSubmenu(IReadOnlyList<TaxiToRunwayGroup> groups, MenuContext context, IMenuHost host)
    {
        List<Control> ends = OtherRunwayEnds(groups, context, host);
        if (ends.Count == 0)
        {
            return null;
        }

        var other = new MenuItem { Header = OtherRunwaysHeader };
        foreach (Control end in ends)
        {
            other.Items.Add(end);
        }

        return other;
    }

    /// <summary>The items of a lazily searched Other runways: one submenu per end, else one disabled <see cref="NoRunwayEntry"/> row.</summary>
    private static List<Control> OtherRunwayItems(IReadOnlyList<TaxiToRunwayGroup> groups, MenuContext context, IMenuHost host)
    {
        List<Control> ends = OtherRunwayEnds(groups, context, host);
        return (ends.Count > 0) ? ends : [NoRunwayEntryRow()];
    }

    /// <summary>One submenu per end of <paramref name="groups"/> over its rows (<see cref="BuildTaxiRouteSubmenu"/>).</summary>
    private static List<Control> OtherRunwayEnds(IReadOnlyList<TaxiToRunwayGroup> groups, MenuContext context, IMenuHost host) =>
        [.. groups.Select(group => BuildTaxiRouteSubmenu(group.Title, group.Rows, context, host)).OfType<MenuItem>()];

    /// <summary>The disabled row saying no runway entry or preset is reachable.</summary>
    private static MenuItem NoRunwayEntryRow() => new() { Header = NoRunwayEntry, IsEnabled = false };

    /// <summary>
    /// One taxi route row: the PR or RW badge, the name followed by the dimmed reason, the runway an intersection leaves
    /// ahead and the via, then the distance right-aligned and the command
    /// (<c>RW At W4 · nearest · ~7,600 ft avail · via S T V W4 · ~2,300 ft — TAXI S T V W4 30</c>), all drawn bold on a
    /// highlighted row. A preset sends its command; a runway entry opens its variants (<see cref="BuildChoice"/>). Either
    /// previews its route on hover.
    /// </summary>
    private static MenuItem BuildTaxiRouteRow(TaxiRouteRow row, MenuContext context, IMenuHost host)
    {
        string? available = (row.AvailableFt is { } availableFt) ? $"~{availableFt.ToString("N0", CultureInfo.InvariantCulture)} ft avail" : null;
        string?[] detail = [row.Reason, available, row.Via];
        var header = new MenuCommandRow
        {
            Badge = new MenuDetailBadge(row.Badge, (row.Badge == TaxiRouteRow.PresetBadge) ? PresetBadgeBrush : RunwayBadgeBrush),
            Name = row.Name,
            EmphasizeName = true,
            IsHighlighted = row.IsHighlighted,
            Detail = string.Join(" · ", detail.OfType<string>()),
            DetailPlacement = MenuDetailPlacement.Inline,
            Distance = $"~{row.DistanceFt.ToString("N0", CultureInfo.InvariantCulture)} ft",
            Command = row.Command,
        };
        MenuItem item =
            (row.Variants.Count == 0)
                ? BuildTemplatedSend(header, MenuCommandRowTemplate.Instance, row.Command, context, host)
                : BuildTemplatedSubmenu(
                    header,
                    MenuCommandRowTemplate.Instance,
                    [.. row.Variants.Select(variant => BuildChoice(variant, context, host))]
                );
        item.PointerEntered += (_, _) => host.SetRoutePreview(row.Preview);
        return item;
    }

    /// <summary>The route line's view: dimmed, in the monospace font.</summary>
    private static TextBlock HoldShortRouteLineView(string routeLine)
    {
        var text = new TextBlock
        {
            Text = routeLine,
            FontSize = 12,
            Opacity = 0.7,
        };
        text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        return text;
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
    /// A submenu over host-answered <paramref name="choices"/>: one item per choice, sending its finished command, and
    /// previewing its route when the pointer enters an item whose choice carries one. Nothing clears the preview when
    /// the pointer leaves. Null when there are no choices.
    /// </summary>
    private static MenuItem? BuildChoiceSubmenu(string label, IReadOnlyList<MenuCommandChoice> choices, MenuContext context, IMenuHost host)
    {
        if (choices.Count == 0)
        {
            return null;
        }

        var menu = new MenuItem { Header = label };
        foreach (MenuCommandChoice choice in choices)
        {
            menu.Items.Add(BuildChoice(choice, context, host));
        }

        return menu;
    }

    /// <summary>
    /// One host-answered choice as a menu control: <see cref="MenuCommandChoice.Separator"/> as a separator, a choice
    /// with children as a submenu over them (built the same way), one with a command as an item that sends it, and one
    /// with neither as a disabled row. An item whose choice carries a route previews it when the pointer enters it.
    /// </summary>
    private static Control BuildChoice(MenuCommandChoice choice, MenuContext context, IMenuHost host)
    {
        if (ReferenceEquals(choice, MenuCommandChoice.Separator))
        {
            return new Separator();
        }

        MenuItem item;
        if (choice.Children.Count > 0)
        {
            item = new MenuItem { Header = choice.Label };
            foreach (MenuCommandChoice child in choice.Children)
            {
                item.Items.Add(BuildChoice(child, context, host));
            }
        }
        else if (choice.Command is { } command)
        {
            item = BuildSend(choice.Label, command, context, host);
        }
        else
        {
            item = new MenuItem { Header = choice.Label, IsEnabled = false };
        }

        if (choice.Preview is { } preview)
        {
            item.PointerEntered += (_, _) => host.SetRoutePreview(preview);
        }

        return item;
    }

    /// <summary>How many aircraft each section of the Follow… and Give way to… submenus lists before its More flyout.</summary>
    private const int GroundTrafficRowsPerSection = 4;

    /// <summary>
    /// The Follow… or Give way to… submenu over the host's ground traffic (<see cref="IMenuHost.GetGroundTrafficRows"/>),
    /// leaving out surface shadows unless <paramref name="includeSurfaceShadows"/>: a Moving section, then a Parked or
    /// holding section (<see cref="MenuGroundTrafficRow.IsMoving"/>), each in the host's nearest-first order
    /// (<see cref="AddGroundTrafficSection"/>). Every row sends <paramref name="verb"/> with its callsign and highlights its
    /// aircraft while the pointer is on it. Null when no aircraft is left to list.
    /// </summary>
    private static MenuItem? BuildGroundTraffic(string label, string verb, bool includeSurfaceShadows, MenuContext context, IMenuHost host)
    {
        List<MenuGroundTrafficRow> traffic =
        [
            .. host.GetGroundTrafficRows(context.Callsign).Where(row => includeSurfaceShadows || !row.IsSurfaceShadow),
        ];
        if (traffic.Count == 0)
        {
            return null;
        }

        var menu = new MenuItem { Header = label };
        var sender = new GroundTrafficRowSender(verb, context, host);
        AddGroundTrafficSection(menu.Items, "Moving", [.. traffic.Where(row => row.IsMoving)], sender);
        AddGroundTrafficSection(menu.Items, "Parked or holding", [.. traffic.Where(row => !row.IsMoving)], sender);
        return menu;
    }

    /// <summary>
    /// One section of a ground traffic submenu: its bold <paramref name="title"/>, the first
    /// <see cref="GroundTrafficRowsPerSection"/> of <paramref name="rows"/>, then <c>More ({n}, up to ~{d} ft)</c>, a flyout
    /// over the rest. Adds nothing for no rows.
    /// </summary>
    private static void AddGroundTrafficSection(ItemCollection items, string title, List<MenuGroundTrafficRow> rows, GroundTrafficRowSender sender)
    {
        if (rows.Count == 0)
        {
            return;
        }

        items.Add(SharedMenuGroups.SectionLabel(title));
        foreach (MenuGroundTrafficRow row in rows.Take(GroundTrafficRowsPerSection))
        {
            items.Add(sender.Row(row));
        }

        List<MenuGroundTrafficRow> rest = [.. rows.Skip(GroundTrafficRowsPerSection)];
        if (rest.Count == 0)
        {
            return;
        }

        var more = new MenuItem { Header = $"More ({rest.Count}, up to ~{RelativeGeometry.FeetText(rest.Max(row => row.DistanceFeet))})" };
        foreach (MenuGroundTrafficRow row in rest)
        {
            more.Items.Add(sender.Row(row));
        }

        items.Add(more);
    }

    /// <summary>
    /// An Exit left / Exit right entry: a submenu over the named exits ahead on its side
    /// (<see cref="BuildExitFlyout"/>), applicable on the rollout and while the aircraft has an exits-ahead list, and then
    /// only with an exit listed on its side (<see cref="AircraftCommandApplicability.CanExitRunway(IMenuAircraft?, ExitSide)"/>).
    /// </summary>
    private static MenuCatalogEntry ExitFlyout(string id, ExitSide side) =>
        new(
            id,
            ExitLabel(side),
            MenuFlightRules.Both,
            (ac, _) => AircraftCommandApplicability.CanExitRunway(ac, side),
            (ac, context, host) => BuildExitFlyout(side, ac, context, host)
        );

    private static string ExitLabel(ExitSide side) => (side == ExitSide.Left) ? "Exit left" : "Exit right";

    /// <summary>
    /// The Exit left / Exit right submenu, read from the aircraft's exits-ahead list as the menu opens: the section header
    /// (<see cref="ExitsAheadTitle"/>), one row per listed exit on <paramref name="side"/> in list order sending
    /// <c>EL</c>/<c>ER</c> with its taxiway, a separator, then the pilot's-choice row sending the bare verb. With no list, the
    /// pilot's-choice row alone; with a list holding no exit on this side, nothing (null). A row is sent as shown: the pilot
    /// refuses an exit that is no longer makeable.
    /// </summary>
    private static MenuItem? BuildExitFlyout(ExitSide side, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string verb = (side == ExitSide.Left) ? "EL" : "ER";
        var menu = new MenuItem { Header = ExitLabel(side) };
        if ((aircraft is { } listed) && (listed.ExitsAhead is { } exits))
        {
            List<ExitAheadDto> rows = [.. exits.Where(row => row.Side == side)];
            if (rows.Count == 0)
            {
                return null;
            }

            menu.Items.Add(SharedMenuGroups.SectionLabel(ExitsAheadTitle(side, listed)));
            foreach (ExitAheadDto row in rows)
            {
                menu.Items.Add(ExitRow(row, verb, context, host));
            }

            menu.Items.Add(new Separator());
        }

        menu.Items.Add(BuildSend($"{ExitLabel(side)} · pilot's choice", verb, context, host));
        return menu;
    }

    /// <summary>
    /// The exit flyout's section header. On the rollout the distances are from the aircraft: "Exits ahead, left side" (or
    /// right). On final they are the forecast's, from the landing threshold: "Exits ahead (distance from threshold)".
    /// </summary>
    private static string ExitsAheadTitle(ExitSide side, IMenuAircraft aircraft)
    {
        if (aircraft.CurrentPhase is not ("Landing" or "Runway Exit"))
        {
            return "Exits ahead (distance from threshold)";
        }

        return (side == ExitSide.Left) ? "Exits ahead, left side" : "Exits ahead, right side";
    }

    /// <summary>
    /// One exit row: the taxiway with "planned" under it for the planned exit, then the distance and the command it sends
    /// (<c>W2 · planned · ~1,800 ft — ER W2</c>, or <c>W3 · ~3,600 ft — ER W3</c>).
    /// </summary>
    private static MenuItem ExitRow(ExitAheadDto row, string verb, MenuContext context, IMenuHost host)
    {
        string command = $"{verb} {row.Taxiway}";
        var header = new MenuCommandRow
        {
            Badge = null,
            Name = row.Taxiway,
            EmphasizeName = false,
            IsHighlighted = false,
            Detail = row.Planned ? "planned" : null,
            DetailPlacement = MenuDetailPlacement.Stacked,
            Distance = $"~{RelativeGeometry.FeetText(row.DistanceFt)}",
            Command = command,
        };
        return BuildTemplatedSend(header, MenuCommandRowTemplate.Instance, command, context, host);
    }

    /// <summary>
    /// Builds a ground traffic submenu's rows: the callsign and type over the dimmed state, then the distance and the
    /// command. Each sends the verb with its callsign and highlights its aircraft on hover.
    /// </summary>
    private sealed class GroundTrafficRowSender(string verb, MenuContext context, IMenuHost host)
    {
        public MenuItem Row(MenuGroundTrafficRow row)
        {
            string command = $"{verb} {row.Callsign}";
            var header = new MenuCommandRow
            {
                Badge = null,
                Name = (row.AircraftType.Length > 0) ? $"{row.Callsign} · {row.AircraftType}" : row.Callsign,
                EmphasizeName = false,
                IsHighlighted = false,
                Detail = row.State,
                DetailPlacement = MenuDetailPlacement.Stacked,
                Distance = $"~{RelativeGeometry.FeetText(row.DistanceFeet)}",
                Command = command,
            };
            MenuItem item = BuildTemplatedSend(header, MenuCommandRowTemplate.Instance, command, context, host);
            item.PointerEntered += (_, _) => host.HighlightAircraft(row.Callsign);
            return item;
        }
    }

    /// <summary>
    /// The Cleared for takeoff submenu, the same on every view: headed with the held runway, else the assigned one
    /// (<see cref="HoldShortMenuHelper.HeldRunway(IMenuAircraft?)"/>), the default clearance (the filed SID for IFR,
    /// runway heading for VFR) and an explicit runway heading for either, then the VFR-only departure instructions when
    /// <see cref="AircraftCommandApplicability.ShowVfrTakeoffModifiers"/> allows them, then the trailing separator and
    /// Custom item: blank sends a bare <c>CTO</c>, anything else is trimmed and sent after it.
    /// </summary>
    private static MenuItem BuildClearedForTakeoff(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = ClearedForTakeoffLabel + DisplaySuffix(HoldShortMenuHelper.HeldRunway(aircraft)) };
        menu.Items.Add(BuildSend("Default (SID/on course)", "CTO", context, host));
        menu.Items.Add(BuildSend("Fly runway heading", "CTO RH", context, host));
        if (AircraftCommandApplicability.ShowVfrTakeoffModifiers(aircraft, context.VfrCommandsForIfr))
        {
            foreach ((string label, string argument) in VfrTakeoffModifiers)
            {
                menu.Items.Add(BuildSend(label, $"CTO {argument}", context, host));
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            BuildInput(
                "Custom…",
                ClearedForTakeoffPlaceholder,
                BlankInput.Submits,
                input => string.IsNullOrWhiteSpace(input) ? "CTO" : $"CTO {input.Trim()}",
                context,
                host
            )
        );
        return menu;
    }

    /// <summary>
    /// A pattern entry: with a runway assigned, a leaf naming it that sends the verb for the runway as assigned;
    /// otherwise a picker over <see cref="PatternEntryRunways"/>, or free text when there are none, where a blank answer
    /// sends the bare verb and any other is sent after it as typed. <see cref="BuildPatternEntryOther"/> offers the
    /// runways beside an assigned one.
    /// </summary>
    private static MenuCatalogEntry PatternEntry(string id, Func<IMenuAircraft?, MenuContext, bool> isApplicable)
    {
        (string label, string command) = PatternEntrySpec(id);
        return new(id, label, MenuFlightRules.Both, isApplicable, (ac, context, host) => BuildPatternEntry(label, command, ac, context, host));
    }

    private static MenuItem BuildPatternEntry(string label, string command, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is { AssignedRunway.Length: > 0 })
        {
            string runway = aircraft.AssignedRunway;
            return BuildSend($"{label} {RunwayIdentifier.ToDisplayDesignator(runway)}", $"{command} {runway}", context, host);
        }

        IReadOnlyList<string> runways = PatternEntryRunways(aircraft);
        if (runways.Count > 0)
        {
            return BuildPatternRunwayList($"{label}{Ellipsis}", command, runways, context, host);
        }

        return BuildInput(
            $"{label}{Ellipsis}",
            "Runway (optional)",
            BlankInput.Submits,
            input => string.IsNullOrWhiteSpace(input) ? command : $"{command} {input}",
            context,
            host
        );
    }

    /// <summary>
    /// A pattern entry's companion, which shares its id: a picker over <see cref="PatternEntryRunways"/> labelled
    /// "(other)", offered beside an assigned runway; null without an assigned runway or without runways.
    /// </summary>
    internal static MenuItem? BuildPatternEntryOther(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is not { AssignedRunway.Length: > 0 })
        {
            return null;
        }

        IReadOnlyList<string> runways = PatternEntryRunways(aircraft);
        if (runways.Count == 0)
        {
            return null;
        }

        (string label, string command) = PatternEntrySpec(id);
        return BuildPatternRunwayList($"{label} (other){Ellipsis}", command, runways, context, host);
    }

    /// <summary>The runways a pattern entry offers: the destination's, else the departure airport's; none without either.</summary>
    private static IReadOnlyList<string> PatternEntryRunways(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return [];
        }

        string airport = !string.IsNullOrEmpty(aircraft.Destination) ? aircraft.Destination : aircraft.Departure;
        return !string.IsNullOrEmpty(airport) ? RunwayDesignators.ForAirport(airport) : [];
    }

    /// <summary>A list picker over <paramref name="runways"/>, the first highlighted, that sends <paramref name="command"/> for the pick.</summary>
    private static MenuItem BuildPatternRunwayList(string label, string command, IReadOnlyList<string> runways, MenuContext context, IMenuHost host)
    {
        List<object> items = [.. runways];
        return BuildList(label, items, items[0], picked => Send($"{command} {picked}", context, host), host);
    }

    private static MenuCatalogEntry InputLeaf(string id, string label, string placeholder, BlankInput blank, Func<string, string> format) =>
        new(id, label, MenuFlightRules.Both, Always, (_, context, host) => BuildInput(label, placeholder, blank, format, context, host));

    /// <summary>
    /// An entry whose item the host builds for a surface of its own rather than from a command text: the warp popup,
    /// the flight-plan editor, the display toggles and measure item that read and drive the surface's own state, and
    /// the ground submenus whose choices the host answers (hold short, follow, give way, push back to, preset taxi, taxi to
    /// runway).
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

    internal static MenuItem BuildInput(
        string label,
        string placeholder,
        BlankInput blank,
        Func<string, string> format,
        MenuContext context,
        IMenuHost host
    )
    {
        var item = new MenuItem { Header = label, Tag = new MenuPickerDescriptor(MenuPickerDescriptor.Input, []) };
        item.Click += (_, _) =>
            host.ShowInputPopup(placeholder, blank, "", 0, input => host.SendAsync(context.Callsign, format(input), context.Initials));
        return item;
    }

    /// <summary>
    /// Warp is offered only for a controllable aircraft: the warp goes through the command path, which auto-assumes an
    /// airborne shadow but refuses an unassumable surface one.
    /// </summary>
    private static bool CanWarp(IMenuAircraft? aircraft) => AircraftCommandApplicability.IsControllable(aircraft);

    /// <summary>
    /// The Warp item, or null when the aircraft is not controllable (<see cref="CanWarp"/>). It seeds the host's warp
    /// popup with the aircraft's heading, altitude and indicated airspeed and sends
    /// <c>WARP {frd} {heading} {altitude} {speed}</c> for the values the popup submits. A WARP needs a real heading, so
    /// a zero or negative one is clamped to 360.
    /// </summary>
    private static MenuItem? BuildWarp(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!CanWarp(aircraft))
        {
            return null;
        }

        var item = new MenuItem { Header = label };
        item.Click += (_, _) => ShowWarp(aircraft, "", context, host);
        return item;
    }

    /// <summary>
    /// Opens the host's warp popup at <paramref name="frd"/>, seeded with the aircraft's heading (a zero or negative one
    /// clamped to 360), altitude and indicated airspeed, and sends <c>WARP {frd} {heading} {altitude} {speed}</c> for the
    /// values the popup submits.
    /// </summary>
    private static void ShowWarp(IMenuAircraft? aircraft, string frd, MenuContext context, IMenuHost host)
    {
        int heading = aircraft is not null ? (int)Math.Round(aircraft.HeadingDegrees) : 0;
        if (heading <= 0)
        {
            heading = 360;
        }

        int altitude = aircraft is not null ? (int)Math.Round(aircraft.AltitudeFeet) : 0;
        int speed = aircraft is not null ? (int)Math.Round(aircraft.IndicatedAirspeedKnots) : 0;
        host.ShowWarpPopup(
            context.Callsign,
            frd,
            heading,
            altitude,
            speed,
            (position, h, a, s) => host.SendAsync(context.Callsign, $"WARP {position} {h} {a} {s}", context.Initials)
        );
    }

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

    /// <summary>
    /// The host's Taxi here choices for the clicked node as one item: its one choice (a route's item, a "Taxi here"
    /// submenu over several, or the disabled "No route found" row), or a "Taxi here" submenu should it answer more.
    /// </summary>
    private static MenuItem? BuildPointTaxiHere(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        if (point.Node is not { } node)
        {
            return null;
        }

        IReadOnlyList<MenuCommandChoice> choices = host.GetTaxiChoices(context.Callsign, node, point.RunwayEnd);
        if (choices.Count != 1)
        {
            return BuildChoiceSubmenu("Taxi here", choices, context, host);
        }

        return BuildChoice(choices[0], context, host) as MenuItem
            ?? throw new InvalidOperationException(
                $"IMenuHost.GetTaxiChoices answered a lone separator for {context.Callsign} at node {node.Id}; it answers a route, a "
                    + "\"Taxi here\" submenu or a \"No route found\" row."
            );
    }

    /// <summary>
    /// Taxi to runway: for each runway under the click, one <c>Taxi to {end}</c> submenu per end in the runway's order,
    /// over the host's hold-short targets for that end, each a submenu of the Taxi here choices for its node routed to
    /// that end, for <see cref="SharedMenuGroups.AddTaxiToRunwayEnds"/> to add in the point menu. An end with no target
    /// that has a choice is left out.
    /// </summary>
    internal static IReadOnlyList<MenuItem> BuildTaxiToRunwayEnds(MenuPoint point, MenuContext context, IMenuHost host)
    {
        List<MenuItem> ends = [];
        foreach (string runwayName in point.SurfaceRunways)
        {
            var ids = RunwayIdentifier.Parse(runwayName);
            foreach (string end in new[] { ids.End1, ids.End2 }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (BuildTaxiToRunwayEnd(runwayName, end, point, context, host) is { } endItem)
                {
                    ends.Add(endItem);
                }
            }
        }

        return ends;
    }

    /// <summary>
    /// The <c>Taxi to {end}</c> submenu: one submenu per host target for <paramref name="end"/> of
    /// <paramref name="runwayName"/>, over <see cref="IMenuHost.GetTaxiChoices"/> for its node and that end, built as Taxi
    /// here builds them; a target with no choice is left out, and null when none is left.
    /// </summary>
    private static MenuItem? BuildTaxiToRunwayEnd(string runwayName, string end, MenuPoint point, MenuContext context, IMenuHost host)
    {
        var endItem = new MenuItem { Header = $"Taxi to {RunwayIdentifier.ToDisplayDesignator(end)}" };
        foreach (RunwayHoldShortTarget target in host.GetRunwayHoldShortTargets(context.Callsign, runwayName, end, point.Position))
        {
            if (BuildChoiceSubmenu(target.Label, host.GetTaxiChoices(context.Callsign, target.Node, end), context, host) is { } targetItem)
            {
                endItem.Items.Add(targetItem);
            }
        }

        return (endItem.Items.Count > 0) ? endItem : null;
    }

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
            host.ShowInputPopup(
                "Taxi command",
                BlankInput.Closes,
                seed.Text,
                seed.Caret,
                text => host.SendAsync(context.Callsign, text.Trim(), context.Initials)
            );
        return item;
    }

    /// <summary>
    /// Warp here: to a clicked taxi node, <c>WARPG #node</c>; to a map point, the warp popup at its fix-radial-distance,
    /// seeded as Warp… seeds it; null at a map point the host cannot name.
    /// </summary>
    private static MenuItem? BuildPointWarp(IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        if ((point.Node ?? point.WarpNode) is { } node)
        {
            return BuildSend("Warp here", $"WARPG #{node.Id}", context, host);
        }

        if (host.DescribePoint(point.Position) is not { } frd)
        {
            return null;
        }

        var item = new MenuItem { Header = $"Warp here ({frd})" };
        item.Click += (_, _) => ShowWarp(aircraft, frd, context, host);
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
    /// An altitude as the Maintain picker and the Altitude header show it: feet with thousands commas below 18,000 ft
    /// (<c>17,500</c>), a flight level from it (<c>FL180</c>).
    /// </summary>
    internal static string FormatAltitude(int altitude) =>
        (altitude >= 18000) ? $"FL{altitude / 100}" : altitude.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The aircraft's route fixes, which the navigation pickers offer first; none without an aircraft.</summary>
    private static Func<IMenuAircraft?, IReadOnlyList<string>> RouteFixes => ac => ac?.RouteFixNames() ?? [];

    /// <summary>
    /// The Direct-to picker's fix list: the aircraft's route from the fix it is navigating to on. Without a navigating-to
    /// fix the list is the whole route (<see cref="IMenuAircraft.RouteFixNames"/>); otherwise it is the navigation route
    /// from that fix on, then the filed route's fixes after the last of them, then the destination — each fix once, in
    /// order, ignoring case, and with the departure airport never listed.
    /// </summary>
    private static Func<IMenuAircraft?, IReadOnlyList<string>> DirectToFixes => ac => ac is null ? [] : DirectToFixNames(ac);

    /// <summary>The fix names <see cref="DirectToFixes"/> offers <paramref name="aircraft"/>, in list order and without duplicates.</summary>
    private static IReadOnlyList<string> DirectToFixNames(IMenuAircraft aircraft)
    {
        if (string.IsNullOrEmpty(aircraft.NavigatingTo))
        {
            return aircraft.RouteFixNames();
        }

        var fixes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string fix)
        {
            if (!string.IsNullOrWhiteSpace(fix) && seen.Add(fix))
            {
                fixes.Add(fix);
            }
        }

        AddNavRouteFromNext(aircraft.NavigationRoute, aircraft.NavigatingTo, TryAdd);
        AddFiledRouteAfter(aircraft, aircraft.NavigationRoute, TryAdd);
        TryAdd(aircraft.Destination);
        return fixes;
    }

    /// <summary>
    /// Adds the navigation route from the first entry equal to <paramref name="navigatingTo"/> on, or that fix alone
    /// when the route does not carry it.
    /// </summary>
    private static void AddNavRouteFromNext(IReadOnlyList<string> navRoute, string navigatingTo, Action<string> tryAdd)
    {
        int start = IndexOfFix(navRoute, navigatingTo);
        if (start < 0)
        {
            tryAdd(navigatingTo);
            return;
        }

        for (int i = start; i < navRoute.Count; i++)
        {
            tryAdd(navRoute[i]);
        }
    }

    /// <summary>
    /// Adds the filed route's fixes after the last of the aircraft's navigation route — or after the fix it is
    /// navigating to when the route is empty — or nothing when the filed route does not carry that fix.
    /// </summary>
    private static void AddFiledRouteAfter(IMenuAircraft aircraft, IReadOnlyList<string> navRoute, Action<string> tryAdd)
    {
        if (string.IsNullOrWhiteSpace(aircraft.Route))
        {
            return;
        }

        string last = navRoute.Count > 0 ? navRoute[^1] : aircraft.NavigatingTo;
        IReadOnlyList<string> expanded = NavigationDatabase.Instance.ExpandRoute(aircraft.Route);
        int lastIndex = LastIndexOfFix(expanded, last);
        if (lastIndex < 0)
        {
            return;
        }

        for (int i = lastIndex + 1; i < expanded.Count; i++)
        {
            tryAdd(expanded[i]);
        }
    }

    /// <summary>The index of the first entry of <paramref name="fixes"/> equal to <paramref name="fix"/> ignoring case, or -1.</summary>
    private static int IndexOfFix(IReadOnlyList<string> fixes, string fix)
    {
        for (int i = 0; i < fixes.Count; i++)
        {
            if (string.Equals(fixes[i], fix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index of the last entry of <paramref name="fixes"/> equal to <paramref name="fix"/> ignoring case, or -1.</summary>
    private static int LastIndexOfFix(IReadOnlyList<string> fixes, string fix)
    {
        for (int i = fixes.Count - 1; i >= 0; i--)
        {
            if (string.Equals(fixes[i], fix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>No route fixes: the hold pickers offer every fix alike.</summary>
    private static Func<IMenuAircraft?, IReadOnlyList<string>> NoRouteFixes => _ => [];

    /// <summary>Whether the aircraft is navigating to a fix, which an appended direct-to follows.</summary>
    private static Func<IMenuAircraft?, MenuContext, bool> IsNavigatingToFix => (ac, _) => !string.IsNullOrEmpty(ac?.NavigatingTo);

    private static Task Send(string command, MenuContext context, IMenuHost host) => host.SendAsync(context.Callsign, command, context.Initials);

    /// <summary>
    /// A list picker: the host's list popup offers <paramref name="items"/> with <paramref name="selected"/>
    /// highlighted, and the pick reaches <paramref name="onPick"/>. The item's descriptor carries the texts the popup lists.
    /// </summary>
    private static MenuItem BuildList(string label, IReadOnlyList<object> items, object? selected, Func<object, Task> onPick, IMenuHost host)
    {
        var item = new MenuItem { Header = label, Tag = new MenuPickerDescriptor(MenuPickerDescriptor.List, PickerTexts(items)) };
        item.Click += (_, _) => host.ShowListPopup(items, selected, onPick);
        return item;
    }

    /// <summary>
    /// A type-to-filter picker over <paramref name="sortedNames"/>: the popup lists <paramref name="priorityItems"/>
    /// until the controller types, so those are the texts the item's descriptor carries.
    /// </summary>
    private static MenuItem BuildFilteredList(
        string label,
        string[] sortedNames,
        IReadOnlyList<object>? priorityItems,
        Func<string, Task> onPick,
        IMenuHost host
    )
    {
        var item = new MenuItem
        {
            Header = label,
            Tag = new MenuPickerDescriptor(MenuPickerDescriptor.FilteredList, PickerTexts(priorityItems ?? [])),
        };
        item.Click += (_, _) => host.ShowFilteredListPopup(sortedNames, priorityItems, onPick);
        return item;
    }

    /// <summary>The display texts of a popup's values, as the popup itself shows them.</summary>
    private static List<string> PickerTexts(IReadOnlyList<object> values) => [.. values.Select(value => value.ToString() ?? "")];

    /// <summary>A heading picker that sends <paramref name="command"/> with the picked heading, highlighting the aircraft's own.</summary>
    private static MenuCatalogEntry HeadingList(string id, string label, string command) =>
        new(
            id,
            label,
            MenuFlightRules.Both,
            Always,
            (ac, context, host) => BuildList(label, HeadingValues(), HeadingSeed(ac), picked => Send($"{command} {picked}", context, host), host)
        );

    /// <summary>A relative-turn picker that sends <paramref name="command"/> with the picked number of degrees, highlighting 30.</summary>
    private static MenuCatalogEntry RelativeTurnList(string id, string label, string command) =>
        new(
            id,
            label,
            MenuFlightRules.Both,
            Always,
            (_, context, host) => BuildList(label, [5, 10, 15, 20, 30, 45, 60, 90], 30, picked => Send($"{command} {picked}", context, host), host)
        );

    private static List<object> HeadingValues()
    {
        var items = new List<object>(360 / HeadingStep);
        for (int heading = HeadingStep; heading <= 360; heading += HeadingStep)
        {
            items.Add(heading);
        }

        return items;
    }

    /// <summary>
    /// The heading the heading pickers highlight: the aircraft's true heading rounded to the nearest five, so it lands
    /// on a listed value, with north (and no aircraft) as 360.
    /// </summary>
    private static int HeadingSeed(IMenuAircraft? aircraft)
    {
        int heading = aircraft is not null ? (int)(Math.Round(aircraft.HeadingDegrees / HeadingStep) * HeadingStep) : 360;
        return heading <= 0 ? 360 : heading;
    }

    /// <summary>The Report traffic in sight… free-text box's placeholder, for a target callsign or none.</summary>
    private const string TrafficInSightPlaceholder = "Target callsign (optional)";

    /// <summary>
    /// The Report traffic in sight… picker: with airborne traffic near the aircraft (<see cref="IMenuHost.GetNearbyTraffic"/>),
    /// a titled list of it (<see cref="TrafficInSightList"/>), each row sending <c>RTIS {callsign}</c>, then Any traffic
    /// sending a bare <c>RTIS</c> and Other callsign… opening the free-text box; with none, the free-text box alone. The
    /// box sends <c>RTIS {input}</c>, or a bare <c>RTIS</c> when left blank.
    /// </summary>
    private static MenuItem BuildReportTrafficInSight(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        IReadOnlyList<MenuTrafficRow> traffic = host.GetNearbyTraffic(context.Callsign);
        if (traffic.Count == 0)
        {
            return BuildInput(label, TrafficInSightPlaceholder, BlankInput.Submits, FormatTrafficInSight, context, host);
        }

        MenuRichList list = TrafficInSightList(context.Callsign, traffic);
        var item = new MenuItem
        {
            Header = label,
            Tag = new MenuPickerDescriptor(MenuPickerDescriptor.RichList, [.. list.Rows.Select(row => row.Label)]),
        };
        item.Click += (_, _) => host.ShowRichListPopup(list, row => PickTrafficInSight(row, context, host));
        return item;
    }

    /// <summary>Sends a traffic row's or Any traffic's command, or opens the free-text box for Other callsign….</summary>
    private static void PickTrafficInSight(MenuRichRow row, MenuContext context, IMenuHost host)
    {
        if (row.Kind == MenuRichRowKind.Prompt)
        {
            host.ShowInputPopup(TrafficInSightPlaceholder, BlankInput.Submits, "", 0, input => Send(FormatTrafficInSight(input), context, host));
        }
        else if (row.Command is { } command)
        {
            _ = Send(command, context, host);
        }
    }

    /// <summary>
    /// The Nearest traffic list for <paramref name="callsign"/>: one row per aircraft of <paramref name="traffic"/> in its
    /// order, <c>{callsign} · {type}</c> with its clock position, whole nm and altitude difference as columns, then a rule,
    /// <c>Any traffic (no target)</c> and <c>Other callsign…</c>. Opens on the first row.
    /// </summary>
    private static MenuRichList TrafficInSightList(string callsign, IReadOnlyList<MenuTrafficRow> traffic)
    {
        List<MenuRichRow> rows =
        [
            .. traffic.Select(row => new MenuRichRow(
                "",
                string.IsNullOrWhiteSpace(row.AircraftType) ? row.Callsign : $"{row.Callsign} · {row.AircraftType}",
                "",
                MenuRichRowKind.Traffic,
                $"RTIS {row.Callsign}",
                null,
                [
                    $"{row.ClockPosition} o'clock",
                    $"{RelativeGeometry.WholeNm(row.DistanceNm)} nm",
                    RelativeGeometry.AltitudeDeltaColumn(row.AltitudeDeltaFeet),
                ]
            )),
            new MenuRichRow("", "---", "", MenuRichRowKind.Separator, null, null, []),
            new MenuRichRow("", "Any traffic (no target)", "RTIS", MenuRichRowKind.Action, "RTIS", null, []),
            new MenuRichRow("", "Other callsign…", "", MenuRichRowKind.Prompt, null, null, []),
        ];
        return new MenuRichList("Nearest traffic", $"{callsign} · report traffic in sight", rows, 0);
    }

    /// <summary>The highest altitude the Maintain picker lists, and its cap for a type without a profile.</summary>
    private const int MaintainCapFeet = 60000;

    /// <summary>
    /// The Maintain picker: a titled list of every altitude from the destination's field elevation up to the type's
    /// ceiling, highest first (<see cref="MaintainList"/>), opening on the assigned row, else on the row nearest the
    /// aircraft's altitude. The item's descriptor carries every row's label. Null when no altitude is listed.
    /// </summary>
    private static MenuItem? BuildMaintainAltitude(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<int> altitudes = AltitudeValues(host.GetFieldElevation(aircraft?.Destination), AltitudeCeiling(aircraft));
        if (altitudes.Count == 0)
        {
            return null;
        }

        (string Sector, int FloorFtMsl)? mva = aircraft is not null ? host.GetMva(aircraft.Position) : null;
        MenuRichList list = MaintainList(context.Callsign, altitudes, aircraft?.AltitudeFeet ?? 0, aircraft?.AssignedAltitude, mva);
        var item = new MenuItem
        {
            Header = label,
            Tag = new MenuPickerDescriptor(MenuPickerDescriptor.RichList, [.. list.Rows.Select(row => row.Label)]),
        };
        item.Click += (_, _) =>
            host.ShowRichListPopup(
                list,
                row =>
                {
                    if (row.Command is { } command)
                    {
                        _ = Send(command, context, host);
                    }
                }
            );
        return item;
    }

    /// <summary>
    /// The type's service ceiling (<see cref="AircraftPerformance.Ceiling"/>) capped at 60,000 ft; 60,000 ft for a type
    /// without a profile.
    /// </summary>
    private static int AltitudeCeiling(IMenuAircraft? aircraft)
    {
        string type = aircraft?.DisplayAircraftType ?? "";
        double? ceiling = (type.Length > 0) ? AircraftPerformance.Ceiling(type) : null;
        return (ceiling is { } feet) ? (int)Math.Min(feet, MaintainCapFeet) : MaintainCapFeet;
    }

    /// <summary>
    /// The Maintain picker's rows for an aircraft at <paramref name="current"/> ft: one per altitude, marked ● for the row
    /// nearest the current altitude, ◆ on the assigned altitude (its own row when it is off the steps or above the
    /// ceiling), ↑ above the current altitude and ↓ below it, and, when <paramref name="mva"/> is known, the MVA line
    /// between the lowest row at or above its floor and the highest row below it, with every row under the floor greyed.
    /// Titled with <paramref name="callsign"/>; opens on the assigned row, else the ● row.
    /// </summary>
    private static MenuRichList MaintainList(
        string callsign,
        List<int> altitudes,
        double current,
        double? assigned,
        (string Sector, int FloorFtMsl)? mva
    )
    {
        int? assignedIndex = InsertAssignment(altitudes, assigned);
        int nowIndex = NearestIndex(altitudes, current);
        var rows = new List<MenuRichRow>(altitudes.Count + 1);
        for (int i = 0; i < altitudes.Count; i++)
        {
            MenuRichRowKind kind = MaintainKind(i == nowIndex, i == assignedIndex, altitudes[i], current);
            rows.Add(MaintainRow(altitudes[i], kind, MaintainCommand(altitudes[i], kind, current, assigned), mva?.FloorFtMsl));
        }

        int selected = assignedIndex ?? nowIndex;
        if (mva is { } sector)
        {
            selected = InsertMvaLine(rows, altitudes, sector, selected);
        }

        return new MenuRichList($"{callsign} · Maintain", MaintainSubtitle(current, assigned), rows, selected);
    }

    /// <summary>
    /// The index of the assigned altitude among <paramref name="altitudes"/> (highest first), added in its sorted place
    /// when it is off the steps or above the ceiling; null without an assignment.
    /// </summary>
    private static int? InsertAssignment(List<int> altitudes, double? assigned)
    {
        if (assigned is not { } feet)
        {
            return null;
        }

        int altitude = (int)Math.Round(feet);
        int index = altitudes.FindIndex(listed => listed <= altitude);
        if (index < 0)
        {
            altitudes.Add(altitude);
            return altitudes.Count - 1;
        }

        if (altitudes[index] != altitude)
        {
            altitudes.Insert(index, altitude);
        }

        return index;
    }

    /// <summary>
    /// Inserts the MVA line before the first row under <paramref name="sector"/>'s floor (at the end when none is) and
    /// returns <paramref name="selected"/> moved past it when it was at or after the line.
    /// </summary>
    private static int InsertMvaLine(List<MenuRichRow> rows, List<int> altitudes, (string Sector, int FloorFtMsl) sector, int selected)
    {
        int lineIndex = altitudes.FindIndex(altitude => altitude < sector.FloorFtMsl);
        lineIndex = (lineIndex < 0) ? altitudes.Count : lineIndex;
        string floor = sector.FloorFtMsl.ToString("N0", CultureInfo.InvariantCulture);
        rows.Insert(lineIndex, new MenuRichRow("", $"MVA {floor} here (sector {sector.Sector})", "", MenuRichRowKind.MvaLine, null, null, []));
        return (selected >= lineIndex) ? selected + 1 : selected;
    }

    /// <summary>
    /// <c>now {current} · assigned {assigned} · type to jump</c>, the current altitude to the nearest 100 ft; without the
    /// assignment when there is none.
    /// </summary>
    private static string MaintainSubtitle(double current, double? assigned)
    {
        string now = $"now {FormatAltitude((int)(Math.Round(current / 100, MidpointRounding.AwayFromZero) * 100))}";
        return (assigned is { } feet) ? $"{now} · assigned {FormatAltitude((int)Math.Round(feet))} · type to jump" : $"{now} · type to jump";
    }

    /// <summary>The index of the altitude nearest <paramref name="target"/>; of two equally near, the higher (listed first).</summary>
    private static int NearestIndex(List<int> altitudes, double target)
    {
        int best = 0;
        for (int i = 1; i < altitudes.Count; i++)
        {
            if (Math.Abs(altitudes[i] - target) < Math.Abs(altitudes[best] - target))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// A row's mark before the MVA greys it: ● for the current row, ◆ for the assigned one, else ↑ above the current
    /// altitude and ↓ below.
    /// </summary>
    private static MenuRichRowKind MaintainKind(bool isNow, bool isAssigned, int altitude, double current) =>
        (isNow, isAssigned) switch
        {
            (true, true) => MenuRichRowKind.NowAssigned,
            (true, false) => MenuRichRowKind.Now,
            (false, true) => MenuRichRowKind.Assigned,
            _ => (altitude > current) ? MenuRichRowKind.Climb : MenuRichRowKind.Descend,
        };

    /// <summary>
    /// The command a row sends: the ● row climbs unless the assignment is below the current altitude; every other row
    /// climbs above the current altitude and descends otherwise. The value is bare feet (<c>CM 35000</c>).
    /// </summary>
    private static string MaintainCommand(int altitude, MenuRichRowKind kind, double current, double? assigned)
    {
        bool climb = (kind is MenuRichRowKind.Now or MenuRichRowKind.NowAssigned) ? !(assigned < current) : (altitude > current);
        return climb ? $"CM {altitude}" : $"DM {altitude}";
    }

    /// <summary>
    /// One altitude row with its glyph and hint: the ● and ◆ rows name their mark (<c>now</c>, <c>assigned</c>,
    /// <c>now · assigned</c>), the ↑ and ↓ rows their command. Every row under <paramref name="mvaFloor"/> is greyed
    /// (<see cref="MenuRichRowKind.BelowMva"/>), keeps its glyph, ends its hint with <c>below MVA</c> in place of the
    /// command, and still sends <paramref name="command"/>.
    /// </summary>
    private static MenuRichRow MaintainRow(int altitude, MenuRichRowKind kind, string command, int? mvaFloor)
    {
        (string glyph, string mark) = MaintainMark(kind);
        string label = FormatAltitude(altitude);
        if (altitude < mvaFloor)
        {
            string hint = (mark.Length > 0) ? $"{mark} · below MVA" : "below MVA";
            return new MenuRichRow(glyph, label, hint, MenuRichRowKind.BelowMva, command, altitude, []);
        }

        return new MenuRichRow(glyph, label, (mark.Length > 0) ? mark : command, kind, command, altitude, []);
    }

    /// <summary>A row's glyph and, for the ● and ◆ rows, the mark its hint names; the ↑ and ↓ rows have none.</summary>
    private static (string Glyph, string Mark) MaintainMark(MenuRichRowKind kind) =>
        kind switch
        {
            MenuRichRowKind.Now => ("●", "now"),
            MenuRichRowKind.NowAssigned => ("●", "now · assigned"),
            MenuRichRowKind.Assigned => ("◆", "assigned"),
            MenuRichRowKind.Climb => ("↑", ""),
            _ => ("↓", ""),
        };

    /// <summary>
    /// Every 100 ft from the field up to 5,000 ft above it, then every 500 ft, up to <paramref name="ceiling"/>; highest first.
    /// </summary>
    private static List<int> AltitudeValues(double fieldElevation, int ceiling)
    {
        var items = new List<int>();
        int lowThreshold = (int)(fieldElevation + 5000);

        int roundedLow = (int)(Math.Ceiling(fieldElevation / 100.0) * 100);
        if (roundedLow < 100)
        {
            roundedLow = 100;
        }

        for (int altitude = roundedLow; (altitude < lowThreshold) && (altitude <= ceiling); altitude += 100)
        {
            items.Add(altitude);
        }

        int start500 = (int)(Math.Ceiling(lowThreshold / 500.0) * 500);
        for (int altitude = start500; altitude <= ceiling; altitude += 500)
        {
            items.Add(altitude);
        }

        items.Reverse();
        return items;
    }

    /// <summary>The altitude at and above which the picker lists Mach rows (7110.65 5-7-1.g).</summary>
    private const double MachRowFloorFeet = 24000;

    /// <summary>The owner's clamp on the picker's Mach rows: 0.60 at the lowest and 0.92 at the highest.</summary>
    private const double MachRowMinimum = 0.60;

    private const double MachRowMaximum = 0.92;

    /// <summary>The picker's first row, above every speed: normal speed.</summary>
    private const string ResumeNormalSpeedRow = "Resume normal speed";

    /// <summary>The picker's second row, above every speed: the final approach speed the filed type flies.</summary>
    private const string FinalApproachSpeedRow = "Final approach speed";

    /// <summary>The Assign speed picker, highlighting the assigned speed rounded to ten knots, or the middle knots row.</summary>
    private static MenuItem BuildAssignSpeed(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<object> speeds = SpeedValues(aircraft);
        double? assigned = aircraft?.AssignedSpeed;
        object? seed = assigned is > 0 ? (int)(Math.Round(assigned.Value / 10.0) * 10) : MiddleKnots(speeds);
        return BuildList(label, speeds, seed, picked => Send(SpeedCommand(picked), context, host), host);
    }

    /// <summary>
    /// The middle knots row, which seeds the picker when no speed is assigned, or null when the caps leave no knots rows,
    /// which the host reads as nothing to highlight. The leading and Mach rows are not counted.
    /// </summary>
    private static object? MiddleKnots(List<object> speeds)
    {
        List<object> knots = [.. speeds.Where(speed => speed is int)];
        return knots.Count > 0 ? knots[knots.Count / 2] : null;
    }

    /// <summary>The command a picked row sends: its own for a labelled row (<c>MACH .78</c>, <c>RNS</c>, <c>RFAS</c>), else <c>SPD 250</c>.</summary>
    private static string SpeedCommand(object picked) => picked is MenuLabeledCommand row ? row.Command : $"SPD {picked}";

    /// <summary>
    /// The rows the Assign speed picker lists. Every list leads with Resume normal speed (<c>RNS</c>) and Final approach
    /// speed (<c>RFAS</c>). A filed type then adds every tenth knot from its approach speed rounded up through its knots
    /// ceiling — the highest of its cruise, climb and descent speeds at the aircraft's altitude and the profile's
    /// FL150/FL240 climb and FL100 descent knots, plus 20 kt (10 for a piston), floored to ten, capped at 250 below
    /// 10,000 ft unless the type waives the limit — then, at or above FL240, the Mach rows its profile spans. Without a
    /// filed type the knots stay 150-350.
    /// </summary>
    private static List<object> SpeedValues(IMenuAircraft? aircraft)
    {
        var speeds = new List<object>
        {
            new MenuLabeledCommand(ResumeNormalSpeedRow, "RNS"),
            new MenuLabeledCommand(FinalApproachSpeedRowLabel(aircraft), "RFAS"),
        };
        if (aircraft is null || string.IsNullOrEmpty(aircraft.FiledAircraftType))
        {
            speeds.AddRange(SpeedRange(150, 350));
            return speeds;
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
        speeds.AddRange(SpeedRange(floor, ceiling));
        foreach (double mach in machs)
        {
            speeds.Add(MachRow(mach));
        }

        return speeds;
    }

    /// <summary>The final-approach-speed row's label: the filed type's approach speed rounded to the knot, else the bare label.</summary>
    private static string FinalApproachSpeedRowLabel(IMenuAircraft? aircraft)
    {
        if (aircraft is null || string.IsNullOrEmpty(aircraft.FiledAircraftType))
        {
            return FinalApproachSpeedRow;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.FiledAircraftType);
        int knots = (int)Math.Round(AircraftPerformance.ApproachSpeed(aircraft.FiledAircraftType, category), MidpointRounding.AwayFromZero);
        return $"{FinalApproachSpeedRow} ({knots})";
    }

    /// <summary>A Mach row, listed <c>M.78</c> and sent as <c>MACH .78</c>.</summary>
    private static MenuLabeledCommand MachRow(double mach)
    {
        int hundredths = (int)Math.Round(mach * 100, MidpointRounding.AwayFromZero);
        return new MenuLabeledCommand($"M.{hundredths:00}", $"MACH .{hundredths:00}");
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

    /// <summary>
    /// A row of the Assign speed picker that carries its own command text: a Mach row (<c>M.78</c>, sent <c>MACH .78</c>)
    /// or one of the two fixed rows every list leads with (<c>RNS</c>, <c>RFAS</c>). Its own type so the pick tells a
    /// labelled row from a knots row, which is a bare int.
    /// </summary>
    private sealed record MenuLabeledCommand(string Label, string Command)
    {
        public override string ToString() => Label;
    }

    private static List<object> SpeedRange(int min, int max)
    {
        var items = new List<object>(((max - min) / 10) + 1);
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
    public static string FinalApproachSpeedLabel(string label, IMenuAircraft? aircraft)
    {
        if (aircraft is null || string.IsNullOrEmpty(aircraft.FiledAircraftType))
        {
            return label;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.FiledAircraftType);
        double fas = AircraftPerformance.ApproachSpeed(aircraft.FiledAircraftType, category);
        return fas > 0 ? $"{label} - {fas:F0} kt" : label;
    }

    /// <summary>
    /// A fix picker that sends <paramref name="command"/> with the picked fix. <paramref name="label"/> ends in an
    /// ellipsis, which the plain route-fix list drops; a label without one throws.
    /// </summary>
    private static MenuCatalogEntry FixPicker(
        string id,
        string label,
        string command,
        Func<IMenuAircraft?, MenuContext, bool> isApplicable,
        Func<IMenuAircraft?, IReadOnlyList<string>> routeFixes
    )
    {
        if (!label.EndsWith(Ellipsis, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Fix picker label '{label}' must end in '{Ellipsis}'", nameof(label));
        }

        return new(
            id,
            label,
            MenuFlightRules.Both,
            isApplicable,
            (ac, context, host) => BuildFixPicker(label, command, routeFixes(ac), context, host)
        );
    }

    /// <summary>
    /// The fix picker's form, by the data present when the menu is built: the filtered list over every fix while the
    /// host has fix names (the route fixes listed first), else a plain list of the route fixes when there are any,
    /// else free text.
    /// </summary>
    private static MenuItem BuildFixPicker(string label, string command, IReadOnlyList<string> routeFixes, MenuContext context, IMenuHost host)
    {
        List<object> routeItems = [.. routeFixes];
        if (host.FixNames is { } fixNames)
        {
            return BuildFilteredList(label, fixNames, routeItems.Count > 0 ? routeItems : null, fix => Send($"{command} {fix}", context, host), host);
        }

        if (routeItems.Count > 0)
        {
            return BuildList(label[..^Ellipsis.Length], routeItems, routeItems[0], fix => Send($"{command} {fix}", context, host), host);
        }

        return BuildInput(label, FixNamePlaceholder, BlankInput.Closes, input => $"{command} {input}", context, host);
    }

    /// <summary>The Draw taxi route item, which puts the host into drawing a route for the aircraft.</summary>
    private static MenuItem BuildDrawRoute(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.EnterDrawRoute(context.Callsign);
        return item;
    }

    /// <summary>An approach picker entry (<see cref="ApproachSpec"/>), built by <see cref="ApproachPickerBuilder.Build"/>.</summary>
    private static MenuCatalogEntry ApproachPicker(string id)
    {
        ApproachPickerSpec spec = ApproachSpec(id);
        return new(id, spec.Label, MenuFlightRules.Both, Always, (ac, context, host) => ApproachPickerBuilder.Build(spec, ac, context, host));
    }

    /// <summary>The approach picker of each approach entry: its label, its command and its default-approach leaf label.</summary>
    private static ApproachPickerSpec ApproachSpec(string id) =>
        id switch
        {
            MenuIds.ApproachCleared => new("Cleared approach", "CAPP", name => $"Cleared {name}"),
            MenuIds.ApproachJoin => new("Join approach", "JAPP", name => $"Join {name}"),
            MenuIds.ApproachClearedStraightIn => new("Cleared straight-in", "CAPPSI", name => $"Cleared straight-in {name}"),
            MenuIds.ApproachJoinStraightIn => new("Join straight-in", "JAPPSI", name => $"Join straight-in {name}"),
            MenuIds.ApproachClearedForce => new("Cleared approach (force)", "CAPPF", name => $"Cleared {name} (force)"),
            MenuIds.ApproachJoinForce => new("Join approach (force)", "JAPPF", name => $"Join {name} (force)"),
            MenuIds.ApproachJoinFinalCourse => new("Join final approach course", "JFAC", name => $"Join final course {name}"),
            MenuIds.ApproachExpect => new("Expect approach", "EAPP", name => $"Expect {name}"),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Not an approach picker entry."),
        };

    /// <summary>
    /// An approach entry's companion, which shares its id: the grouped picker labelled "(other)", offered beside a
    /// default approach; null without a default (<see cref="ApproachPickerBuilder.BuildOther"/>).
    /// </summary>
    internal static MenuItem? BuildApproachOther(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        ApproachPickerBuilder.BuildOther(ApproachSpec(id), aircraft, context, host);

    /// <summary>The destination's published approaches; none without an aircraft or a destination.</summary>
    internal static IReadOnlyList<CifpApproachProcedure> DestinationApproaches(IMenuAircraft? aircraft) =>
        aircraft is { Destination.Length: > 0 } ? NavigationDatabase.Instance.GetApproaches(aircraft.Destination) : [];

    /// <summary>The destination's runway designators, which the visual-approach picker lists; none without a destination.</summary>
    private static IReadOnlyList<string> DestinationRunways(IMenuAircraft? aircraft) =>
        aircraft is { Destination.Length: > 0 } ? RunwayDesignators.ForAirport(aircraft.Destination) : [];

    /// <summary>
    /// The runway a visual approach defaults to: the assigned runway, else the runway of the active approach, else
    /// that of the expected one, looked up among the destination's approaches; null when none applies.
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
        return ApproachRunway(approaches, aircraft.ActiveApproachId) ?? ApproachRunway(approaches, aircraft.ExpectedApproach);
    }

    /// <summary>The runway of the approach named <paramref name="approachId"/> among <paramref name="approaches"/>; null when none.</summary>
    private static string? ApproachRunway(IReadOnlyList<CifpApproachProcedure> approaches, string? approachId)
    {
        if (string.IsNullOrEmpty(approachId))
        {
            return null;
        }

        string? runway = approaches.FirstOrDefault(a => string.Equals(a.ApproachId, approachId, StringComparison.OrdinalIgnoreCase))?.Runway;
        return string.IsNullOrEmpty(runway) ? null : runway;
    }

    /// <summary>
    /// The visual-approach item: with a default runway (<see cref="SmartVisualRunway"/>) a leaf naming it that sends
    /// <c>CVA</c> for it; otherwise a picker over the destination's runways, or free text when it has none.
    /// <see cref="BuildClearedVisualOther"/> offers the other runways beside a default.
    /// </summary>
    private static MenuItem BuildClearedVisual(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (SmartVisualRunway(aircraft) is { } runway)
        {
            return BuildSend($"{label} {RunwayIdentifier.ToDisplayDesignator(runway)}", $"CVA {runway}", context, host);
        }

        IReadOnlyList<string> runways = DestinationRunways(aircraft);
        if (runways.Count > 0)
        {
            return BuildVisualRunwayList($"{label}{Ellipsis}", runways, context, host);
        }

        return BuildInput($"{label}{Ellipsis}", "Runway (e.g. 28R)", BlankInput.Closes, input => $"CVA {input}", context, host);
    }

    /// <summary>
    /// The visual-approach item's companion, which shares its id: a picker over the destination's runways labelled
    /// "(other)", offered beside a default runway; null without a default or without runways.
    /// </summary>
    internal static MenuItem? BuildClearedVisualOther(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (SmartVisualRunway(aircraft) is null)
        {
            return null;
        }

        IReadOnlyList<string> runways = DestinationRunways(aircraft);
        return runways.Count > 0 ? BuildVisualRunwayList($"{ClearedVisualLabel} (other){Ellipsis}", runways, context, host) : null;
    }

    /// <summary>A list picker over <paramref name="runways"/>, the first highlighted, that sends <c>CVA</c> for the pick.</summary>
    private static MenuItem BuildVisualRunwayList(string label, IReadOnlyList<string> runways, MenuContext context, IMenuHost host)
    {
        List<object> items = [.. runways];
        return BuildList(label, items, items[0], picked => Send($"CVA {picked}", context, host), host);
    }

    /// <summary>The traffic-in-sight request: bare <c>RTIS</c> for a blank answer, else <c>RTIS</c> naming the target.</summary>
    private static string FormatTrafficInSight(string input) => string.IsNullOrWhiteSpace(input) ? "RTIS" : $"RTIS {input}";

    /// <summary>
    /// The Join STAR item: with a filed STAR (<see cref="FiledStar"/>) a leaf naming it that sends <c>JARR</c> for it;
    /// otherwise a picker over the destination's STARs, or free text when it has none.
    /// <see cref="BuildJoinStarOther"/> offers the other STARs beside a filed one.
    /// </summary>
    private static MenuItem BuildJoinStar(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (FiledStar(aircraft) is { } star)
        {
            return BuildSend($"{label} {star}", $"JARR {star}", context, host);
        }

        IReadOnlyList<string> stars = DestinationStars(aircraft);
        if (stars.Count > 0)
        {
            return BuildStarList($"{label}{Ellipsis}", stars, context, host);
        }

        return BuildInput($"{label}{Ellipsis}", "STAR name", BlankInput.Closes, input => $"JARR {input}", context, host);
    }

    /// <summary>
    /// The Join STAR item's companion, which shares its id: a picker over the destination's STARs labelled "(other)",
    /// offered beside a filed STAR; null without a filed STAR or without STARs.
    /// </summary>
    internal static MenuItem? BuildJoinStarOther(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (FiledStar(aircraft) is null)
        {
            return null;
        }

        IReadOnlyList<string> stars = DestinationStars(aircraft);
        return stars.Count > 0 ? BuildStarList($"{JoinStarLabel} (other){Ellipsis}", stars, context, host) : null;
    }

    /// <summary>A list picker over <paramref name="stars"/>, the first highlighted, that sends <c>JARR</c> for the pick.</summary>
    private static MenuItem BuildStarList(string label, IReadOnlyList<string> stars, MenuContext context, IMenuHost host)
    {
        List<object> items = [.. stars];
        return BuildList(label, items, items[0], picked => Send($"JARR {picked}", context, host), host);
    }

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

    /// <summary>The destination's STAR ids, sorted ignoring case; none without an aircraft or a destination.</summary>
    private static IReadOnlyList<string> DestinationStars(IMenuAircraft? aircraft)
    {
        if (aircraft is not { Destination.Length: > 0 })
        {
            return [];
        }

        List<string> ids = [.. NavigationDatabase.Instance.GetStars(aircraft.Destination).Select(star => star.ProcedureId)];
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    /// <summary>The non-blank tokens of a filed route, split on spaces and dots.</summary>
    private static IEnumerable<string> RouteTokens(string route) =>
        route.Split(RouteSeparators, StringSplitOptions.RemoveEmptyEntries).Select(token => token.Trim()).Where(token => token.Length > 0);

    /// <summary>
    /// A fix entry over the aircraft's route fixes that sends <paramref name="command"/> with the fix; its form is
    /// <see cref="BuildOneManyOrInput"/>'s, and <see cref="BuildCrossFixOther"/> or <see cref="BuildDepartFixOther"/>
    /// offers free text beside the route fixes.
    /// </summary>
    private static MenuCatalogEntry RouteFixPicker(string id, string label, string command) =>
        new(
            id,
            label,
            MenuFlightRules.Both,
            Always,
            (ac, context, host) => BuildOneManyOrInput(new(label, command, FixNamePlaceholder), RouteFixes(ac), context, host)
        );

    /// <summary>
    /// The Cross fix item's companion, which shares its id: free text labelled "(other)" beside the route fixes; null
    /// without any.
    /// </summary>
    internal static MenuItem? BuildCrossFixOther(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildOtherInput(new(CrossFixLabel, CrossFixCommand, FixNamePlaceholder), RouteFixes(aircraft), context, host);

    /// <summary>
    /// The Depart fix item's companion, which shares its id: free text labelled "(other)" beside the route fixes; null
    /// without any.
    /// </summary>
    internal static MenuItem? BuildDepartFixOther(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildOtherInput(new(DepartFixLabel, DepartFixCommand, FixNamePlaceholder), RouteFixes(aircraft), context, host);

    /// <summary>The Join airway item over the airways the flight plan files; its form is <see cref="BuildOneManyOrInput"/>'s.</summary>
    private static MenuItem BuildJoinAirway(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildOneManyOrInput(new(label, JoinAirwayCommand, AirwayIdPlaceholder), FiledAirways(aircraft), context, host);

    /// <summary>
    /// The Join airway item's companion, which shares its id: free text labelled "(other)" beside the filed airways;
    /// null without any.
    /// </summary>
    internal static MenuItem? BuildJoinAirwayOther(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildOtherInput(new(JoinAirwayLabel, JoinAirwayCommand, AirwayIdPlaceholder), FiledAirways(aircraft), context, host);

    /// <summary>
    /// The airways the flight plan files, in filed order without repeats; none without a route. Every airway is never
    /// offered: the navigation data holds thousands, too many to pick from.
    /// </summary>
    private static IReadOnlyList<string> FiledAirways(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || string.IsNullOrEmpty(aircraft.Route))
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

    /// <summary>
    /// An item over <paramref name="values"/> that sends the spec's command with one of them: a leaf naming the only
    /// value, a list picker labelled with an ellipsis over two or more (the first highlighted), else free text showing
    /// the spec's placeholder.
    /// </summary>
    private static MenuItem BuildOneManyOrInput(ValueItemSpec spec, IReadOnlyList<string> values, MenuContext context, IMenuHost host)
    {
        if (values.Count == 1)
        {
            return BuildSend($"{spec.Label} {values[0]}", $"{spec.Command} {values[0]}", context, host);
        }

        if (values.Count > 1)
        {
            List<object> items = [.. values];
            return BuildList($"{spec.Label}{Ellipsis}", items, items[0], picked => Send($"{spec.Command} {picked}", context, host), host);
        }

        return BuildInput($"{spec.Label}{Ellipsis}", spec.Placeholder, BlankInput.Closes, input => $"{spec.Command} {input}", context, host);
    }

    /// <summary>
    /// The free-text companion of a <see cref="BuildOneManyOrInput"/> item, labelled "(other)" and offered beside one
    /// or more <paramref name="values"/>; null without any, where the item itself is the free text.
    /// </summary>
    private static MenuItem? BuildOtherInput(ValueItemSpec spec, IReadOnlyList<string> values, MenuContext context, IMenuHost host) =>
        values.Count > 0
            ? BuildInput($"{spec.Label} (other){Ellipsis}", spec.Placeholder, BlankInput.Closes, input => $"{spec.Command} {input}", context, host)
            : null;

    /// <summary>
    /// What a <see cref="BuildOneManyOrInput"/> item and its "(other)" companion say and send: the label they extend,
    /// the command the value follows and the free-text placeholder.
    /// </summary>
    private sealed record ValueItemSpec(string Label, string Command, string Placeholder);

    /// <summary>
    /// A join-radial entry that sends <paramref name="command"/> with a fix and a bearing. While the host has fix
    /// names, a type-to-filter fix picker whose pick opens the input popup showing <paramref name="bearingPrompt"/>
    /// for the fix; else free text taking the fix and the bearing together.
    /// </summary>
    private static MenuCatalogEntry RadialPicker(string id, string label, string command, Func<string, string> bearingPrompt) =>
        new(id, label, MenuFlightRules.Both, Always, (_, context, host) => BuildRadialPicker(label, command, bearingPrompt, context, host));

    private static MenuItem BuildRadialPicker(string label, string command, Func<string, string> bearingPrompt, MenuContext context, IMenuHost host)
    {
        if (host.FixNames is { } fixNames)
        {
            return BuildFilteredList(
                label,
                fixNames,
                null,
                fix =>
                {
                    host.ShowInputPopup(bearingPrompt(fix), BlankInput.Closes, "", 0, bearing => Send($"{command} {fix} {bearing}", context, host));
                    return Task.CompletedTask;
                },
                host
            );
        }

        return BuildInput(label, "FIX bearing", BlankInput.Closes, input => $"{command} {input}", context, host);
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
