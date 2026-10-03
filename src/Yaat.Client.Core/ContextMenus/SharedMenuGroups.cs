using Avalonia.Controls;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The groups more than one surface builds — the menu header (title, Command…, Note…), live traffic, track, squawk, ask
/// pilot, coordination, data block, favorites, the menu foot (warp, release to live feed, delete), and the flight groups
/// heading, altitude, speed, navigation, hold, approach, procedures, tower and pattern — assembled from
/// <see cref="MenuCatalog"/> entries. Only track, squawk and ask pilot keep a <see cref="MenuView"/> variant: the radar
/// carries input pickers (handoff, point out, squawk code, custom say) the other surfaces leave out when they build the
/// group themselves. The read-only tree a surface live-traffic shadow gets (<see cref="AddSurfaceShadow"/>) uses the radar's
/// Track on every view, so a shadow's handoff and point-out pickers are there on all three. Data block takes no view: its
/// entries are capability-gated, so a surface without the free-text popup shows fewer of them; the flight groups are the
/// radar's own, so they take no view either. The relative items (<see cref="AddRelative"/>) and
/// the ground view's pushback block, runway clearances, landing block, hold short and follow submenus and taxi-route block
/// are groups of their own, and its tower variants branch on <see cref="MenuContext.View"/> inside the entries. Whether a
/// group is offered at all stays with the caller. A group whose items are canvas-only in nature — the Display submenu
/// and its flat ground form — takes the surface's prebuilt items instead of the entries: the surface builds them with
/// <see cref="CanvasMenuItems"/> from its own canvas state and only the placing stays here.
/// </summary>
public static class SharedMenuGroups
{
    /// <summary>The Track submenu: track and drop, then the handoff and pointout items.</summary>
    public static MenuItem Track(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuView view)
    {
        var menu = new MenuItem { Header = "Track" };
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackTrack, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackDrop, aircraft, context, host));
        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackAcceptHandoff, aircraft, context, host));
        if (view == MenuView.Radar)
        {
            TryAdd(menu.Items, TryLeaf(MenuIds.TrackInitiateHandoff, aircraft, context, host));
        }

        TryAdd(menu.Items, TryLeaf(MenuIds.TrackCancelHandoff, aircraft, context, host));
        if (view == MenuView.Radar)
        {
            menu.Items.Add(new Separator());
            TryAdd(menu.Items, TryLeaf(MenuIds.TrackPointOut, aircraft, context, host));
        }

        TryAdd(menu.Items, TryLeaf(MenuIds.TrackAcknowledgePointout, aircraft, context, host));
        return menu;
    }

    /// <summary>The Squawk submenu: the code items, then Ident.</summary>
    public static MenuItem Squawk(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuView view)
    {
        var menu = new MenuItem { Header = "Squawk" };
        if (view == MenuView.Radar)
        {
            TryAdd(menu.Items, TryLeaf(MenuIds.SquawkCode, aircraft, context, host));
        }

        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkRandom, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkVfr, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkNormal, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkStandby, aircraft, context, host));
        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkIdent, aircraft, context, host));
        return menu;
    }

    /// <summary>The "Ask pilot to say..." submenu: the say queries, and on the radar a free-text one.</summary>
    public static MenuItem AskPilot(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuView view)
    {
        var menu = new MenuItem { Header = "Ask pilot to say..." };
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotAltitude, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotHeading, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotSpeed, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotMach, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotPosition, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotExpectedApproach, aircraft, context, host));
        if (view == MenuView.Radar)
        {
            menu.Items.Add(new Separator());
            TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotCustom, aircraft, context, host));
        }

        return menu;
    }

    /// <summary>The Coordination submenu: the departure-release items.</summary>
    public static MenuItem Coordination(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Coordination" };
        TryAdd(menu.Items, TryLeaf(MenuIds.CoordinationRelease, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.CoordinationHold, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.CoordinationRecall, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.CoordinationAcknowledge, aircraft, context, host));
        return menu;
    }

    /// <summary>The Data Block submenu: scratchpad, temporary altitude, cruise, then annotate. The note is the header's Note….</summary>
    public static MenuItem DataBlock(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Data Block" };
        TryAdd(menu.Items, TryLeaf(MenuIds.DataBlockScratchpad, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.DataBlockTempAltitude, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.DataBlockCruise, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.DataBlockAnnotate, aircraft, context, host));
        return menu;
    }

    /// <summary>
    /// The header every aircraft menu opens with: the bold, disabled title — the callsign and the type the aircraft
    /// filed (<see cref="IMenuAircraft.DisplayAircraftType"/>), or the bare callsign with no aircraft model or no type —
    /// then the rows under the title, the route summary (<see cref="RouteSummaryItem"/>) and the hold status
    /// (<see cref="HoldStatusItem"/>) the aircraft's own state raises, then the release items every view offers where
    /// they apply (Release (HFR) while the aircraft is held for release, then Check release window while it has a
    /// call-for-release window), then a separator, the free-text Command… and Note… (which ask the host for its
    /// flyouts), and a separator.
    /// </summary>
    public static void AddHeader(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        items.Add(
            new MenuItem
            {
                Header = HeaderTitle(aircraft, context.Callsign),
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        TryAdd(items, RouteSummaryItem(aircraft));
        TryAdd(items, HoldStatusItem(aircraft));

        AddIfApplicable(items, MenuIds.CoordinationReleaseHeld, aircraft, context, host);
        AddIfApplicable(items, MenuIds.CoordinationCheckReleaseWindow, aircraft, context, host);

        items.Add(new Separator());
        TryAdd(items, TryLeaf(MenuIds.AircraftCommand, aircraft, context, host));
        TryAdd(items, TryLeaf(MenuIds.AircraftNote, aircraft, context, host));
        items.Add(new Separator());
    }

    /// <summary>The header title: <c>{callsign} — {type}</c>, or the bare callsign when there is no aircraft model or no type.</summary>
    private static string HeaderTitle(IMenuAircraft? aircraft, string callsign) =>
        ((aircraft is null) || string.IsNullOrWhiteSpace(aircraft.DisplayAircraftType)) ? callsign : $"{callsign} — {aircraft.DisplayAircraftType}";

    /// <summary>
    /// The header's route summary row: the aircraft's route fix names from the fix it is navigating to on, joined with
    /// spaces — the first five, then an ellipsis when the route has more — with the whole run as the row's tooltip. The
    /// row is a disabled, dimmed label. Null without an aircraft, with no route, or when nothing is left to fly.
    /// </summary>
    private static MenuItem? RouteSummaryItem(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || (aircraft.NavigationRoute.Count == 0))
        {
            return null;
        }

        var fixes = new List<string>();
        bool started = string.IsNullOrEmpty(aircraft.NavigatingTo);
        foreach (string fix in aircraft.NavigationRoute)
        {
            if (!started && fix == aircraft.NavigatingTo)
            {
                started = true;
            }

            if (started)
            {
                fixes.Add(fix);
            }
        }

        if (fixes.Count == 0)
        {
            return null;
        }

        const int maxDisplay = 5;
        string displayFixes = fixes.Count > maxDisplay ? string.Join(" ", fixes.Take(maxDisplay)) + " ..." : string.Join(" ", fixes);
        string fullRoute = string.Join(" ", fixes);

        var item = new MenuItem
        {
            Header = displayFixes,
            IsEnabled = false,
            FontSize = 11,
            Opacity = 0.8,
        };
        ToolTip.SetTip(item, fullRoute);
        ToolTip.SetShowDelay(item, 0);
        return item;
    }

    /// <summary>
    /// The header's hold status row: "Held: position" for a hold in position, "Yielding to: {target}" for a give-way
    /// hold, then the auto-detected yield ("Following: {target} (auto-detected)" for a same-edge in-trail follow,
    /// "Yielding to: {target} (auto-detected)" for a converging one) and a bare "Held" otherwise. The row is a
    /// disabled, dimmed, italicised label. Null without an aircraft, or when it is under neither a hold nor a yield.
    /// </summary>
    private static MenuItem? HoldStatusItem(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || (!aircraft.IsHeld && string.IsNullOrEmpty(aircraft.AutoYieldTarget)))
        {
            return null;
        }

        string header = aircraft.HoldKind switch
        {
            "GiveWay" when !string.IsNullOrEmpty(aircraft.HoldYieldTarget) => $"Yielding to: {aircraft.HoldYieldTarget}",
            "HoldPosition" => "Held: position",
            _ when !string.IsNullOrEmpty(aircraft.AutoYieldTarget) && aircraft.AutoYieldIsFollowing =>
                $"Following: {aircraft.AutoYieldTarget} (auto-detected)",
            _ when !string.IsNullOrEmpty(aircraft.AutoYieldTarget) => $"Yielding to: {aircraft.AutoYieldTarget} (auto-detected)",
            _ => "Held",
        };

        return new MenuItem
        {
            Header = header,
            IsEnabled = false,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Opacity = 0.85,
        };
    }

    /// <summary>
    /// The foot every aircraft menu ends with, before the RPO items the caller appends: a separator (unless the menu
    /// already ends in one), Warp… where it applies (the radar, and never for a surface live-traffic shadow), "Release
    /// to live feed" when the aircraft was assumed from the feed (<see cref="AircraftCommandApplicability.CanUnassume"/>),
    /// then Delete. The aircraft list's delayed-spawn menu keeps its own foot (<see cref="AddDelayedSpawn"/>).
    /// </summary>
    public static void AddFoot(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        AddBlockSeparator(items);
        AddIfApplicable(items, MenuIds.SimControlWarp, aircraft, context, host);
        AddIfApplicable(items, MenuIds.LiveTrafficUnassume, aircraft, context, host);
        items.Add(Delete(aircraft, context, host));
    }

    /// <summary>The Delete leaf that sends <c>DEL</c>.</summary>
    private static MenuItem Delete(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Leaf(MenuIds.SimControlDelete, aircraft, context, host);

    /// <summary>The "Edit flight plan" leaf when the aircraft's flight plan is editable, otherwise null.</summary>
    public static MenuItem? EditFlightPlan(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        IsApplicable(MenuIds.AircraftEditFlightPlan, aircraft, context) ? TryLeaf(MenuIds.AircraftEditFlightPlan, aircraft, context, host) : null;

    /// <summary>
    /// The read-only tree a surface live-traffic shadow gets on every view: the track, data block and coordination
    /// groups, then <paramref name="display"/>, the surface's own Display submenu, and nothing that would command the
    /// aircraft. The track group is the radar's, so every view's shadow offers the radar's handoff and point-out
    /// pickers. It adds no foot and no RPO items; the caller appends those.
    /// </summary>
    public static void AddSurfaceShadow(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuItem display)
    {
        items.Add(Track(aircraft, context, host, MenuView.Radar));
        items.Add(DataBlock(aircraft, context, host));
        items.Add(Coordination(aircraft, context, host));
        items.Add(display);
    }

    /// <summary>
    /// Appends "Assume control" and "Assume and track" to <paramref name="items"/> when the aircraft is an assumable
    /// live-traffic shadow (<see cref="AircraftCommandApplicability.CanAssume"/>); otherwise adds nothing.
    /// </summary>
    public static void AddLiveTrafficAssume(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        foreach (string id in (string[])[MenuIds.LiveTrafficAssume, MenuIds.LiveTrafficAssumeAndTrack])
        {
            AddIfApplicable(items, id, aircraft, context, host);
        }
    }

    /// <summary>
    /// The Heading submenu: present heading, the heading pickers and the relative-turn pickers. The header names the
    /// fix the aircraft is navigating to, else its assigned magnetic heading.
    /// </summary>
    public static MenuItem Heading(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string header = aircraft switch
        {
            { NavigatingTo: { Length: > 0 } fix } => $"Heading (→ {fix})",
            { AssignedHeading: { } assigned } => $"Heading (→ {assigned.ToDisplayString()})",
            _ => "Heading",
        };
        var menu = new MenuItem { Header = header };
        TryAdd(menu.Items, TryLeaf(MenuIds.HeadingPresent, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HeadingFly, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HeadingTurnLeft, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HeadingTurnRight, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HeadingTurnLeftDegrees, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HeadingTurnRightDegrees, aircraft, context, host));
        return menu;
    }

    /// <summary>The Altitude submenu: the Maintain picker, under a header naming the assigned altitude.</summary>
    public static MenuItem Altitude(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string header = aircraft?.AssignedAltitude is { } assigned ? $"Altitude (→ {MenuCatalog.FormatAltitude((int)assigned)})" : "Altitude";
        var menu = new MenuItem { Header = header };
        AddIfBuilt(menu.Items, MenuIds.AltitudeMaintain, aircraft, context, host);
        return menu;
    }

    /// <summary>
    /// The Speed submenu: the speed picker and input, resume normal speed and final approach speed, under a header
    /// naming the assigned speed.
    /// </summary>
    public static MenuItem Speed(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        double? assigned = aircraft?.AssignedSpeed;
        var menu = new MenuItem { Header = assigned is > 0 ? $"Speed (→ {assigned.Value:F0})" : "Speed" };
        TryAdd(menu.Items, TryLeaf(MenuIds.SpeedAssign, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SpeedCustom, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SpeedNormal, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SpeedFinalApproach, aircraft, context, host));
        return menu;
    }

    /// <summary>
    /// The Navigation submenu: direct to, and while the aircraft is navigating to a fix (named in the header) append
    /// direct to.
    /// </summary>
    public static MenuItem Navigation(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string header = aircraft is { NavigatingTo.Length: > 0 } ? $"Navigation (→ {aircraft.NavigatingTo})" : "Navigation";
        var menu = new MenuItem { Header = header };
        TryAdd(menu.Items, TryLeaf(MenuIds.NavigationDirectTo, aircraft, context, host));
        AddIfApplicable(menu.Items, MenuIds.NavigationAppendDirectTo, aircraft, context, host);
        return menu;
    }

    /// <summary>The Hold submenu: hold at present position, then hold at a fix, each with left and right turns.</summary>
    public static MenuItem Hold(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Hold" };
        TryAdd(menu.Items, TryLeaf(MenuIds.HoldPresentLeft, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HoldPresentRight, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HoldFixLeft, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HoldFixRight, aircraft, context, host));
        return menu;
    }

    /// <summary>
    /// The Approach submenu: the approach clearances, the visual approach (with its "(other)" runway picker beside a
    /// default runway), the in-sight requests, then the Report when… submenu. The header names the active approach,
    /// else the expected one.
    /// </summary>
    public static MenuItem Approach(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string header = aircraft switch
        {
            { ActiveApproachId: { Length: > 0 } active } => $"Approach ({active})",
            { ExpectedApproach: { Length: > 0 } expected } => $"Approach (exp: {expected})",
            _ => "Approach",
        };
        var menu = new MenuItem { Header = header };
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachCleared, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachJoin, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachClearedStraightIn, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachJoinStraightIn, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachClearedForce, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachJoinForce, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachJoinFinalCourse, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachExpect, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachClearedVisual, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildClearedVisualOther(aircraft, context, host));

        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportFieldInSight, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportTrafficInSight, aircraft, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(ReportWhen(aircraft, context, host));
        return menu;
    }

    /// <summary>The Report when… submenu: the turn and position reports, then the Stop reporting submenu.</summary>
    private static MenuItem ReportWhen(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Report when…" };
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportBase, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportFinal, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportCrosswind, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportDownwind, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportNMileFinal, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ApproachReportAtFix, aircraft, context, host));

        var stop = new MenuItem { Header = "Stop reporting" };
        TryAdd(stop.Items, TryLeaf(MenuIds.ApproachReportOffBase, aircraft, context, host));
        TryAdd(stop.Items, TryLeaf(MenuIds.ApproachReportOffFinal, aircraft, context, host));
        TryAdd(stop.Items, TryLeaf(MenuIds.ApproachReportOffCrosswind, aircraft, context, host));
        TryAdd(stop.Items, TryLeaf(MenuIds.ApproachReportOffDownwind, aircraft, context, host));
        stop.Items.Add(new Separator());
        TryAdd(stop.Items, TryLeaf(MenuIds.ApproachReportOffAll, aircraft, context, host));

        menu.Items.Add(new Separator());
        menu.Items.Add(stop);
        return menu;
    }

    /// <summary>
    /// The Procedures submenu: join STAR (with its "(other)" STAR picker beside a filed STAR), climb via SID and
    /// descend via STAR, cross and depart fix and join airway (each with its "(other)" free text beside the values it
    /// offers), PTAC, then the join-radial items.
    /// </summary>
    public static MenuItem Procedures(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Procedures" };
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinStar, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildJoinStarOther(aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresClimbViaSid, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresDescendViaStar, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresCrossFix, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildCrossFixOther(aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresDepartFix, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildDepartFixOther(aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresPtac, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinAirway, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildJoinAirwayOther(aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinRadialOutbound, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinRadialInbound, aircraft, context, host));
        return menu;
    }

    /// <summary>
    /// The Tower submenu, state-aware: the departure clearances, then the landing and option clearances with go around
    /// and cancel landing clearance while any of cleared to land, go around or cancel landing clearance applies, then
    /// the runway exits after touchdown, each block after a separator when items precede it. Null when nothing applies,
    /// so the caller omits the submenu.
    /// </summary>
    public static MenuItem? Tower(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Tower" };
        AddIfApplicable(menu.Items, MenuIds.TowerLineUpAndWait, aircraft, context, host);
        AddIfApplicable(menu.Items, MenuIds.TowerClearedForTakeoff, aircraft, context, host);
        AddIfApplicable(menu.Items, MenuIds.TowerCancelTakeoff, aircraft, context, host);

        bool landing =
            (IsApplicable(MenuIds.TowerClearedToLand, aircraft, context))
            || (IsApplicable(MenuIds.TowerGoAround, aircraft, context))
            || (IsApplicable(MenuIds.TowerCancelLanding, aircraft, context));
        if (landing)
        {
            AddSeparatorIfNonEmpty(menu.Items);
            foreach (string id in LandingIds)
            {
                AddIfApplicable(menu.Items, id, aircraft, context, host);
            }
        }

        if (IsApplicable(MenuIds.TowerExitLeft, aircraft, context))
        {
            AddSeparatorIfNonEmpty(menu.Items);
            TryAdd(menu.Items, TryLeaf(MenuIds.TowerExitLeft, aircraft, context, host));
            AddIfApplicable(menu.Items, MenuIds.TowerExitRight, aircraft, context, host);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>The landing block, in menu order on every view.</summary>
    private static readonly string[] LandingIds =
    [
        MenuIds.TowerClearedToLand,
        MenuIds.TowerForceLanding,
        MenuIds.TowerClearedOption,
        MenuIds.TowerTouchAndGo,
        MenuIds.TowerStopAndGo,
        MenuIds.TowerLowApproach,
        MenuIds.TowerGoAround,
        MenuIds.TowerCancelLanding,
    ];

    /// <summary>
    /// The Pattern submenu, state-aware: the pattern entries that apply, each with its "(other)" runway picker beside an
    /// assigned runway, then the leg turns, the spacing adjustments, the orbits and Circle airport, each block after a
    /// separator when it has an item and items precede it. Null when nothing applies, so the caller omits the submenu.
    /// </summary>
    public static MenuItem? Pattern(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Pattern" };
        foreach (string id in PatternEntryIds)
        {
            if (IsApplicable(id, aircraft, context))
            {
                TryAdd(menu.Items, TryLeaf(id, aircraft, context, host));
                AddCompanion(menu.Items, MenuCatalog.BuildPatternEntryOther(id, aircraft, context, host));
            }
        }

        foreach (string[] block in PatternManeuverBlocks)
        {
            AddBlockIfAnyApplies(menu.Items, block, aircraft, context, host);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>The Pattern submenu's entries, in menu order.</summary>
    private static readonly string[] PatternEntryIds =
    [
        MenuIds.PatternEnterLeftDownwind,
        MenuIds.PatternEnterRightDownwind,
        MenuIds.PatternEnterLeftBase,
        MenuIds.PatternEnterRightBase,
        MenuIds.PatternEnterFinal,
    ];

    /// <summary>The Pattern submenu's maneuver blocks, in menu order: leg turns, spacing, orbits, then Circle airport.</summary>
    private static readonly string[][] PatternManeuverBlocks =
    [
        [MenuIds.PatternTurnCrosswind, MenuIds.PatternTurnDownwind, MenuIds.PatternTurnBase],
        [MenuIds.PatternExtend, MenuIds.PatternShortApproach, MenuIds.PatternNormalApproach],
        [
            MenuIds.PatternLeft360,
            MenuIds.PatternRight360,
            MenuIds.PatternLeft270,
            MenuIds.PatternRight270,
            MenuIds.PatternPlan270,
            MenuIds.PatternCancel270,
        ],
        [MenuIds.PatternCircleAirport],
    ];

    /// <summary>Adds the applicable entries of <paramref name="ids"/> after a separator when items precede them; nothing when none applies.</summary>
    private static void AddBlockIfAnyApplies(ItemCollection items, string[] ids, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!ids.Any(id => IsApplicable(id, aircraft, context)))
        {
            return;
        }

        AddSeparatorIfNonEmpty(items);
        foreach (string id in ids)
        {
            AddIfApplicable(items, id, aircraft, context, host);
        }
    }

    private static bool IsApplicable(string id, IMenuAircraft? aircraft, MenuContext context) => MenuCatalog.Get(id).IsApplicable(aircraft, context);

    /// <summary>
    /// Adds the entry's item when the entry applies to the aircraft, otherwise nothing, and returns whether it added one;
    /// the ground view places its single entries with it between its groups.
    /// </summary>
    public static bool AddIfApplicable(ItemCollection items, string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!IsApplicable(id, aircraft, context))
        {
            return false;
        }

        if (TryLeaf(id, aircraft, context, host) is not { } item)
        {
            return false;
        }

        items.Add(item);
        return true;
    }

    /// <summary>
    /// The relative items while another aircraft is selected (<see cref="MenuContext.PreviousSelection"/>): a bold
    /// header naming the selected aircraft, the pair that applies — report in sight, then follow once the selected
    /// aircraft has reported the right-clicked one in sight, for an airborne pair
    /// (<see cref="RelativeTraffic.OffersAirborneRelative"/>); give way, then follow, for a ground pair
    /// (<see cref="RelativeTraffic.OffersGroundRelative"/>) — and a trailing separator. Adds nothing when there is no
    /// previous selection or neither pair applies, so a mixed pair gets no header either.
    /// </summary>
    public static void AddRelative(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (context.PreviousSelection is not { } selected)
        {
            return;
        }

        bool airborne = RelativeTraffic.OffersAirborneRelative(aircraft, context);
        if (!airborne && !RelativeTraffic.OffersGroundRelative(aircraft, context))
        {
            return;
        }

        items.Add(
            new MenuItem
            {
                Header = $"↪ {selected.Callsign}:",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        if (airborne)
        {
            TryAdd(items, TryLeaf(MenuIds.RelativeReportInSight, aircraft, context, host));
            AddIfApplicable(items, MenuIds.RelativeFollow, aircraft, context, host);
        }
        else
        {
            TryAdd(items, TryLeaf(MenuIds.GroundRelativeGiveWay, aircraft, context, host));
            TryAdd(items, TryLeaf(MenuIds.GroundRelativeFollow, aircraft, context, host));
        }

        items.Add(new Separator());
    }

    /// <summary>
    /// The ground view's runway clearances that apply, in the ground's order: resume taxi (from a hold-short or a
    /// stationary hold), cross the held runway, line up and wait, Cleared for takeoff, then cancel takeoff clearance
    /// while lined up and waiting. Cancel takeoff while rolling comes after the landing items instead
    /// (<see cref="AddGroundLanding"/>). The caller builds the context with <see cref="MenuView.Ground"/>.
    /// </summary>
    public static void AddGroundClearances(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        foreach (string id in GroundClearanceIds)
        {
            AddIfApplicable(items, id, aircraft, context, host);
        }

        if ((aircraft?.CurrentPhase ?? "") == "LinedUpAndWaiting")
        {
            AddIfApplicable(items, MenuIds.TowerCancelTakeoff, aircraft, context, host);
        }
    }

    /// <summary>The ground view's runway clearances, in menu order.</summary>
    private static readonly string[] GroundClearanceIds =
    [
        MenuIds.GroundResumeTaxi,
        MenuIds.GroundCrossRunway,
        MenuIds.TowerLineUpAndWait,
        MenuIds.TowerClearedForTakeoff,
    ];

    /// <summary>
    /// The ground view's landing items and runway exits, flat (<see cref="AddFlatLandingAndExits"/>); then cancel
    /// takeoff clearance while rolling.
    /// </summary>
    public static void AddGroundLanding(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        AddFlatLandingAndExits(items, aircraft, context, host);
        if ((aircraft?.CurrentPhase ?? "") == "Takeoff")
        {
            AddIfApplicable(items, MenuIds.TowerCancelTakeoff, aircraft, context, host);
        }
    }

    /// <summary>
    /// The ground view's display items, flat in the order the view built them
    /// (<c>GroundView.BuildCanvasItems</c>, which reads the ground view model's data-block, taxi-route and measure
    /// state). An item the view's state hid is null and adds nothing.
    /// </summary>
    public static void AddGroundDisplay(ItemCollection items, IReadOnlyList<MenuItem?> viewItems)
    {
        foreach (MenuItem? item in viewItems)
        {
            TryAdd(items, item);
        }
    }

    /// <summary>
    /// The ground view's Hold short of… submenu while the aircraft is taxiing
    /// (<see cref="AircraftCommandApplicability.CanHoldShort"/>) and its route offers a target; otherwise nothing.
    /// </summary>
    public static void AddGroundHoldShort(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        AddIfApplicableAndBuilt(items, MenuIds.GroundHoldShort, aircraft, context, host);

    /// <summary>
    /// The ground view's Follow… and Give way to… submenus at <paramref name="position"/>, which offers them only for
    /// its own phases: <see cref="GroundFollowPosition.Parking"/> for an aircraft at parking,
    /// <see cref="GroundFollowPosition.Taxi"/> for a taxiing one, <see cref="GroundFollowPosition.Hold"/> for the
    /// three stationary holds. Each submenu is added while its predicate
    /// (<see cref="AircraftCommandApplicability.CanFollowBehind"/>, <see cref="AircraftCommandApplicability.CanGiveWayTo"/>)
    /// allows it and there is other ground traffic, so the parking position never offers Give way to….
    /// </summary>
    public static void AddGroundFollowAndGiveWay(
        ItemCollection items,
        IMenuAircraft? aircraft,
        MenuContext context,
        IMenuHost host,
        GroundFollowPosition position
    )
    {
        if (!IsAtFollowPosition(aircraft, position))
        {
            return;
        }

        AddIfApplicableAndBuilt(items, MenuIds.GroundFollow, aircraft, context, host);
        AddIfApplicableAndBuilt(items, MenuIds.GroundGiveWay, aircraft, context, host);
    }

    /// <summary>
    /// The ground view's phase-aware command items, in the ground's order: the relative items
    /// (<see cref="AddRelative"/>), the pushback block, hold position, hold short, the taxi position's follow and give
    /// way, break conflict, the runway clearances, the hold position's follow and give way, the landing items, then the
    /// taxi-route block. Each adds nothing when it does not apply.
    /// </summary>
    public static void AddGroundAircraftCommands(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        AddRelative(items, aircraft, context, host);
        AddGroundPushback(items, aircraft, context, host);

        // The single emission for the whole HOLD window, taxi-follow phases included — those emit
        // nothing of their own before this item, so it stays the first item they show.
        AddIfApplicable(items, MenuIds.GroundHoldPosition, aircraft, context, host);
        AddGroundHoldShort(items, aircraft, context, host);
        AddGroundFollowAndGiveWay(items, aircraft, context, host, GroundFollowPosition.Taxi);
        AddIfApplicable(items, MenuIds.GroundBreakConflict, aircraft, context, host);
        AddGroundClearances(items, aircraft, context, host);
        AddGroundFollowAndGiveWay(items, aircraft, context, host, GroundFollowPosition.Hold);
        AddGroundLanding(items, aircraft, context, host);
        AddGroundTaxiRoutes(items, aircraft, context, host);
    }

    /// <summary>
    /// The ground view's pushback block while the aircraft can push back (<see cref="AircraftCommandApplicability.CanPushBack"/>):
    /// Push back, the flat face items (<see cref="MenuCatalog.BuildPushbackFaces"/>), Push back to… when the host
    /// answers a stand, Push route…, then the parking position's Follow…
    /// (<see cref="GroundFollowPosition.Parking"/>). Holding After Pushback gets its follow submenus from the hold
    /// position after the clearances instead. Adds nothing otherwise.
    /// </summary>
    public static void AddGroundPushback(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!AddIfApplicable(items, MenuIds.GroundPushback, aircraft, context, host))
        {
            return;
        }

        if (IsApplicable(MenuIds.GroundPushbackFace, aircraft, context))
        {
            AddRange(items, MenuCatalog.BuildPushbackFaces(context, host));
        }

        AddIfApplicableAndBuilt(items, MenuIds.GroundPushbackTo, aircraft, context, host);
        AddIfApplicable(items, MenuIds.GroundPushRoute, aircraft, context, host);
        AddGroundFollowAndGiveWay(items, aircraft, context, host, GroundFollowPosition.Parking);
    }

    /// <summary>
    /// The ground view's taxi-route block while a taxi route can be drawn (<see cref="AircraftCommandApplicability.CanDrawTaxiRoute"/>):
    /// a separator, the Preset taxi route submenu when a preset applies, then Draw taxi route…. Adds nothing otherwise.
    /// </summary>
    public static void AddGroundTaxiRoutes(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!IsApplicable(MenuIds.GroundDrawTaxiRoute, aircraft, context))
        {
            return;
        }

        items.Add(new Separator());
        AddIfApplicableAndBuilt(items, MenuIds.GroundTaxiPreset, aircraft, context, host);
        AddIfApplicable(items, MenuIds.GroundDrawTaxiRoute, aircraft, context, host);
    }

    /// <summary>Whether the aircraft's phase is one <paramref name="position"/> places the follow submenus for.</summary>
    private static bool IsAtFollowPosition(IMenuAircraft? aircraft, GroundFollowPosition position)
    {
        string phase = aircraft?.CurrentPhase ?? "";
        return position switch
        {
            GroundFollowPosition.Parking => phase == "At Parking",
            GroundFollowPosition.Taxi => phase == "Taxiing",
            GroundFollowPosition.Hold => phase is "Holding In Position" or "Holding After Exit" or "Holding After Pushback",
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown ground follow position"),
        };
    }

    /// <summary>Adds the entry's item when the entry applies and the surface's state gives it something to show; otherwise nothing.</summary>
    private static void AddIfApplicableAndBuilt(ItemCollection items, string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (IsApplicable(id, aircraft, context))
        {
            AddIfBuilt(items, id, aircraft, context, host);
        }
    }

    /// <summary>
    /// The aircraft list's phase-aware command items, in the list's order: the relative items
    /// (<see cref="AddRelative"/>) while another row was selected at the right-click, as the ground's command block
    /// opens with them; the ground-movement block (push back, hold position, resume taxi and cross the held runway) for
    /// an on-ground aircraft, then line up and wait, Cleared for takeoff and cancel takeoff clearance, then the landing
    /// items and the runway exits. The landing block and the exits come from <see cref="AddFlatLandingAndExits"/>. The
    /// caller builds the context with <see cref="MenuView.List"/>. Each item adds nothing when it does not apply.
    /// </summary>
    public static void AddListAircraftCommands(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        AddRelative(items, aircraft, context, host);
        if (aircraft?.IsOnGround == true)
        {
            AddIfApplicable(items, MenuIds.GroundPushback, aircraft, context, host);
            AddIfApplicable(items, MenuIds.GroundHoldPosition, aircraft, context, host);
            AddIfApplicable(items, MenuIds.GroundResumeTaxi, aircraft, context, host);
            AddIfApplicable(items, MenuIds.GroundCrossRunway, aircraft, context, host);
        }

        AddIfApplicable(items, MenuIds.TowerLineUpAndWait, aircraft, context, host);
        AddIfApplicable(items, MenuIds.TowerClearedForTakeoff, aircraft, context, host);
        AddIfApplicable(items, MenuIds.TowerCancelTakeoff, aircraft, context, host);
        AddFlatLandingAndExits(items, aircraft, context, host);
    }

    /// <summary>
    /// The landing items in the Tower submenu's order (<see cref="LandingIds"/>) while any of cleared to land, go around
    /// or cancel landing clearance applies, then the runway exits, for the flat menus of the ground view and the
    /// aircraft list. Each block opens with a separator when items precede it (<see cref="AddBlockSeparator"/>), as the
    /// Tower submenu separates its blocks.
    /// </summary>
    private static void AddFlatLandingAndExits(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        bool landing =
            (IsApplicable(MenuIds.TowerClearedToLand, aircraft, context))
            || (IsApplicable(MenuIds.TowerGoAround, aircraft, context))
            || (IsApplicable(MenuIds.TowerCancelLanding, aircraft, context));
        if (landing)
        {
            AddBlockSeparator(items);
            foreach (string id in LandingIds)
            {
                AddIfApplicable(items, id, aircraft, context, host);
            }
        }

        if ((IsApplicable(MenuIds.TowerExitLeft, aircraft, context)) || (IsApplicable(MenuIds.TowerExitRight, aircraft, context)))
        {
            AddBlockSeparator(items);
            AddIfApplicable(items, MenuIds.TowerExitLeft, aircraft, context, host);
            AddIfApplicable(items, MenuIds.TowerExitRight, aircraft, context, host);
        }
    }

    /// <summary>
    /// The aircraft list's delayed-spawn items, in the list's order: Spawn now, the Change spawn delay submenu and
    /// Delete. The caller places them in place of the phase-aware command groups when the aircraft is a delayed spawn.
    /// The submenu's free-text delay box closes the menu it sits in, so the submenu comes from
    /// <see cref="MenuCatalog.BuildSpawnDelay"/> rather than from its own entry's builder.
    /// </summary>
    public static void AddDelayedSpawn(ContextMenu menu, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        TryAdd(menu.Items, TryLeaf(MenuIds.SpawnNow, aircraft, context, host));
        if (IsApplicable(MenuIds.SpawnDelay, aircraft, context))
        {
            menu.Items.Add(MenuCatalog.BuildSpawnDelay(menu, context, host));
        }

        menu.Items.Add(Delete(aircraft, context, host));
    }

    /// <summary>
    /// The aircraft list's multi-selection live-traffic item, placed after the Delete item: a separator and
    /// "Assume selected live traffic (N)" when the click's selection (<see cref="MenuClick.Selection"/>) holds two or
    /// more assumable shadows (<see cref="AircraftCommandApplicability.CanAssume"/>) to assume at once and the entry's
    /// own gate allows it, otherwise nothing. The gate — read through <see cref="MenuCatalog.Get"/> the way
    /// <see cref="AddDelayedSpawn"/> reads the delayed spawn's — stays with the entry. The item itself comes from
    /// <see cref="MenuCatalog.BuildAssumeSelected"/> rather than from its own entry's builder.
    /// </summary>
    public static void AddAssumeSelected(ContextMenu menu, MenuContext context, IMenuHost host)
    {
        List<string> selectedShadows = [.. context.Click.Selection.Where(AircraftCommandApplicability.CanAssume).Select(a => a.Callsign)];
        if (
            (selectedShadows.Count < 2)
            || !IsApplicable(MenuIds.LiveTrafficAssumeSelected, null, context)
            || !MenuCatalog.Serves(MenuIds.LiveTrafficAssumeSelected, host)
        )
        {
            return;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCatalog.BuildAssumeSelected(selectedShadows, host));
    }

    private static void AddSeparatorIfNonEmpty(ItemCollection items)
    {
        if (items.Count > 0)
        {
            items.Add(new Separator());
        }
    }

    /// <summary>Opens a block in a flat menu: a separator, unless the menu is empty or already ends in one.</summary>
    internal static void AddBlockSeparator(ItemCollection items)
    {
        if ((items.Count > 0) && (items[items.Count - 1] is not Separator))
        {
            items.Add(new Separator());
        }
    }

    /// <summary>Adds every one of <paramref name="controls"/>, in order.</summary>
    internal static void AddRange(ItemCollection items, IEnumerable<Control> controls)
    {
        foreach (Control control in controls)
        {
            items.Add(control);
        }
    }

    /// <summary>Adds an entry's "(other)" companion item, or nothing when the entry offers none (the item is null).</summary>
    private static void AddCompanion(ItemCollection items, MenuItem? companion)
    {
        if (companion is not null)
        {
            items.Add(companion);
        }
    }

    /// <summary>The Favorite Commands submenu, which the catalog entry has the host build.</summary>
    public static MenuItem Favorites(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Leaf(MenuIds.FavoritesMenu, aircraft, context, host);

    /// <summary>
    /// The entry's item when the host serves it and it built one; null when the host lacks any capability the entry
    /// requires, or when the entry's own state hides it.
    /// </summary>
    private static MenuItem? TryLeaf(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        return MenuCatalog.CanServe(entry, host) ? entry.Build(aircraft, context, host) : null;
    }

    /// <summary>The entry's item, which must exist: for the entries that require no capability (Delete, Favorites).</summary>
    private static MenuItem Leaf(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        TryLeaf(id, aircraft, context, host) ?? throw new InvalidOperationException($"The context-menu catalog entry '{id}' built no menu item.");

    /// <summary>Adds <paramref name="item"/>, or nothing when it is null.</summary>
    private static void TryAdd(ItemCollection items, MenuItem? item)
    {
        if (item is not null)
        {
            items.Add(item);
        }
    }

    /// <summary>
    /// Adds the entry's item, or nothing when the surface cannot serve it (<see cref="MenuCatalog.CanServe"/>) or its
    /// own state hides it (the item is null).
    /// </summary>
    private static void AddIfBuilt(ItemCollection items, string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        TryAdd(items, TryLeaf(id, aircraft, context, host));
}
