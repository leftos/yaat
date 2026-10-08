using Avalonia.Controls;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The aircraft context menu, built from the aircraft, the click and the host and the same whichever view opened it. A
/// view adds only its view section, the canvas items it alone can serve, which the builder places after Squawk.
/// </summary>
public static class AircraftMenuBuilder
{
    /// <summary>The header of the submenu that holds the full command tree.</summary>
    public const string AllCommandsHeader = "All Commands";

    /// <summary>
    /// The menu for <paramref name="aircraft"/> (null when the clicked callsign has no aircraft model), in one order: the
    /// header (with Command…), then by the aircraft's kind —
    /// <list type="bullet">
    /// <item>a delayed spawn: Favorites, Spawn now, Change spawn delay and Delete, and nothing else;</item>
    /// <item>
    /// a surface live-traffic shadow: Track, Data Block, the view section, Favorites, All Commands (Coordination only),
    /// then Delete;
    /// </item>
    /// <item>
    /// any other aircraft: the For section while another aircraft is selected (<see cref="SharedMenuGroups.AddForSection"/>),
    /// the quick commands its situation resolves to (<see cref="QuickCommandResolver"/>; the icon strip, then the text
    /// entries), Track, Data Block, Squawk, the view section, Favorites, All Commands (the full command tree,
    /// <see cref="AddAllCommands"/>), then Delete;
    /// </item>
    /// </list>
    /// then "Assume selected live traffic (N)" and the RPO items for the click's selection, else for the clicked aircraft.
    /// A point click (<see cref="MenuClick.Point"/> set) builds the point menu instead (<see cref="BuildPointMenu"/>), and
    /// throws <see cref="ArgumentException"/> without an aircraft.
    /// </summary>
    /// <param name="aircraft">The aircraft the menu commands.</param>
    /// <param name="click">What was right-clicked and what was selected then.</param>
    /// <param name="host">The send path, popups, choices and session the menu's items use.</param>
    /// <param name="viewSection">
    /// The view's own canvas items for the menu's context; called once, and never for a delayed spawn.
    /// </param>
    public static ContextMenu Build(IMenuAircraft? aircraft, MenuClick click, IMenuHost host, Func<MenuContext, IReadOnlyList<Control>> viewSection)
    {
        var context = new MenuContext(click, host.Session);
        if (click.Point is not null)
        {
            return BuildPointMenu(
                aircraft
                    ?? throw new ArgumentException(
                        $"A point click needs the selected aircraft; {click.Callsign} has no aircraft model.",
                        nameof(aircraft)
                    ),
                context,
                host,
                viewSection
            );
        }

        var menu = new ContextMenu();
        menu.Closed += (_, _) => host.HighlightAircraft(null);
        SharedMenuGroups.AddHeader(menu.Items, aircraft, context, host);

        if (aircraft is { IsDelayed: true })
        {
            menu.Items.Add(SharedMenuGroups.Favorites(aircraft, context, host));
            menu.Items.Add(new Separator());
            SharedMenuGroups.AddDelayedSpawn(menu, aircraft, context, host);
            return menu;
        }

        if (AircraftCommandApplicability.IsSurfaceShadow(aircraft))
        {
            AddSurfaceShadow(menu.Items, aircraft, context, host, viewSection(context));
        }
        else
        {
            SharedMenuGroups.AddForSection(menu.Items, aircraft, context, host);
            AddQuickCommands(menu, aircraft, context, host);
            AddTopLevelGroups(menu.Items, aircraft, context, host, viewSection(context));
        }

        SharedMenuGroups.AddFoot(menu.Items, aircraft, context, host);
        SharedMenuGroups.AddAssumeSelected(menu, context, host);
        SharedMenuGroups.AddRange(menu.Items, host.BuildRpoItems(RpoCallsigns(click)));
        return menu;
    }

    /// <summary>
    /// Whether the aircraft's phase hides the flight groups (Heading, Altitude, Speed, Navigation, Hold, Approach,
    /// Procedures) and Pattern from All Commands, leaving Tower: a ground phase, a takeoff still on the ground, a landing
    /// roll and the touch-and-go variants. An aircraft with no phase shows them. The radar's Draw route follows the same rule.
    /// </summary>
    public static bool HidesFlightCommands(string? phase, bool isOnGround)
    {
        if (string.IsNullOrEmpty(phase))
        {
            return false;
        }

        return AircraftCommandApplicability.IsGroundPhase(phase)
            || ((phase == "Takeoff") && isOnGround)
            || (phase is "Landing" or "Landing-H" or "TouchAndGo" or "StopAndGo" or "LowApproach" or "Takeoff-H");
    }

