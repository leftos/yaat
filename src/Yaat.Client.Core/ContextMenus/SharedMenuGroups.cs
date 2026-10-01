using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The groups more than one surface builds — live traffic, track, squawk, ask pilot, coordination, data block,
/// sim control, display, favorites, and the flight groups heading, altitude, speed, navigation (with Draw route),
/// hold and approach — assembled from <see cref="MenuCatalog"/> entries. Only track, squawk and ask pilot keep a
/// <see cref="MenuView"/> variant: the radar carries input pickers (handoff, point out, squawk code, custom say) the
/// other surfaces leave out, and sends <c>ID</c> for Ident where the others send <c>IDENT</c>. Data block, sim
/// control, display and the flight groups are built by the radar today, so they take no view. Whether a group is
/// offered at all stays with the caller.
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
        MenuCatalog.Get(MenuIds.AircraftEditFlightPlan).IsApplicable(aircraft, context)
            ? Leaf(MenuIds.AircraftEditFlightPlan, aircraft, context, host)
            : null;

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
            if (MenuCatalog.Get(id).IsApplicable(aircraft, context))
            {
                items.Add(Leaf(id, aircraft, context, host));
            }
        }
    }

    /// <summary>
    /// "Release to live feed" when the aircraft was assumed from the feed
    /// (<see cref="AircraftCommandApplicability.CanUnassume"/>), otherwise null.
    /// </summary>
    public static MenuItem? Unassume(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        MenuCatalog.Get(MenuIds.LiveTrafficUnassume).IsApplicable(aircraft, context)
            ? Leaf(MenuIds.LiveTrafficUnassume, aircraft, context, host)
            : null;

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
        if (MenuCatalog.Get(MenuIds.NavigationAppendDirectTo).IsApplicable(aircraft, context))
        {
            menu.Items.Add(Leaf(MenuIds.NavigationAppendDirectTo, aircraft, context, host));
        }

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
        if (MenuCatalog.BuildClearedVisualOther(aircraft, context, host) is { } otherRunway)
        {
            menu.Items.Add(otherRunway);
        }

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
