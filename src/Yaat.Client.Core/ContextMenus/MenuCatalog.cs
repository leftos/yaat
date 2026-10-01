using System.Globalization;
using Avalonia.Controls;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Every context-menu action the catalog knows, one <see cref="MenuCatalogEntry"/> per <see cref="MenuIds"/>
/// identifier. A leaf's builder sends its command text through <see cref="IMenuHost.SendAsync"/>; an input leaf
/// opens the host's input popup and formats the submitted text into the command; a list or filtered-list picker opens
/// the host's list popup over values the catalog computes (headings, altitudes, speeds, fixes, approaches, runways,
/// STARs, airways) and formats the pick into the command, choosing its form from the data present when the menu is
/// built; a host leaf asks the host for the item itself, for the entries that open a host surface or read the
/// surface's own state — the warp popup, the flight-plan editor, the data-block toggle, hide and reset, the nav route,
/// the measure item, route and push-route drawing, and the ground's hold-short, follow, give-way, push-back-to and
/// preset-taxi submenus over the choices the host answers (the pushback faces are flat items of a companion helper,
/// <see cref="BuildPushbackFaces"/>); a value submenu builds a whole submenu of items from its own label and the menu
/// context, one per value — the leader directions, the J-ring radii, the cone lengths and the taxi-route modes; and the Cleared for
/// takeoff submenu offers the default clearance and runway heading, the VFR departure instructions when the aircraft
/// and the controller's VFR-for-IFR setting allow them, and a free-text item last. A pattern entry is a leaf naming the
/// assigned runway, else a runway picker, else free text, and each pattern maneuver applies only on the legs it fits.
/// On the ground view (<see cref="MenuContext.View"/>), line up and wait, Cleared for takeoff and cancel takeoff keep
/// the ground's narrower gates, line up and wait and the Cleared for takeoff header name the held runway, and that
/// submenu keeps the ground's modifier set with no free text; the relative ground items send as the previous selection.
/// </summary>
public static class MenuCatalog
{
    /// <summary>Every catalog entry, in menu order within each group.</summary>
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
        Leaf(MenuIds.TrackTrack, "Track", "TRACK", Always),
        Leaf(MenuIds.TrackDrop, "Drop track", "DROP", Always),
        Leaf(MenuIds.TrackAcceptHandoff, "Accept handoff", "ACCEPT", Always),
        InputLeaf(MenuIds.TrackInitiateHandoff, "Initiate handoff...", "Position ID", input => $"HO {input}"),
        Leaf(MenuIds.TrackCancelHandoff, "Cancel handoff", "CANCEL", Always),
        InputLeaf(MenuIds.TrackPointOut, "Point out...", "Position ID", input => $"PO {input}"),
        Leaf(MenuIds.TrackAcknowledgePointout, "Acknowledge pointout", "OK", Always),
        InputLeaf(MenuIds.SquawkCode, "Squawk...", "Code (0000-7777)", input => $"SQ {int.Parse(input)}"),
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
            "Custom...",
            MenuFlightRules.Both,
            CanAskPilot,
            (_, context, host) => BuildInput("Custom...", "Text", input => $"SAY {input}", context, host)
        ),
        Leaf(MenuIds.CoordinationRelease, "Release", "RD", Always),
        Leaf(MenuIds.CoordinationHold, "Hold", "RDH", Always),
        Leaf(MenuIds.CoordinationRecall, "Recall", "RDR", Always),
        Leaf(MenuIds.CoordinationAcknowledge, "Acknowledge release", "RDACK", Always),
        Leaf(
            MenuIds.CoordinationCheckReleaseWindow,
            "Check release window",
            "CFR CHECK",
            (ac, _) => AircraftCommandApplicability.CanCheckReleaseWindow(ac)
        ),
        InputLeaf(MenuIds.DataBlockScratchpad, "Scratchpad...", "Text", input => $"SP {input}"),
        InputLeaf(MenuIds.DataBlockNote, "Note...", "Note text (max 40)", input => $"NOTE {input}"),
        InputLeaf(MenuIds.DataBlockTempAltitude, "Temporary altitude...", "Altitude", input => $"TEMPALT {int.Parse(input)}"),
        InputLeaf(MenuIds.DataBlockCruise, "Cruise...", "Altitude", input => $"CRUISE {int.Parse(input)}"),
        Leaf(MenuIds.DataBlockAnnotate, "Annotate", "ANNOTATE", Always),
        HostLeaf(MenuIds.SimControlWarp, "Warp...", Always, BuildWarp),
        Leaf(MenuIds.SimControlDelete, "Delete", "DEL", Always),
        HostLeaf(MenuIds.AircraftEditFlightPlan, "Edit flight plan", CanEditFlightPlan, BuildEditFlightPlan),
        HostLeaf(MenuIds.DisplayMiniDataBlock, "Mini datablock", Always, BuildMiniDataBlock),
        HostLeaf(MenuIds.DisplayResetDataBlockPosition, "Reset to student position", Always, BuildResetDataBlockPosition),
        HostLeaf(MenuIds.DisplayNavRoute, "Show nav route", Always, BuildNavRoute),
        HostLeaf(MenuIds.DisplayMeasure, "Measure", Always, BuildMeasure),
        Submenu(MenuIds.DisplayLeaderDirection, "Leader direction", BuildLeaderDirection),
        Submenu(MenuIds.DisplayJRing, "J-ring", BuildJRing),
        Submenu(MenuIds.DisplayCone, "Cone", BuildCone),
        Leaf(MenuIds.DisplayBlank, "Blank target", "BLANK", Always),
        Leaf(MenuIds.DisplayUnblank, "Unblank target", "BLANKD", Always),
        Submenu(MenuIds.DisplayTaxiRoute, "Taxi route", BuildTaxiRoute),
        HostLeaf(MenuIds.DisplayHideDataBlock, "Hide datablock", Always, BuildHideDataBlock),
        Leaf(MenuIds.HeadingPresent, "Present heading", "FPH", Always),
        HeadingList(MenuIds.HeadingFly, "Fly heading", "FH"),
        HeadingList(MenuIds.HeadingTurnLeft, "Turn left", "TL"),
        HeadingList(MenuIds.HeadingTurnRight, "Turn right", "TR"),
        RelativeTurnList(MenuIds.HeadingTurnLeftDegrees, "Turn left (degrees)", "LT"),
        RelativeTurnList(MenuIds.HeadingTurnRightDegrees, "Turn right (degrees)", "RT"),
        Picker(MenuIds.AltitudeMaintain, "Maintain", BuildMaintainAltitude),
        Picker(MenuIds.SpeedAssign, "Assign speed", BuildAssignSpeed),
        InputLeaf(MenuIds.SpeedCustom, "Speed...", "Speed (knots)", input => $"SPD {int.Parse(input)}"),
        Leaf(MenuIds.SpeedNormal, "Resume normal speed", "RNS", Always),
        Picker(MenuIds.SpeedFinalApproach, "FAS", BuildFinalApproachSpeed),
        FixPicker(MenuIds.NavigationDirectTo, "Direct to...", "DCT", Always, RouteFixes),
        FixPicker(MenuIds.NavigationAppendDirectTo, "Append direct to...", "ADCT", IsNavigatingToFix, RouteFixes),
        HostLeaf(MenuIds.NavigationDrawRoute, "Draw route", Always, BuildDrawRoute),
        Leaf(MenuIds.HoldPresentLeft, "Hold present position (left)", "HPPL", Always),
        Leaf(MenuIds.HoldPresentRight, "Hold present position (right)", "HPPR", Always),
        FixPicker(MenuIds.HoldFixLeft, "Hold at fix (left)...", "HFIXL", Always, NoRouteFixes),
        FixPicker(MenuIds.HoldFixRight, "Hold at fix (right)...", "HFIXR", Always, NoRouteFixes),
        ApproachPicker(MenuIds.ApproachCleared, "Cleared approach", "CAPP"),
        ApproachPicker(MenuIds.ApproachJoin, "Join approach", "JAPP"),
        ApproachPicker(MenuIds.ApproachClearedStraightIn, "Cleared straight-in", "CAPPSI"),
        ApproachPicker(MenuIds.ApproachJoinStraightIn, "Join straight-in", "JAPPSI"),
        ApproachPicker(MenuIds.ApproachClearedForce, "Cleared approach (force)", "CAPPF"),
        ApproachPicker(MenuIds.ApproachJoinForce, "Join approach (force)", "JAPPF"),
        ApproachPicker(MenuIds.ApproachJoinFinalCourse, "Join final approach course", "JFAC"),
        ApproachPicker(MenuIds.ApproachExpect, "Expect approach", "EAPP"),
        Picker(MenuIds.ApproachClearedVisual, ClearedVisualLabel, BuildClearedVisual),
        Leaf(MenuIds.ApproachReportFieldInSight, "Report field in sight", "RFIS", Always),
        InputLeaf(MenuIds.ApproachReportTrafficInSight, "Report traffic in sight...", "Target callsign (optional)", FormatTrafficInSight),
        Leaf(MenuIds.ApproachReportBase, "Turning base", "REPORT BASE", Always),
        Leaf(MenuIds.ApproachReportFinal, "Turning final", "REPORT FINAL", Always),
        Leaf(MenuIds.ApproachReportCrosswind, "Turning crosswind", "REPORT CROSSWIND", Always),
        Leaf(MenuIds.ApproachReportDownwind, "Turning downwind", "REPORT DOWNWIND", Always),
        InputLeaf(MenuIds.ApproachReportNMileFinal, "N-mile final...", "Distance (NM)", input => $"REPORT {input} FINAL"),
        InputLeaf(MenuIds.ApproachReportAtFix, "At fix...", "Fix name", input => $"REPORT {input}"),
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
        InputLeaf(MenuIds.ProceduresPtac, "PTAC...", "PTAC arguments", input => $"PTAC {input}"),
        Picker(MenuIds.ProceduresJoinAirway, JoinAirwayLabel, BuildJoinAirway),
        RadialPicker(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound...", "JRADO", fix => $"Bearing from {fix} (0-360)"),
        RadialPicker(MenuIds.ProceduresJoinRadialInbound, "Join radial inbound...", "JRADI", fix => $"Bearing to {fix} (0-360)"),
        new(
            MenuIds.TowerLineUpAndWait,
            LineUpAndWaitLabel,
            MenuFlightRules.Both,
            (ac, context) => context.View == MenuView.Ground ? CanGroundLineUpAndWait(ac) : AircraftCommandApplicability.CanLineUpAndWait(ac),
            BuildLineUpAndWait
        ),
        new(
            MenuIds.TowerClearedForTakeoff,
            ClearedForTakeoffLabel,
            MenuFlightRules.Both,
            (ac, context) => context.View == MenuView.Ground ? CanGroundClearForTakeoff(ac) : AircraftCommandApplicability.CanClearForTakeoff(ac),
            (ac, context, host) =>
                context.View == MenuView.Ground ? BuildGroundClearedForTakeoff(ac, context, host) : BuildClearedForTakeoff(ac, context, host)
        ),
        Leaf(
            MenuIds.TowerCancelTakeoff,
            "Cancel takeoff clearance",
            "CTOC",
            (ac, context) => context.View == MenuView.Ground ? CanGroundCancelTakeoff(ac) : AircraftCommandApplicability.CanCancelTakeoff(ac)
        ),
        RunwayLeaf(MenuIds.TowerClearedToLand, "Cleared to land", "CLAND", CanClearToLand),
        RunwayLeaf(MenuIds.TowerForceLanding, "Force landing", "CLANDF", CanForceLanding),
        RunwayLeaf(MenuIds.TowerClearedOption, "Cleared for the option", "COPT", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerTouchAndGo, "Touch and go", "TG", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerStopAndGo, "Stop and go", "SG", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerLowApproach, "Low approach", "LA", CanIssueVfrOption),
        RunwayLeaf(MenuIds.TowerGoAround, "Go around", "GA", (ac, _) => AircraftCommandApplicability.CanGoAround(ac)),
        Leaf(MenuIds.TowerCancelLanding, "Cancel landing clearance", "CLC", (ac, _) => AircraftCommandApplicability.CanCancelLandingClearance(ac)),
        Leaf(MenuIds.TowerExitLeft, "Exit left", "EL", CanExitRunway),
        Leaf(MenuIds.TowerExitRight, "Exit right", "ER", CanExitRunway),
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
            "Push back to...",
            (ac, _) => AircraftCommandApplicability.CanPushBack(ac),
            (label, _, context, host) => BuildChoiceSubmenu(label, host.GetPushbackToChoices(context.Callsign), context, host)
        ),
        HostLeaf(MenuIds.GroundPushRoute, "Push route...", (ac, _) => AircraftCommandApplicability.CanPushBack(ac), BuildPushRoute),
        Leaf(MenuIds.GroundHoldPosition, "Hold position", "HP", (ac, _) => AircraftCommandApplicability.CanHoldPosition(ac)),
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
        Leaf(MenuIds.GroundBreakConflict, "Break conflict", "BREAK", (ac, _) => AircraftCommandApplicability.CanBreakConflict(ac)),
        HostLeaf(MenuIds.GroundHoldShort, "Hold short of...", (ac, _) => AircraftCommandApplicability.CanHoldShort(ac), BuildHoldShort),
        HostLeaf(
            MenuIds.GroundFollow,
            "Follow...",
            AircraftCommandApplicability.CanFollowBehind,
            (label, _, context, host) => BuildGroundTraffic(label, "FOLLOWG", context, host)
        ),
        HostLeaf(
            MenuIds.GroundGiveWay,
            "Give way to...",
            AircraftCommandApplicability.CanGiveWayTo,
            (label, _, context, host) => BuildGroundTraffic(label, "GW", context, host)
        ),
        HostLeaf(
            MenuIds.GroundTaxiPreset,
            "Preset taxi route",
            (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac),
            (label, _, context, host) => BuildChoiceSubmenu(label, host.GetPresetTaxiChoices(context.Callsign), context, host)
        ),
        HostLeaf(MenuIds.GroundDrawTaxiRoute, "Draw taxi route...", (ac, _) => AircraftCommandApplicability.CanDrawTaxiRoute(ac), BuildDrawRoute),
        RelativeGround(MenuIds.GroundRelativeGiveWay, "Selected aircraft: give way to", "give way to", "GW"),
        RelativeGround(MenuIds.GroundRelativeFollow, "Selected aircraft: follow", "follow", "FOLLOWG"),
    ];

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
            MenuIds.PatternEnterFinal => ("Enter straight-in final", "EF"),
            _ => throw new ArgumentException($"'{id}' is not a pattern-entry menu id", nameof(id)),
        };

    /// <summary>The Cleared for takeoff entry's label, the header of the submenu it builds.</summary>
    private const string ClearedForTakeoffLabel = "Cleared for takeoff";

    /// <summary>The Line up and wait entry's label, which the runway it names follows.</summary>
    private const string LineUpAndWaitLabel = "Line up and wait";

    /// <summary>The Cross runway entry's label, which the held runway follows.</summary>
    private const string CrossRunwayLabel = "Cross";

    /// <summary>The ground view's closed-traffic departure instructions, offered after the default when the VFR ones apply.</summary>
    private static readonly (string Label, string Argument)[] GroundTrafficTakeoffModifiers =
    [
        ("Make left traffic", "MLT"),
        ("Make right traffic", "MRT"),
    ];

    /// <summary>The ground view's departure-heading instructions, offered after the closed-traffic ones when the VFR ones apply.</summary>
    private static readonly (string Label, string Argument)[] GroundDepartureTakeoffModifiers =
    [
        ("Runway heading", "RH"),
        ("On course", "OC"),
        ("Right crosswind (90° right)", "MRC"),
        ("Right downwind (180° right)", "MRD"),
        ("Left crosswind (90° left)", "MLC"),
        ("Left downwind (180° left)", "MLD"),
    ];

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
    private const string Ellipsis = "...";

    /// <summary>The ground view's text for the data-block reset item, which the radar labels "Reset to student position".</summary>
    private const string GroundResetDataBlockLabel = "Reset datablock position";

    /// <summary>The taxi-route submenu's items, in menu order: each item's text and the mode it sets.</summary>
    private static readonly (string Label, TaxiRouteDisplayMode Mode)[] TaxiRouteModeItems =
    [
        ("Always show", TaxiRouteDisplayMode.AlwaysShow),
        ("Always hide", TaxiRouteDisplayMode.AlwaysHide),
        ("Follow “Show all” setting", TaxiRouteDisplayMode.Follow),
    ];

    /// <summary>The J-ring radii and cone lengths the display submenus offer, in nautical miles.</summary>
    private static readonly double[] RingDistances = [1.0, 2.0, 3.0, 5.0, 10.0];

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
        item.Click += async (_, _) => await host.SendAsync(context.Callsign, command, context.Initials);
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

    private static Func<IMenuAircraft?, MenuContext, bool> CanExitRunway => (ac, _) => AircraftCommandApplicability.CanExitRunway(ac);

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

    /// <summary>The runway the aircraft is holding short of (<see cref="HoldShortMenuHelper.HeldRunway"/>), or null when none.</summary>
    private static string? HeldRunway(IMenuAircraft? aircraft) => HoldShortMenuHelper.HeldRunway(aircraft?.CurrentPhase ?? "", aircraft);

    /// <summary>
    /// The runway the ground view's takeoff items name: the held runway at a hold-short, else the assigned one; null
    /// or empty when there is none.
    /// </summary>
    private static string? GroundTakeoffRunway(IMenuAircraft? aircraft) =>
        (aircraft?.CurrentPhase ?? "").StartsWith("Holding Short", StringComparison.Ordinal) ? HeldRunway(aircraft) : aircraft?.AssignedRunway;

    /// <summary>
    /// The ground view's Cleared for takeoff gate, narrower than <see cref="AircraftCommandApplicability.CanClearForTakeoff"/>:
    /// a departure taxiing to, holding short of or lined up on a runway it can name (<see cref="GroundTakeoffRunway"/>),
    /// never while lining up or rolling.
    /// </summary>
    private static bool CanGroundClearForTakeoff(IMenuAircraft? aircraft)
    {
        if (!AircraftCommandApplicability.IsControllable(aircraft))
        {
            return false;
        }

        string phase = aircraft.CurrentPhase ?? "";
        bool departing = (phase is "Taxiing" or "LinedUpAndWaiting") || (phase.StartsWith("Holding Short", StringComparison.Ordinal));
        return departing && !string.IsNullOrEmpty(GroundTakeoffRunway(aircraft));
    }

    /// <summary>The ground view's Line up and wait gate: holding short of a runway it can name, never while taxiing to one.</summary>
    private static bool CanGroundLineUpAndWait(IMenuAircraft? aircraft) => AircraftCommandApplicability.CanCrossRunway(aircraft);

    /// <summary>The ground view's Cancel takeoff clearance gate: lined up and waiting or rolling, never while lining up.</summary>
    private static bool CanGroundCancelTakeoff(IMenuAircraft? aircraft) =>
        AircraftCommandApplicability.IsControllable(aircraft) && ((aircraft.CurrentPhase ?? "") is "LinedUpAndWaiting" or "Takeoff");

    /// <summary>
    /// Line up and wait, sending the bare verb: on the ground view the label names the held runway, elsewhere the
    /// assigned one.
    /// </summary>
    private static MenuItem BuildLineUpAndWait(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string suffix = context.View == MenuView.Ground ? DisplaySuffix(HeldRunway(aircraft)) : RunwaySuffix(aircraft);
        return BuildSend(LineUpAndWaitLabel + suffix, "LUAW", context, host);
    }

    /// <summary>Cross the held runway, named in display form and sent as held; null when the aircraft holds short of none.</summary>
    private static MenuItem? BuildCrossRunway(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        HeldRunway(aircraft) is { } runway ? BuildSend(CrossRunwayLabel + DisplaySuffix(runway), $"CROSS {runway}", context, host) : null;

    /// <summary>
    /// The ground view's Cleared for takeoff submenu, headed with the runway it names (<see cref="GroundTakeoffRunway"/>):
    /// the default clearance, then the closed-traffic and departure-heading instructions when
    /// <see cref="AircraftCommandApplicability.ShowVfrTakeoffModifiers"/> allows them, else runway heading only. No free text.
    /// </summary>
    private static MenuItem BuildGroundClearedForTakeoff(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = ClearedForTakeoffLabel + DisplaySuffix(GroundTakeoffRunway(aircraft)) };
        menu.Items.Add(BuildSend("Default (SID/on course)", "CTO", context, host));
        menu.Items.Add(new Separator());
        if (AircraftCommandApplicability.ShowVfrTakeoffModifiers(aircraft, context.VfrCommandsForIfr))
        {
            AddTakeoffModifiers(menu.Items, GroundTrafficTakeoffModifiers, context, host);
            menu.Items.Add(new Separator());
            AddTakeoffModifiers(menu.Items, GroundDepartureTakeoffModifiers, context, host);
        }
        else
        {
            menu.Items.Add(BuildSend("Runway heading", "CTO RH", context, host));
        }

        return menu;
    }

    /// <summary>Adds one item per modifier, each sending <c>CTO</c> followed by the modifier's argument.</summary>
    private static void AddTakeoffModifiers(ItemCollection items, (string Label, string Argument)[] modifiers, MenuContext context, IMenuHost host)
    {
        foreach ((string label, string argument) in modifiers)
        {
            items.Add(BuildSend(label, $"CTO {argument}", context, host));
        }
    }

    /// <summary>
    /// A relative ground item, offered while <see cref="RelativeTraffic.OffersGroundRelative"/> holds: sent as the
    /// previous selection with the right-clicked callsign after <paramref name="verb"/>, labelled
    /// "{selected}: {phrase} {right-clicked}".
    /// </summary>
    private static MenuCatalogEntry RelativeGround(string id, string label, string phrase, string verb) =>
        new(
            id,
            label,
            MenuFlightRules.Both,
            RelativeTraffic.OffersGroundRelative,
            (_, context, host) => BuildRelativeGround(phrase, verb, context, host)
        );

    /// <summary>The relative ground item for the context's previous selection, or null when there is none.</summary>
    private static MenuItem? BuildRelativeGround(string phrase, string verb, MenuContext context, IMenuHost host)
    {
        if (context.PreviousSelection is not { } selected)
        {
            return null;
        }

        string sender = selected.Callsign;
        var item = new MenuItem { Header = $"{sender}: {phrase} {context.Callsign}" };
        item.Click += async (_, _) => await host.SendAsync(sender, $"{verb} {context.Callsign}", context.Initials);
        return item;
    }

    /// <summary>
    /// The Hold short of… submenu: one item per target the host finds on the taxi route, sending the host's finished
    /// <c>HS</c> command, and previewing the route to the target when the pointer enters it. Null when the route offers
    /// no target.
    /// </summary>
    private static MenuItem? BuildHoldShort(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        BuildChoiceSubmenu(label, host.GetHoldShortChoices(context.Callsign), context, host);

    /// <summary>
    /// The pushback-face entry's companion, which the ground group places right after Push back: one flat item per
    /// facing the host answers, sending its finished <c>PUSH FACE</c> command. Building the entry itself throws.
    /// </summary>
    internal static IReadOnlyList<MenuItem> BuildPushbackFaces(MenuContext context, IMenuHost host) =>
        [.. host.GetPushbackFaceChoices(context.Callsign).Select(choice => BuildSend(choice.Label, choice.Command, context, host))];

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
            MenuItem item = BuildSend(choice.Label, choice.Command, context, host);
            if (choice.Preview is { } preview)
            {
                item.PointerEntered += (_, _) => host.SetRoutePreview(preview);
            }

            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// The Follow… or Give way to… submenu: one item per aircraft of the host's nearest ground traffic, sending
    /// <paramref name="verb"/> with its callsign. Null when there is no other aircraft on the ground.
    /// </summary>
    private static MenuItem? BuildGroundTraffic(string label, string verb, MenuContext context, IMenuHost host)
    {
        IReadOnlyList<string> traffic = host.GetGroundTrafficCallsigns(context.Callsign);
        if (traffic.Count == 0)
        {
            return null;
        }

        var menu = new MenuItem { Header = label };
        foreach (string other in traffic)
        {
            menu.Items.Add(BuildSend(other, $"{verb} {other}", context, host));
        }

        return menu;
    }

    /// <summary>
    /// The Cleared for takeoff submenu: the default clearance (the filed SID for IFR, runway heading for VFR) and an
    /// explicit runway heading for either, then the VFR-only departure instructions when
    /// <see cref="AircraftCommandApplicability.ShowVfrTakeoffModifiers"/> allows them, then free text: blank sends a
    /// bare <c>CTO</c>, anything else is trimmed and sent after it.
    /// </summary>
    private static MenuItem BuildClearedForTakeoff(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = ClearedForTakeoffLabel };
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
                "Custom...",
                ClearedForTakeoffPlaceholder,
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

    private static MenuCatalogEntry InputLeaf(string id, string label, string placeholder, Func<string, string> format) =>
        new(id, label, MenuFlightRules.Both, Always, (_, context, host) => BuildInput(label, placeholder, format, context, host));

    /// <summary>
    /// An entry whose item the host builds for a surface of its own rather than from a command text: the warp popup,
    /// the flight-plan editor, the display toggles and measure item that read and drive the surface's own state, and
    /// the ground submenus whose choices the host answers (hold short, follow, give way, push back to, preset taxi).
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

    /// <summary>
    /// An entry that builds a whole submenu of command items rather than a single leaf — the leader directions, the
    /// J-ring radii and the cone lengths today. <paramref name="build"/> receives the entry's own label, so the
    /// submenu's header lives in one place.
    /// </summary>
    private static MenuCatalogEntry Submenu(string id, string label, Func<string, MenuContext, IMenuHost, MenuItem> build) =>
        new(id, label, MenuFlightRules.Both, Always, (_, context, host) => build(label, context, host));

    private static MenuItem BuildInput(string label, string placeholder, Func<string, string> format, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label, Tag = new MenuPickerDescriptor(MenuPickerDescriptor.Input, []) };
        item.Click += (_, _) => host.ShowInputPopup(placeholder, input => host.SendAsync(context.Callsign, format(input), context.Initials));
        return item;
    }

    /// <summary>
    /// The Warp item: it seeds the host's warp popup with the aircraft's heading, altitude and indicated airspeed and
    /// sends <c>WARP {frd} {heading} {altitude} {speed}</c> for the values the popup submits. A WARP needs a real
    /// heading, so a zero or negative one is clamped to 360.
    /// </summary>
    private static MenuItem BuildWarp(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
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
                heading,
                altitude,
                speed,
                (frd, h, a, s) => host.SendAsync(context.Callsign, $"WARP {frd} {h} {a} {s}", context.Initials)
            );
        };
        return item;
    }

    /// <summary>
    /// The Edit flight plan item, which asks the host to open its flight-plan editor. It takes the aircraft and
    /// context it has no use for so that it matches the <see cref="HostLeaf"/> builder shape.
    /// </summary>
    private static MenuItem BuildEditFlightPlan(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.OpenFlightPlanEditor();
        return item;
    }

    /// <summary>
    /// The data-block form item: it toggles the host's data block and reads "Mini datablock" or "Full datablock" by
    /// the form the surface is showing.
    /// </summary>
    private static MenuItem BuildMiniDataBlock(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = host.IsMinified(context.Callsign) ? "Full datablock" : "Mini datablock" };
        item.Click += (_, _) => host.ToggleMinified(context.Callsign);
        return item;
    }

    /// <summary>
    /// The "Reset to student position" item ("Reset datablock position" on the ground view), which the surface offers
    /// only while the data block sits away from the position the student sees it in; null otherwise.
    /// </summary>
    private static MenuItem? BuildResetDataBlockPosition(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!host.HasManualDataBlockOffset(context.Callsign))
        {
            return null;
        }

        var item = new MenuItem { Header = context.View == MenuView.Ground ? GroundResetDataBlockLabel : label };
        item.Click += (_, _) => host.ResetDataBlockOffset(context.Callsign);
        return item;
    }

    /// <summary>The nav-route item: it toggles the route and reads "Show nav route" or "Hide nav route" by whether it is drawn.</summary>
    private static MenuItem BuildNavRoute(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = host.IsPathShown(context.Callsign) ? "Hide nav route" : label };
        item.Click += (_, _) => host.ToggleShowPath(context.Callsign);
        return item;
    }

    /// <summary>
    /// The measure item, offered whenever the surface has a measure tool: it reads "from" while the tool has no
    /// endpoint yet and "to" once one is anchored, and latches that endpoint to the aircraft the menu was opened on.
    /// </summary>
    private static MenuItem? BuildMeasure(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        MenuMeasureState state = host.GetMeasureState();
        if (state == MenuMeasureState.None)
        {
            return null;
        }

        string direction = state == MenuMeasureState.HasAnchor ? "to" : "from";
        var item = new MenuItem { Header = $"Measure {direction} {context.Callsign}" };
        item.Click += (_, _) => host.MeasurePickOnAircraft(context.Callsign);
        return item;
    }

    /// <summary>
    /// The taxi-route submenu: one radio item per <see cref="TaxiRouteDisplayMode"/>, the host's current mode checked,
    /// each setting that mode on the host when clicked.
    /// </summary>
    private static MenuItem BuildTaxiRoute(string label, MenuContext context, IMenuHost host)
    {
        TaxiRouteDisplayMode current = host.GetTaxiRouteMode(context.Callsign);
        var menu = new MenuItem { Header = label };
        foreach ((string itemLabel, TaxiRouteDisplayMode mode) in TaxiRouteModeItems)
        {
            var item = new MenuItem
            {
                Header = itemLabel,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "TaxiRouteMode",
                IsChecked = mode == current,
            };
            item.Click += (_, _) => host.SetTaxiRouteMode(context.Callsign, mode);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>The hidden-datablock item: it toggles the host's data block and reads "Show datablock" while it is hidden.</summary>
    private static MenuItem BuildHideDataBlock(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = host.IsDataBlockHidden(context.Callsign) ? "Show datablock" : label };
        item.Click += (_, _) => host.ToggleHiddenDataBlock(context.Callsign);
        return item;
    }

    /// <summary>The leader-direction submenu: one line per 1-9, with 5 marked as the STARS default.</summary>
    private static MenuItem BuildLeaderDirection(string label, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = label };
        for (int direction = 1; direction <= 9; direction++)
        {
            string itemLabel = direction == 5 ? "5 (default)" : direction.ToString(CultureInfo.InvariantCulture);
            menu.Items.Add(BuildSend(itemLabel, $"LDR {direction}", context, host));
        }

        return menu;
    }

    /// <summary>The J-ring submenu: Clear turns the overlay off, then one item per ring radius.</summary>
    private static MenuItem BuildJRing(string label, MenuContext context, IMenuHost host) => BuildRingMenu(label, "JRING", context, host);

    /// <summary>The cone submenu: Clear turns the overlay off, then one item per cone length.</summary>
    private static MenuItem BuildCone(string label, MenuContext context, IMenuHost host) => BuildRingMenu(label, "CONE", context, host);

    /// <summary>
    /// A J-ring or cone submenu: Clear sends the bare command, then each distance sends it with the size. The item
    /// reads "3 nm" while the command carries the same figure without the unit.
    /// </summary>
    private static MenuItem BuildRingMenu(string label, string command, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = label };
        menu.Items.Add(BuildSend("Clear", command, context, host));
        foreach (double distance in RingDistances)
        {
            string size = distance.ToString("0.#", CultureInfo.InvariantCulture);
            menu.Items.Add(BuildSend($"{distance:0} nm", $"{command} {size}", context, host));
        }

        return menu;
    }

    /// <summary>An altitude as the altitude picker and the Altitude header show it: a flight level from 18,000 ft, feet below.</summary>
    internal static string FormatAltitude(int altitude) => altitude >= 18000 ? $"FL{altitude / 100}" : $"{altitude}";

    /// <summary>The aircraft's route fixes, which the navigation pickers offer first; none without an aircraft.</summary>
    private static Func<IMenuAircraft?, IReadOnlyList<string>> RouteFixes => ac => ac?.RouteFixNames() ?? [];

    /// <summary>No route fixes: the hold pickers offer every fix alike.</summary>
    private static Func<IMenuAircraft?, IReadOnlyList<string>> NoRouteFixes => _ => [];

    /// <summary>Whether the aircraft is navigating to a fix, which an appended direct-to follows.</summary>
    private static Func<IMenuAircraft?, MenuContext, bool> IsNavigatingToFix => (ac, _) => !string.IsNullOrEmpty(ac?.NavigatingTo);

    private static Task Send(string command, MenuContext context, IMenuHost host) => host.SendAsync(context.Callsign, command, context.Initials);

    /// <summary>
    /// A list picker: the host's list popup offers <paramref name="items"/> with <paramref name="selected"/>
    /// highlighted, and the pick reaches <paramref name="onPick"/> as its value — a <see cref="MenuLabeledValue"/> is
    /// unwrapped to its int first. The item's descriptor carries the texts the popup lists.
    /// </summary>
    private static MenuItem BuildList(string label, IReadOnlyList<object> items, object? selected, Func<object, Task> onPick, IMenuHost host)
    {
        var item = new MenuItem { Header = label, Tag = new MenuPickerDescriptor(MenuPickerDescriptor.List, PickerTexts(items)) };
        item.Click += (_, _) => host.ShowListPopup(items, selected, picked => onPick(picked is MenuLabeledValue labeled ? labeled.Value : picked));
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

    /// <summary>
    /// The Maintain picker: every altitude from the destination's field elevation up, listed as the controller reads
    /// them (flight levels from 18,000 ft), sending <c>CM</c> above the aircraft's altitude and <c>DM</c> otherwise.
    /// Null when no altitude is listed.
    /// </summary>
    private static MenuItem? BuildMaintainAltitude(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        int current = (int)(aircraft?.AltitudeFeet ?? 0);
        List<object> altitudes = AltitudeValues(host.GetFieldElevation(aircraft?.Destination));
        if (altitudes.Count == 0)
        {
            return null;
        }

        return BuildList(
            label,
            altitudes,
            null,
            picked =>
            {
                int altitude = (int)picked;
                return Send(altitude > current ? $"CM {altitude}" : $"DM {altitude}", context, host);
            },
            host
        );
    }

    /// <summary>Every 100 ft from the field up to 5,000 ft above it, then every 500 ft to 60,000 ft, each labelled.</summary>
    private static List<object> AltitudeValues(double fieldElevation)
    {
        var items = new List<object>();
        int lowThreshold = (int)(fieldElevation + 5000);

        int roundedLow = (int)(Math.Ceiling(fieldElevation / 100.0) * 100);
        if (roundedLow < 100)
        {
            roundedLow = 100;
        }

        for (int altitude = roundedLow; altitude < lowThreshold; altitude += 100)
        {
            items.Add(new MenuLabeledValue(FormatAltitude(altitude), altitude));
        }

        int start500 = (int)(Math.Ceiling(lowThreshold / 500.0) * 500);
        for (int altitude = start500; altitude <= 60000; altitude += 500)
        {
            items.Add(new MenuLabeledValue(FormatAltitude(altitude), altitude));
        }

        return items;
    }

    /// <summary>The Assign speed picker, highlighting the assigned speed rounded to ten knots, or the middle of the list.</summary>
    private static MenuItem BuildAssignSpeed(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<object> speeds = SpeedValues(aircraft);
        double? assigned = aircraft?.AssignedSpeed;
        int seed = assigned is > 0 ? (int)(Math.Round(assigned.Value / 10.0) * 10) : (int)speeds[speeds.Count / 2];
        return BuildList(label, speeds, seed, picked => Send($"SPD {picked}", context, host), host);
    }

    /// <summary>
    /// The speeds the Assign speed picker lists, in tens: from the filed type's approach speed to its climb speed at the
    /// aircraft's altitude, widened to at least 50 kt and never below 40 kt; 150-350 kt when no type is filed.
    /// </summary>
    private static List<object> SpeedValues(IMenuAircraft? aircraft)
    {
        if (aircraft is null || string.IsNullOrEmpty(aircraft.FiledAircraftType))
        {
            return SpeedRange(150, 350);
        }

        string type = aircraft.FiledAircraftType;
        AircraftCategory category = AircraftCategorization.Categorize(type);
        double approach = AircraftPerformance.ApproachSpeed(type, category);
        double climb = AircraftPerformance.ClimbSpeed(type, category, Math.Max(aircraft.AltitudeFeet, 0));

        int min = (int)(Math.Floor(approach / 10.0) * 10);
        int max = (int)(Math.Ceiling(climb / 10.0) * 10);
        if (min < 40)
        {
            min = 40;
        }

        if (max - min < 50)
        {
            min = Math.Max(40, min - 20);
            max += 20;
        }

        return SpeedRange(min, max);
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

    /// <summary>The final-approach-speed label: "FAS - 140 kt" with the filed type's approach speed, else the bare label.</summary>
    private static string FinalApproachSpeedLabel(string label, IMenuAircraft? aircraft)
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

        return BuildInput(label, FixNamePlaceholder, input => $"{command} {input}", context, host);
    }

    /// <summary>The Draw route item, which puts the host into drawing a route for the aircraft.</summary>
    private static MenuItem BuildDrawRoute(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.EnterDrawRoute(context.Callsign);
        return item;
    }

    /// <summary>An approach picker that sends <paramref name="command"/> with the picked or typed approach id.</summary>
    private static MenuCatalogEntry ApproachPicker(string id, string label, string command) =>
        new(id, label, MenuFlightRules.Both, Always, (ac, context, host) => BuildApproachPicker(label, command, ac, context, host));

    /// <summary>
    /// The approach picker's form, by the data present when the menu is built: a list of the destination's approaches
    /// with the first highlighted, else free text under the label with an ellipsis.
    /// </summary>
    private static MenuItem BuildApproachPicker(string label, string command, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<object> ids = [.. DestinationApproaches(aircraft).Select(approach => (object)approach.ApproachId)];
        if (ids.Count > 0)
        {
            return BuildList(label, ids, ids[0], picked => Send($"{command} {picked}", context, host), host);
        }

        return BuildInput($"{label}{Ellipsis}", "Approach ID", input => $"{command} {input}", context, host);
    }

    /// <summary>The destination's published approaches; none without an aircraft or a destination.</summary>
    private static IReadOnlyList<CifpApproachProcedure> DestinationApproaches(IMenuAircraft? aircraft) =>
        aircraft is { Destination.Length: > 0 } ? NavigationDatabase.Instance.GetApproaches(aircraft.Destination) : [];

    /// <summary>The destination's runway designators, which the visual-approach picker lists; none without a destination.</summary>
    private static IReadOnlyList<string> DestinationRunways(IMenuAircraft? aircraft) =>
        aircraft is { Destination.Length: > 0 } ? RunwayDesignators.ForAirport(aircraft.Destination) : [];

    /// <summary>
    /// The runway a visual approach defaults to: the assigned runway, else the runway of the active approach, else
    /// that of the expected one, looked up among the destination's approaches; null when none applies.
    /// </summary>
    private static string? SmartVisualRunway(IMenuAircraft? aircraft)
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

        return BuildInput($"{label}{Ellipsis}", "Runway (e.g. 28R)", input => $"CVA {input}", context, host);
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

        return BuildInput($"{label}{Ellipsis}", "STAR name", input => $"JARR {input}", context, host);
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

        return BuildInput($"{spec.Label}{Ellipsis}", spec.Placeholder, input => $"{spec.Command} {input}", context, host);
    }

    /// <summary>
    /// The free-text companion of a <see cref="BuildOneManyOrInput"/> item, labelled "(other)" and offered beside one
    /// or more <paramref name="values"/>; null without any, where the item itself is the free text.
    /// </summary>
    private static MenuItem? BuildOtherInput(ValueItemSpec spec, IReadOnlyList<string> values, MenuContext context, IMenuHost host) =>
        values.Count > 0 ? BuildInput($"{spec.Label} (other){Ellipsis}", spec.Placeholder, input => $"{spec.Command} {input}", context, host) : null;

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
                    host.ShowInputPopup(bearingPrompt(fix), bearing => Send($"{command} {fix} {bearing}", context, host));
                    return Task.CompletedTask;
                },
                host
            );
        }

        return BuildInput(label, "FIX bearing", input => $"{command} {input}", context, host);
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