    /// <summary>The point items before Warp here, each where its own predicate allows it, in menu order.</summary>
    private static readonly string[] PointIds =
    [
        MenuIds.PointFlyHeading,
        MenuIds.PointDirectTo,
        MenuIds.PointAppendDirectTo,
        MenuIds.PointHoldLeft,
        MenuIds.PointHoldRight,
        MenuIds.PointTaxiHere,
        MenuIds.PointTaxiToRunway,
        MenuIds.PointPushTo,
        MenuIds.PointCustomTaxi,
    ];

    /// <summary>
    /// The menu for a point right-clicked with <paramref name="aircraft"/> selected: the point header
    /// (<see cref="SharedMenuGroups.AddPointHeader"/>), the strip of the point items that apply and a separator
    /// (<see cref="AddPointStrip"/>), then the point items by the aircraft's predicates, then the view section after a
    /// separator. On a radar point with an airborne aircraft (<see cref="OffersRadarIcons"/>) the strip holds Fly heading,
    /// Direct to, the two holds and Warp here, and the text items are Fly heading, with its turn row under it
    /// (<see cref="AddFlyHeadingTurn"/>), and Append direct to. Otherwise the strip holds the ground point items and the
    /// text items are: airborne, Fly heading, Direct to, Append direct to and the two holds; at a taxi node, Taxi here,
    /// Push to and Custom taxi…; on a runway surface, a Taxi to submenu per runway end — then Warp here after a separator.
    /// No Favorites, command tree, foot or RPO items. The header, the strip and the text items are built over one
    /// <see cref="PointMenuHostCache"/>, so the host answers each point question once.
    /// </summary>
    private static ContextMenu BuildPointMenu(
        IMenuAircraft aircraft,
        MenuContext context,
        IMenuHost realHost,
        Func<MenuContext, IReadOnlyList<Control>> viewSection
    )
    {
        var host = new PointMenuHostCache(realHost);
        var menu = new ContextMenu();
        MenuPoint point = context.Click.Point ?? throw new ArgumentException("The point menu needs a point click.", nameof(context));
        SharedMenuGroups.AddPointHeader(menu.Items, aircraft, point, context, host);
        if (OffersRadarIcons(aircraft, point))
        {
            AddPointStrip(menu, RadarPointStripIds, aircraft, context, host);
            AddRadarPointItems(menu.Items, aircraft, point, context, host);
        }
        else
        {
            AddPointStrip(menu, PointStripIds, aircraft, context, host);
            AddGroundPointItems(menu.Items, aircraft, context, host);
        }

        IReadOnlyList<Control> section = viewSection(context);
        if (section.Count > 0)
        {
            SharedMenuGroups.AddBlockSeparator(menu.Items);
            SharedMenuGroups.AddRange(menu.Items, section);
        }

        return menu;
    }

    /// <summary>Whether the point menu offers the radar icons: a radar point clicked with an airborne aircraft that can be commanded.</summary>
    private static bool OffersRadarIcons(IMenuAircraft aircraft, MenuPoint point) =>
        SharedMenuGroups.IsRadarPoint(point) && AircraftCommandApplicability.IsAirborneControllable(aircraft);

    /// <summary>The point items in <see cref="PointIds"/> order, then Warp here after a separator when it builds.</summary>
    private static void AddGroundPointItems(ItemCollection items, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        foreach (string id in PointIds)
        {
            AddPointItems(items, id, aircraft, context, host);
        }

        // Warp here is built into a scratch menu first, so its separator goes in only when it builds.
        var scratch = new ContextMenu();
        if (SharedMenuGroups.AddIfApplicable(scratch.Items, MenuIds.PointWarpHere, aircraft, context, host))
        {
            List<Control> warp = [.. scratch.Items.OfType<Control>()];
            scratch.Items.Clear();
            SharedMenuGroups.AddBlockSeparator(items);
            SharedMenuGroups.AddRange(items, warp);
        }
    }

