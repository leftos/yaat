using Avalonia.Controls;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The groups more than one surface builds — live traffic, track, squawk, ask pilot, coordination, data block,
/// sim control, display, favorites, and the flight groups heading, altitude, speed, navigation (with Draw route),
/// hold, approach, procedures, tower and pattern — assembled from <see cref="MenuCatalog"/> entries. Only track, squawk and ask pilot keep a
/// <see cref="MenuView"/> variant: the radar carries input pickers (handoff, point out, squawk code, custom say) the
/// other surfaces leave out, and sends <c>ID</c> for Ident where the others send <c>IDENT</c>. Data block, sim
/// control, display and the flight groups are built by the radar today, so they take no view. The ground view's
/// relative items, runway clearances and landing block are flat groups of their own, and its tower variants branch on
/// <see cref="MenuContext.View"/> inside the entries. Whether a group is offered at all stays with the caller.
/// </summary>
public static class SharedMenuGroups
{
    /// <summary>The Track submenu: track and drop, then the handoff and pointout items.</summary>
    public static MenuItem Track(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuView view)
    {
        var menu = new MenuItem { Header = "Track" };
        menu.Items.Add(Leaf(MenuIds.TrackTrack, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.TrackDrop, aircraft, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(Leaf(MenuIds.TrackAcceptHandoff, aircraft, context, host));
        if (view == MenuView.Radar)
        {
            menu.Items.Add(Leaf(MenuIds.TrackInitiateHandoff, aircraft, context, host));
        }

        menu.Items.Add(Leaf(MenuIds.TrackCancelHandoff, aircraft, context, host));
        if (view == MenuView.Radar)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Leaf(MenuIds.TrackPointOut, aircraft, context, host));
        }

        menu.Items.Add(Leaf(MenuIds.TrackAcknowledgePointout, aircraft, context, host));
        return menu;
    }

    /// <summary>The Squawk submenu: the code items, then Ident.</summary>
    public static MenuItem Squawk(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuView view)
    {
        var menu = new MenuItem { Header = "Squawk" };
        if (view == MenuView.Radar)
        {
            menu.Items.Add(Leaf(MenuIds.SquawkCode, aircraft, context, host));
        }

        menu.Items.Add(Leaf(MenuIds.SquawkRandom, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.SquawkVfr, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.SquawkNormal, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.SquawkStandby, aircraft, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(
            view == MenuView.Radar
                ? MenuCatalog.BuildSend(MenuCatalog.Get(MenuIds.SquawkIdent).Label, "ID", context, host)
                : Leaf(MenuIds.SquawkIdent, aircraft, context, host)
        );
        return menu;
    }

    /// <summary>The "Ask pilot to say..." submenu: the say queries, and on the radar a free-text one.</summary>
    public static MenuItem AskPilot(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, MenuView view)
    {
        var menu = new MenuItem { Header = "Ask pilot to say..." };
        menu.Items.Add(Leaf(MenuIds.AskPilotAltitude, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.AskPilotHeading, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.AskPilotSpeed, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.AskPilotMach, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.AskPilotPosition, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.AskPilotExpectedApproach, aircraft, context, host));
        if (view == MenuView.Radar)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Leaf(MenuIds.AskPilotCustom, aircraft, context, host));
        }

        return menu;
    }

    /// <summary>The Coordination submenu: the departure-release items.</summary>
    public static MenuItem Coordination(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Coordination" };
        menu.Items.Add(Leaf(MenuIds.CoordinationRelease, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.CoordinationHold, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.CoordinationRecall, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.CoordinationAcknowledge, aircraft, context, host));
        return menu;
    }

    /// <summary>The Data Block submenu: scratchpad, note, temporary altitude, cruise, then annotate.</summary>
    public static MenuItem DataBlock(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Data Block" };
        menu.Items.Add(Leaf(MenuIds.DataBlockScratchpad, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DataBlockNote, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DataBlockTempAltitude, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DataBlockCruise, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DataBlockAnnotate, aircraft, context, host));
        return menu;
    }

    /// <summary>The Sim Control submenu: Warp, the release-to-live-feed item when it applies, then Delete.</summary>
    public static MenuItem SimControl(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Sim Control" };
        menu.Items.Add(Leaf(MenuIds.SimControlWarp, aircraft, context, host));
        if (Unassume(aircraft, context, host) is { } unassume)
        {
            menu.Items.Add(unassume);
        }

        menu.Items.Add(Leaf(MenuIds.SimControlDelete, aircraft, context, host));
        return menu;
    }