    /// <summary>
    /// The radar point menu's text items, the ones its icons do not repeat: Fly heading with its turn row under it, then
    /// Append direct to, each where its predicate allows it.
    /// </summary>
    private static void AddRadarPointItems(ItemCollection items, IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        if (SharedMenuGroups.AddIfApplicable(items, MenuIds.PointFlyHeading, aircraft, context, host))
        {
            AddFlyHeadingTurn(items, aircraft, point);
        }

        SharedMenuGroups.AddIfApplicable(items, MenuIds.PointAppendDirectTo, aircraft, context, host);
    }

    /// <summary>
    /// The dimmed row under the radar point menu's Fly heading: <c>FH {hdg} · {left|right} turn, {n}°</c>, the heading
    /// the row sends and the shorter turn to it from the aircraft's current heading (made magnetic at its position), in
    /// whole degrees. Nothing when that turn is under 1°.
    /// </summary>
    private static void AddFlyHeadingTurn(ItemCollection items, IMenuAircraft aircraft, MenuPoint point)
    {
        var heading = new MagneticHeading(MenuCatalog.PointFlyHeading(aircraft, point));
        MagneticHeading current = new TrueHeading(aircraft.HeadingDegrees).ToMagnetic(MagneticDeclination.GetDeclination(aircraft.Position));
        double turn = current.SignedAngleTo(heading);
        if (Math.Abs(turn) < 1.0)
        {
            return;
        }

        int degrees = (int)Math.Round(Math.Abs(turn), MidpointRounding.AwayFromZero);
        string direction = (turn < 0) ? "left" : "right";
        items.Add(SharedMenuGroups.DetailRow($"FH {heading.ToDisplayString()} · {direction} turn, {degrees}°"));
    }

    /// <summary>The ground point items the point menu's strip offers, in strip order.</summary>
    private static readonly string[] PointStripIds = [MenuIds.PointTaxiHere, MenuIds.PointTaxiToRunway, MenuIds.PointPushTo, MenuIds.PointCustomTaxi];

    /// <summary>The point items the radar point menu's strip offers an airborne aircraft, in strip order.</summary>
    private static readonly string[] RadarPointStripIds =
    [
        MenuIds.PointFlyHeading,
        MenuIds.PointDirectTo,
        MenuIds.PointHoldLeft,
        MenuIds.PointHoldRight,
        MenuIds.PointWarpHere,
    ];

    /// <summary>
    /// The point menu's icon strip and a separator under it: one button per item of <paramref name="ids"/> that applies,
    /// built by the same predicates and builders as the text items (a Taxi to runway per runway end), each with its point
    /// glyph (<see cref="QuickCommandGlyphs.ForPoint"/>). A disabled item (Taxi here's "No route found") gets no button;
    /// its text row still shows. Nothing when none applies.
    /// </summary>
    private static void AddPointStrip(ContextMenu menu, string[] ids, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        List<(QuickCommandStripItem Item, MenuItem Built)> built = [];
        var scratch = new ContextMenu();
        foreach (string id in ids)
        {
            AddPointItems(scratch.Items, id, aircraft, context, host);
            var item = new QuickCommandStripItem(MenuCatalog.Get(id), QuickCommandGlyphs.ForPoint(id));
            List<MenuItem> items = [.. scratch.Items.OfType<MenuItem>().Where(menuItem => menuItem.IsEnabled)];
            scratch.Items.Clear();
            built.AddRange(items.Select(menuItem => (item, menuItem)));
        }

        if (QuickCommandStrip.FromBuilt(menu, built) is { } strip)
        {
            menu.Items.Add(strip);
            menu.Items.Add(new Separator());
        }
    }

    /// <summary>The point item <paramref name="id"/> where its predicate allows it; Taxi to runway adds one item per runway end.</summary>
    private static void AddPointItems(ItemCollection items, string id, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        if (id == MenuIds.PointTaxiToRunway)
        {
            SharedMenuGroups.AddTaxiToRunwayEnds(items, aircraft, context, host);
            return;
        }

        SharedMenuGroups.AddIfApplicable(items, id, aircraft, context, host);
    }

    /// <summary>
    /// A surface shadow's read-only menu: Track and Data Block, the view section, Favorites, then All Commands holding
    /// only Coordination, and nothing that commands it.
    /// </summary>
    private static void AddSurfaceShadow(
        ItemCollection items,
        IMenuAircraft aircraft,
        MenuContext context,
        IMenuHost host,
        IReadOnlyList<Control> section
    )
    {
        items.Add(SharedMenuGroups.Track(aircraft, context, host));
        items.Add(SharedMenuGroups.DataBlock(aircraft, context, host));
        SharedMenuGroups.AddRange(items, section);
        items.Add(SharedMenuGroups.Favorites(aircraft, context, host));
        var all = new MenuItem { Header = AllCommandsHeader };
        all.Items.Add(SharedMenuGroups.Coordination(aircraft, context, host));
        items.Add(all);
    }

    /// <summary>
    /// The aircraft's quick commands, each built through its catalog entry's own builder so its pickers, prompts and
    /// runway defaults match All Commands: the icon strip when the resolution has strip items, then the text entries.
    /// An approach entry with a default approach is a one-click leaf, and its "(other)" grouped picker
    /// (<see cref="MenuCatalog.BuildApproachOther"/>) follows as a text row: directly under the leaf for a text entry,
    /// and first among the text rows for a strip button, which stays the leaf alone. Nothing for an aircraft whose
    /// situation is unknown.
    /// </summary>
    private static void AddQuickCommands(ContextMenu menu, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is null)
        {
            return;
        }