    /// <summary>The Delete leaf that sends <c>DEL</c>, which each surface places itself.</summary>
    public static MenuItem Delete(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Leaf(MenuIds.SimControlDelete, aircraft, context, host);

    /// <summary>The "Edit flight plan" leaf when the aircraft's flight plan is editable, otherwise null.</summary>
    public static MenuItem? EditFlightPlan(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        IsApplicable(MenuIds.AircraftEditFlightPlan, aircraft, context) ? Leaf(MenuIds.AircraftEditFlightPlan, aircraft, context, host) : null;

    /// <summary>
    /// The Display submenu: the data-block form and position, the nav route and the measurement in progress, then the
    /// leader-direction, J-ring and cone overlays, then blank and unblank. The data-block reset and the measure items
    /// come and go with the surface's own state, so each is added only when it built an item.
    /// </summary>
    public static MenuItem Display(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Display" };
        AddIfBuilt(menu.Items, MenuIds.DisplayMiniDataBlock, aircraft, context, host);
        AddIfBuilt(menu.Items, MenuIds.DisplayResetDataBlockPosition, aircraft, context, host);
        AddIfBuilt(menu.Items, MenuIds.DisplayNavRoute, aircraft, context, host);
        AddIfBuilt(menu.Items, MenuIds.DisplayMeasure, aircraft, context, host);
        menu.Items.Add(new Separator());
        menu.Items.Add(Leaf(MenuIds.DisplayLeaderDirection, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DisplayJRing, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DisplayCone, aircraft, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(Leaf(MenuIds.DisplayBlank, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.DisplayUnblank, aircraft, context, host));
        return menu;
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
    /// "Release to live feed" when the aircraft was assumed from the feed
    /// (<see cref="AircraftCommandApplicability.CanUnassume"/>), otherwise null.
    /// </summary>
    public static MenuItem? Unassume(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        IsApplicable(MenuIds.LiveTrafficUnassume, aircraft, context) ? Leaf(MenuIds.LiveTrafficUnassume, aircraft, context, host) : null;

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
        menu.Items.Add(Leaf(MenuIds.HeadingPresent, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HeadingFly, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HeadingTurnLeft, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HeadingTurnRight, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HeadingTurnLeftDegrees, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HeadingTurnRightDegrees, aircraft, context, host));
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
        menu.Items.Add(Leaf(MenuIds.SpeedAssign, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.SpeedCustom, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.SpeedNormal, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.SpeedFinalApproach, aircraft, context, host));
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
        menu.Items.Add(Leaf(MenuIds.NavigationDirectTo, aircraft, context, host));
        AddIfApplicable(menu.Items, MenuIds.NavigationAppendDirectTo, aircraft, context, host);
        return menu;
    }

    /// <summary>The Draw route leaf, which each surface places itself.</summary>
    public static MenuItem DrawRoute(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Leaf(MenuIds.NavigationDrawRoute, aircraft, context, host);

    /// <summary>The Hold submenu: hold at present position, then hold at a fix, each with left and right turns.</summary>
    public static MenuItem Hold(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Hold" };
        menu.Items.Add(Leaf(MenuIds.HoldPresentLeft, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HoldPresentRight, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HoldFixLeft, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.HoldFixRight, aircraft, context, host));
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
        menu.Items.Add(Leaf(MenuIds.ApproachCleared, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachJoin, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachClearedStraightIn, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachJoinStraightIn, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachClearedForce, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachJoinForce, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachJoinFinalCourse, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachExpect, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachClearedVisual, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildClearedVisualOther(aircraft, context, host));

        menu.Items.Add(new Separator());
        menu.Items.Add(Leaf(MenuIds.ApproachReportFieldInSight, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachReportTrafficInSight, aircraft, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(ReportWhen(aircraft, context, host));
        return menu;
    }

    /// <summary>The Report when… submenu: the turn and position reports, then the Stop reporting submenu.</summary>
    private static MenuItem ReportWhen(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Report when…" };
        menu.Items.Add(Leaf(MenuIds.ApproachReportBase, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachReportFinal, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachReportCrosswind, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachReportDownwind, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachReportNMileFinal, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ApproachReportAtFix, aircraft, context, host));

        var stop = new MenuItem { Header = "Stop reporting" };
        stop.Items.Add(Leaf(MenuIds.ApproachReportOffBase, aircraft, context, host));
        stop.Items.Add(Leaf(MenuIds.ApproachReportOffFinal, aircraft, context, host));
        stop.Items.Add(Leaf(MenuIds.ApproachReportOffCrosswind, aircraft, context, host));
        stop.Items.Add(Leaf(MenuIds.ApproachReportOffDownwind, aircraft, context, host));
        stop.Items.Add(new Separator());
        stop.Items.Add(Leaf(MenuIds.ApproachReportOffAll, aircraft, context, host));

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
        menu.Items.Add(Leaf(MenuIds.ProceduresJoinStar, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildJoinStarOther(aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresClimbViaSid, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresDescendViaStar, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresCrossFix, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildCrossFixOther(aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresDepartFix, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildDepartFixOther(aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresPtac, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresJoinAirway, aircraft, context, host));
        AddCompanion(menu.Items, MenuCatalog.BuildJoinAirwayOther(aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresJoinRadialOutbound, aircraft, context, host));
        menu.Items.Add(Leaf(MenuIds.ProceduresJoinRadialInbound, aircraft, context, host));
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
            foreach (string id in TowerLandingIds)
            {
                AddIfApplicable(menu.Items, id, aircraft, context, host);
            }
        }

        if (IsApplicable(MenuIds.TowerExitLeft, aircraft, context))
        {
            AddSeparatorIfNonEmpty(menu.Items);
            menu.Items.Add(Leaf(MenuIds.TowerExitLeft, aircraft, context, host));
            AddIfApplicable(menu.Items, MenuIds.TowerExitRight, aircraft, context, host);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>The Tower submenu's landing block, in menu order.</summary>
    private static readonly string[] TowerLandingIds =
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
                menu.Items.Add(Leaf(id, aircraft, context, host));
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
    /// the ground view places its single entries with it between the submenus it still builds itself.
    /// </summary>
    public static bool AddIfApplicable(ItemCollection items, string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!IsApplicable(id, aircraft, context))
        {
            return false;
        }

        items.Add(Leaf(id, aircraft, context, host));
        return true;
    }

    /// <summary>
    /// The ground view's relative items while another on-ground aircraft is selected
    /// (<see cref="RelativeTraffic.OffersGroundRelative"/>): a bold header naming the selected aircraft, its give-way
    /// and follow items, then a separator. Adds nothing otherwise.
    /// </summary>
    public static void AddGroundRelative(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if ((context.PreviousSelection is not { } selected) || (!IsApplicable(MenuIds.GroundRelativeFollow, aircraft, context)))
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
        items.Add(Leaf(MenuIds.GroundRelativeGiveWay, aircraft, context, host));
        items.Add(Leaf(MenuIds.GroundRelativeFollow, aircraft, context, host));
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
    /// The ground view's landing items, flat and in the ground's order (touch and go, stop and go and low approach
    /// before the option), while any of cleared to land, go around or cancel landing clearance applies; then the
    /// runway exits; then cancel takeoff clearance while rolling. No separators: the ground menu has none here.
    /// </summary>
    public static void AddGroundLanding(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        bool landing =
            (IsApplicable(MenuIds.TowerClearedToLand, aircraft, context))
            || (IsApplicable(MenuIds.TowerGoAround, aircraft, context))
            || (IsApplicable(MenuIds.TowerCancelLanding, aircraft, context));
        if (landing)
        {
            foreach (string id in GroundLandingIds)
            {
                AddIfApplicable(items, id, aircraft, context, host);
            }
        }

        AddIfApplicable(items, MenuIds.TowerExitLeft, aircraft, context, host);
        AddIfApplicable(items, MenuIds.TowerExitRight, aircraft, context, host);
        if ((aircraft?.CurrentPhase ?? "") == "Takeoff")
        {
            AddIfApplicable(items, MenuIds.TowerCancelTakeoff, aircraft, context, host);
        }
    }

    /// <summary>The ground view's landing items, in menu order.</summary>
    private static readonly string[] GroundLandingIds =
    [
        MenuIds.TowerClearedToLand,
        MenuIds.TowerForceLanding,
        MenuIds.TowerTouchAndGo,
        MenuIds.TowerStopAndGo,
        MenuIds.TowerLowApproach,
        MenuIds.TowerClearedOption,
        MenuIds.TowerGoAround,
        MenuIds.TowerCancelLanding,
    ];

    private static void AddSeparatorIfNonEmpty(ItemCollection items)
    {
        if (items.Count > 0)
        {
            items.Add(new Separator());
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

    private static MenuItem Leaf(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        MenuCatalog.Get(id).Build(aircraft, context, host)
        ?? throw new InvalidOperationException($"The context-menu catalog entry '{id}' built no menu item.");

    /// <summary>Adds the entry's item, or nothing when the surface's own state hides it (the item is null).</summary>
    private static void AddIfBuilt(ItemCollection items, string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (MenuCatalog.Get(id).Build(aircraft, context, host) is { } item)
        {
            items.Add(item);
        }
    }
}