        // An entry that builds no item is dropped before the strip is capped; the strip and the text list build it again.
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            aircraft,
            context,
            entry => entry.Build(aircraft, context, host) is not null
        );
        if (QuickCommandStrip.Build(menu, resolution.Strip, aircraft, context, host) is { } strip)
        {
            menu.Items.Add(strip);
        }

        foreach (QuickCommandStripItem stripItem in resolution.Strip)
        {
            AddApproachCompanion(menu.Items, stripItem.Entry.Id, aircraft, context, host);
        }

        foreach (MenuCatalogEntry entry in resolution.Text)
        {
            if (entry.Build(aircraft, context, host) is { } item)
            {
                menu.Items.Add(item);
                AddApproachCompanion(menu.Items, entry.Id, aircraft, context, host);
            }
        }
    }

    /// <summary>Adds an approach entry's "(other)" grouped picker, or nothing for another entry or an approach entry without a default.</summary>
    private static void AddApproachCompanion(ItemCollection items, string id, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        if (MenuCatalog.ApproachPickerIds.Contains(id) && (MenuCatalog.BuildApproachOther(id, aircraft, context, host) is { } other))
        {
            items.Add(other);
        }
    }

    /// <summary>
    /// The top-level groups below the quick commands: Track, Data Block, Squawk, the view section, Favorites, then All
    /// Commands, after a separator.
    /// </summary>
    private static void AddTopLevelGroups(
        ItemCollection items,
        IMenuAircraft? aircraft,
        MenuContext context,
        IMenuHost host,
        IReadOnlyList<Control> section
    )
    {
        SharedMenuGroups.AddBlockSeparator(items);
        items.Add(SharedMenuGroups.Track(aircraft, context, host));
        items.Add(SharedMenuGroups.DataBlock(aircraft, context, host));
        items.Add(SharedMenuGroups.Squawk(aircraft, context, host));
        SharedMenuGroups.AddRange(items, section);
        items.Add(SharedMenuGroups.Favorites(aircraft, context, host));

        var all = new MenuItem { Header = AllCommandsHeader };
        AddAllCommands(all.Items, aircraft, context, host);
        items.Add(all);
    }

    /// <summary>
    /// The full command tree under All Commands: an assumable shadow's assume items, the ground-movement block, the
    /// flight and tower groups in one fixed order (<see cref="AddFlightGroups"/>), Preset taxi route, Draw taxi route…
    /// (which starts on the primary ground view, the host showing it first), Ask pilot, Coordination, Edit flight plan,
    /// then the sim-control items (Warp…, Release to live feed) after a separator.
    /// </summary>
    private static void AddAllCommands(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is { IsLiveTraffic: true })
        {
            SharedMenuGroups.AddLiveTrafficAssume(items, aircraft, context, host);
            // A command sent to an airborne shadow auto-assumes it server-side, so the groups below apply as they are.
            items.Add(new Separator());
        }

        AddGroundMovement(items, aircraft, context, host);
        AddFlightGroups(items, aircraft, context, host);
        SharedMenuGroups.AddIfApplicable(items, MenuIds.GroundTaxiPreset, aircraft, context, host);
        SharedMenuGroups.AddIfApplicable(items, MenuIds.GroundDrawTaxiRoute, aircraft, context, host);

        items.Add(new Separator());
        if (AircraftCommandApplicability.CanAskPilot(aircraft))
        {
            items.Add(SharedMenuGroups.AskPilot(aircraft, context, host));
        }

        items.Add(SharedMenuGroups.Coordination(aircraft, context, host));
        items.Add(new Separator());
        if (SharedMenuGroups.EditFlightPlan(aircraft, context, host) is { } editFlightPlan)
        {
            items.Add(editFlightPlan);
        }

        SharedMenuGroups.AddSimControl(items, aircraft, context, host);
        if (items[^1] is Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    /// <summary>The ground-movement items, each where its own predicate allows it, in menu order.</summary>
    private static readonly string[] GroundMovementIds =
    [
        MenuIds.GroundPushbackTo,
        MenuIds.GroundPushRoute,
        MenuIds.GroundHoldPosition,
        MenuIds.GroundHoldShort,
        MenuIds.GroundFollow,
        MenuIds.GroundGiveWay,
        MenuIds.GroundBreakConflict,
        MenuIds.GroundResumeTaxi,
        MenuIds.GroundCrossRunway,
    ];

    /// <summary>
    /// The ground-movement block, each item by its predicate: Push back, the face items, Push back to…, Push route…, Hold
    /// position, Hold short of…, Follow…, Give way to…, Ignore ground conflicts (15 s), Resume taxi, then Cross. A submenu
    /// with nothing to list (no other ground traffic, no stand, no hold-short target) is left out. Push route… starts its
    /// tug move on the primary ground view, which the host shows first.
    /// </summary>
    private static void AddGroundMovement(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        SharedMenuGroups.AddIfApplicable(items, MenuIds.GroundPushback, aircraft, context, host);
        if (MenuCatalog.Get(MenuIds.GroundPushbackFace).IsApplicable(aircraft, context))
        {
            SharedMenuGroups.AddRange(items, MenuCatalog.BuildPushbackFaces(context, host));
        }

        foreach (string id in GroundMovementIds)
        {
            SharedMenuGroups.AddIfApplicable(items, id, aircraft, context, host);
        }
    }

    /// <summary>
    /// The flight and tower groups in one order on every aircraft: Heading, Altitude, Speed, Navigation, Hold, Approach,
    /// Procedures, Tower, Pattern. All but Tower are left out while the phase hides the flight commands
    /// (<see cref="HidesFlightCommands"/>), and a Tower or Pattern submenu with nothing in it is left out.
    /// </summary>
    private static void AddFlightGroups(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        bool hidden = HidesFlightCommands(aircraft?.CurrentPhase, aircraft?.IsOnGround ?? false);
        if (!hidden)
        {
            items.Add(SharedMenuGroups.Heading(aircraft, context, host));
            items.Add(SharedMenuGroups.Altitude(aircraft, context, host));
            items.Add(SharedMenuGroups.Speed(aircraft, context, host));
            items.Add(SharedMenuGroups.Navigation(aircraft, context, host));
            items.Add(SharedMenuGroups.Hold(aircraft, context, host));
            items.Add(SharedMenuGroups.Approach(aircraft, context, host));
            items.Add(SharedMenuGroups.Procedures(aircraft, context, host));
        }

        if (SharedMenuGroups.Tower(aircraft, context, host) is { } tower)
        {
            items.Add(tower);
        }

        if (!hidden && (SharedMenuGroups.Pattern(aircraft, context, host) is { } pattern))
        {
            items.Add(pattern);
        }
    }

    /// <summary>The callsigns the RPO items act on: the click's selection, else the clicked aircraft.</summary>
    private static List<string> RpoCallsigns(MenuClick click) =>
        (click.Selection.Count > 0) ? [.. click.Selection.Select(a => a.Callsign)] : [click.Callsign];
}
