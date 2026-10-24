using Avalonia.Controls;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The aircraft context menu, built from the aircraft, the click and the host and the same whichever view opened it. A
/// view adds only its view section, the canvas items it alone can serve, which the builder places last in All Commands after a
/// separator, or at the root of the point menu.
/// </summary>
public static class AircraftMenuBuilder
{
    /// <summary>The header of the submenu that holds the full command tree.</summary>
    public const string AllCommandsHeader = "All Commands";

    /// <summary>
    /// The menu for <paramref name="aircraft"/> (null when the clicked callsign has no aircraft model), in one order: the
    /// header (<see cref="SharedMenuGroups.AddHeader"/>), then by the aircraft's kind —
    /// <list type="bullet">
    /// <item>
    /// a delayed spawn: the strip of the release icons that apply (<see cref="AddReleaseStrip"/>), Command… and Note…,
    /// Favorites, Spawn now, Change spawn delay and Delete, and nothing else;
    /// </item>
    /// <item>
    /// a surface live-traffic shadow: Command… and Note…, Favorites, All Commands (Track and coordination without Squawk or
    /// Edit flight plan, then the view section), then Delete;
    /// </item>
    /// <item>
    /// any other aircraft: the For section while another aircraft is selected (<see cref="SharedMenuGroups.AddForSection"/>),
    /// the quick commands (<see cref="AddQuickCommands"/>; the icon strip, then the text entries, or every entry as text
    /// with the icons off), Command…, Note…, All Commands (the full command tree grouped by task,
    /// <see cref="AddAllCommands"/>, then the view section), Favorites, then Delete;
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
        menu.Closed += (_, _) =>
        {
            host.HighlightAircraft(null);
            host.SetScopePreview(null);
            host.SetRadarCue(null);
            host.SetRoutePreview(null);
        };
        SharedMenuGroups.AddHeader(menu.Items, aircraft, context);

        if (aircraft is { IsDelayed: true })
        {
            AddReleaseStrip(menu, aircraft, context, host);
            AddCommandAndNoteBlock(menu.Items, aircraft, context, host);
            menu.Items.Add(SharedMenuGroups.Favorites(aircraft, context, host));
            menu.Items.Add(new Separator());
            SharedMenuGroups.AddDelayedSpawn(menu, aircraft, context, host);
            return menu;
        }

        if (AircraftCommandApplicability.IsSurfaceShadow(aircraft))
        {
            AddCommandAndNoteBlock(menu.Items, aircraft, context, host);
            AddSurfaceShadow(menu.Items, aircraft, context, host, viewSection(context));
        }
        else
        {
            menu.Items.Add(new Separator());
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
    /// Whether the aircraft's phase hides the Vectors, Approach and Pattern groups and the in-sight and Report when… rows of
    /// Pilot reports from All Commands, leaving Runway: a ground phase, a takeoff still on the ground, a landing
    /// roll and the touch-and-go variants. An aircraft with no phase shows them in the air and hides them on the ground. The
    /// radar's Draw route follows the same rule.
    /// </summary>
    public static bool HidesFlightCommands(string? phase, bool isOnGround)
    {
        if (string.IsNullOrEmpty(phase))
        {
            return isOnGround;
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
    /// Push to and Custom taxi…; on a runway surface, Taxi to runway — then Warp here after a separator. With the icons off
    /// (<see cref="MenuContext.ShowQuickCommandIcons"/>) there is no strip, and the radar point's icons become text rows
    /// (<see cref="AddRadarPointRows"/>).
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
        bool radarIcons = OffersRadarIcons(aircraft, point);
        if (radarIcons && !context.ShowQuickCommandIcons)
        {
            AddRadarPointRows(menu.Items, aircraft, point, context, host);
        }
        else if (radarIcons)
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
            SharedMenuGroups.AddIfApplicable(items, id, aircraft, context, host);
        }

        AddWarpHere(items, aircraft, context, host);
    }

    /// <summary>The radar point menu's text items with the icons off, in menu order.</summary>
    private static readonly string[] RadarPointRowIds =
    [
        MenuIds.PointFlyHeading,
        MenuIds.PointDirectTo,
        MenuIds.PointAppendDirectTo,
        MenuIds.PointHoldLeft,
        MenuIds.PointHoldRight,
    ];

    /// <summary>
    /// The radar point menu with the icons off (<see cref="MenuContext.ShowQuickCommandIcons"/>), every strip item a text row:
    /// Fly heading with its turn row under it (<see cref="AddFlyHeadingTurn"/>), Direct to, Append direct to and the two
    /// holds, each where its predicate allows it, then Warp here after a separator when it builds.
    /// </summary>
    private static void AddRadarPointRows(ItemCollection items, IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        foreach (string id in RadarPointRowIds)
        {
            if (SharedMenuGroups.AddIfApplicable(items, id, aircraft, context, host) && (id == MenuIds.PointFlyHeading))
            {
                AddFlyHeadingTurn(items, aircraft, point);
            }
        }

        AddWarpHere(items, aircraft, context, host);
    }

    /// <summary>Warp here after a separator, or nothing when it does not build.</summary>
    private static void AddWarpHere(ItemCollection items, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
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
        MagneticHeading current = new(aircraft.MagneticHeadingDegrees);
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
    /// built by the same predicates and builders as the text items, each with its point glyph
    /// (<see cref="QuickCommandGlyphs.ForPoint"/>). A disabled item gets no button unless it is marked inert
    /// (<see cref="QuickCommandStrip.IsInert"/>: Taxi here's "No route found"), whose button stays, faded; its text row
    /// still shows. Nothing when none applies, or with the icons off (<see cref="MenuContext.ShowQuickCommandIcons"/>), where
    /// the text items stand for it.
    /// </summary>
    private static void AddPointStrip(ContextMenu menu, string[] ids, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        if (!context.ShowQuickCommandIcons)
        {
            return;
        }

        List<(QuickCommandStripItem Item, MenuItem Built)> built = [];
        var scratch = new ContextMenu();
        foreach (string id in ids)
        {
            SharedMenuGroups.AddIfApplicable(scratch.Items, id, aircraft, context, host);
            var item = new QuickCommandStripItem(MenuCatalog.Get(id), QuickCommandGlyphs.ForPoint(id));
            List<MenuItem> items = [.. scratch.Items.OfType<MenuItem>().Where(menuItem => menuItem.IsEnabled || QuickCommandStrip.IsInert(menuItem))];
            scratch.Items.Clear();
            built.AddRange(items.Select(menuItem => (item, menuItem)));
        }

        if (QuickCommandStrip.FromBuilt(menu, built) is { } strip)
        {
            menu.Items.Add(strip);
            menu.Items.Add(new Separator());
        }
    }

    /// <summary>
    /// A surface shadow's read-only menu: Favorites, then All Commands holding one Track and coordination group with only
    /// its Track, Data block and Coordination blocks (<see cref="SharedMenuGroups.TrackAndCoordination"/>), and nothing
    /// that commands it, then the view section after a separator. All Commands is left out when it would hold nothing.
    /// </summary>
    private static void AddSurfaceShadow(
        ItemCollection items,
        IMenuAircraft aircraft,
        MenuContext context,
        IMenuHost host,
        IReadOnlyList<Control> section
    )
    {
        items.Add(SharedMenuGroups.Favorites(aircraft, context, host));
        var all = new MenuItem { Header = AllCommandsHeader };
        if (SharedMenuGroups.TrackAndCoordination(aircraft, context, host, commandsTheAircraft: false) is { } track)
        {
            all.Items.Add(track);
        }

        AddViewSection(all.Items, section);
        if (all.Items.Count > 0)
        {
            items.Add(all);
        }
    }

    /// <summary>A separator, Command… and Note…, and a separator: the block a delayed spawn's and a surface shadow's header ends with.</summary>
    private static void AddCommandAndNoteBlock(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        items.Add(new Separator());
        SharedMenuGroups.AddCommandAndNote(items, aircraft, context, host);
        items.Add(new Separator());
    }

    /// <summary>
    /// The aircraft's quick commands, each built through its catalog entry's own builder so its pickers, prompts and
    /// runway defaults match All Commands: the icon strip when the resolution has strip items, then the text entries; with
    /// the icons off (<see cref="MenuContext.ShowQuickCommandIcons"/>) every entry as a text row (<see cref="AddQuickCommandRows"/>).
    /// The release items that apply lead the strip whatever the situation's list (<see cref="LeadWithRelease"/>), and with
    /// the icons off they lead the text rows, unlabelled, ahead of the category blocks. An aircraft whose situation is
    /// unknown gets only the release items, or nothing.
    /// </summary>
    private static void AddQuickCommands(ContextMenu menu, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is null)
        {
            return;
        }

        // An entry that builds no item is dropped before the strip is capped; the strip and the text list build it again.
        bool BuildsAnItem(MenuCatalogEntry entry) => entry.Build(aircraft, context, host) is not null;
        QuickCommandResolution resolution = LeadWithRelease(
            QuickCommandResolver.Resolve(aircraft, context, BuildsAnItem),
            aircraft,
            context,
            BuildsAnItem
        );
        if (!context.ShowQuickCommandIcons)
        {
            AddQuickCommandRows(menu.Items, [.. resolution.Strip.Select(item => item.Entry), .. resolution.Text], aircraft, context, host);
            return;
        }

        if (QuickCommandStrip.Build(menu, resolution.Strip, aircraft, context, host) is { } strip)
        {
            menu.Items.Add(strip);
        }

        SharedMenuGroups.AddRange(menu.Items, TextRows(resolution.Text, aircraft, context, host));
    }

    /// <summary>
    /// The quick commands as text rows, with the icons off (<see cref="MenuContext.ShowQuickCommandIcons"/>): the release
    /// items (<see cref="ReleaseIds"/>) first and unlabelled, as they lead the strip with the icons on; then the entries of
    /// each task category under its label (<see cref="MenuCategories.Label"/>), in <see cref="MenuCategory"/> order and in
    /// <paramref name="entries"/>' order within a category; then the other entries in no category (custom entries)
    /// unlabelled, after a separator when rows precede them.
    /// </summary>
    private static void AddQuickCommandRows(
        ItemCollection items,
        IReadOnlyList<MenuCatalogEntry> entries,
        IMenuAircraft aircraft,
        MenuContext context,
        IMenuHost host
    )
    {
        List<MenuItem> release = TextRows(entries.Where(entry => ReleaseIds.Contains(entry.Id)), aircraft, context, host);
        SharedMenuGroups.AddRange(items, release);
        bool rowsBefore = release.Count > 0;
        foreach (MenuCategory category in Enum.GetValues<MenuCategory>())
        {
            List<MenuItem> rows = TextRows(entries.Where(entry => MenuCategories.Of(entry.Id) == category), aircraft, context, host);
            if (rows.Count == 0)
            {
                continue;
            }

            items.Add(SharedMenuGroups.SectionLabel(MenuCategories.Label(category)));
            SharedMenuGroups.AddRange(items, rows);
            rowsBefore = true;
        }

        List<MenuItem> rest = TextRows(
            entries.Where(entry => (MenuCategories.Of(entry.Id) is null) && !ReleaseIds.Contains(entry.Id)),
            aircraft,
            context,
            host
        );
        if (rowsBefore && (rest.Count > 0))
        {
            items.Add(new Separator());
        }

        SharedMenuGroups.AddRange(items, rest);
    }

    /// <summary>
    /// Each of <paramref name="entries"/> built as a text row with its command alias (<see cref="MenuAlias.Apply"/>), in
    /// order; an entry that builds nothing is skipped.
    /// </summary>
    private static List<MenuItem> TextRows(IEnumerable<MenuCatalogEntry> entries, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        List<MenuItem> rows = [];
        foreach (MenuCatalogEntry entry in entries)
        {
            if (entry.Build(aircraft, context, host) is { } item)
            {
                MenuAlias.Apply(entry.Id, item, context);
                rows.Add(item);
            }
        }

        return rows;
    }

    /// <summary>
    /// A delayed spawn's strip, which has no quick commands: after a separator, just the release icons that apply, in
    /// <see cref="ReleaseIds"/> order, built and labelled as the aircraft menu's strip is; with the icons off
    /// (<see cref="MenuContext.ShowQuickCommandIcons"/>) the same items as text rows. Nothing when neither applies.
    /// </summary>
    private static void AddReleaseStrip(ContextMenu menu, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        bool BuildsAnItem(MenuCatalogEntry entry) => entry.Build(aircraft, context, host) is not null;
        QuickCommandResolution release = LeadWithRelease(new QuickCommandResolution([], []), aircraft, context, BuildsAnItem);
        if (!context.ShowQuickCommandIcons)
        {
            List<MenuItem> rows = TextRows(release.Strip.Select(item => item.Entry), aircraft, context, host);
            if (rows.Count > 0)
            {
                menu.Items.Add(new Separator());
                SharedMenuGroups.AddRange(menu.Items, rows);
            }

            return;
        }

        if (QuickCommandStrip.Build(menu, release.Strip, aircraft, context, host) is { } strip)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(strip);
        }
    }

    /// <summary>The release items the strip leads with, in order; with the icons off they lead the text rows.</summary>
    public static IReadOnlyList<string> ReleaseIds { get; } = [MenuIds.CoordinationReleaseHeld, MenuIds.CoordinationCheckReleaseWindow];

    /// <summary>
    /// <paramref name="resolution"/> with the release icons that apply and build an item first in the strip, ahead of the
    /// situation's own icons, and taken out of wherever the situation's list placed them. The strip stays within
    /// <see cref="QuickCommandGlyphs.StripCapacity"/>: an icon pushed past it becomes the first text entry.
    /// </summary>
    private static QuickCommandResolution LeadWithRelease(
        QuickCommandResolution resolution,
        IMenuAircraft aircraft,
        MenuContext context,
        Func<MenuCatalogEntry, bool> buildsAnItem
    )
    {
        List<QuickCommandStripItem> release =
        [
            .. ReleaseIds
                .Select(MenuCatalog.Get)
                .Where(entry => entry.IsApplicable(aircraft, context) && buildsAnItem(entry))
                .Select(entry => new QuickCommandStripItem(
                    entry,
                    QuickCommandGlyphs.For(entry.Id) ?? throw new InvalidOperationException($"The release item '{entry.Id}' has no strip glyph.")
                )),
        ];
        if (release.Count == 0)
        {
            return resolution;
        }

        List<QuickCommandStripItem> strip = [.. release, .. resolution.Strip.Where(item => !ReleaseIds.Contains(item.Entry.Id))];
        List<MenuCatalogEntry> text =
        [
            .. strip.Skip(QuickCommandGlyphs.StripCapacity).Select(item => item.Entry),
            .. resolution.Text.Where(entry => !ReleaseIds.Contains(entry.Id)),
        ];
        return new QuickCommandResolution([.. strip.Take(QuickCommandGlyphs.StripCapacity)], text);
    }

    /// <summary>
    /// The top-level groups below the quick commands, after a separator: Command…, Note…, All Commands (ending with the
    /// view section after a separator, <see cref="AddViewSection"/>), then Favorites.
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
        SharedMenuGroups.AddCommandAndNote(items, aircraft, context, host);
        var all = new MenuItem { Header = AllCommandsHeader };
        AddAllCommands(all.Items, aircraft, context, host);
        AddViewSection(all.Items, section);
        items.Add(all);
        items.Add(SharedMenuGroups.Favorites(aircraft, context, host));
    }

    /// <summary>The view section last in All Commands, after a separator when items precede it; nothing when the view has none.</summary>
    private static void AddViewSection(ItemCollection allCommands, IReadOnlyList<Control> section)
    {
        if (section.Count == 0)
        {
            return;
        }

        SharedMenuGroups.AddBlockSeparator(allCommands);
        SharedMenuGroups.AddRange(allCommands, section);
    }

    /// <summary>
    /// The full command tree under All Commands, grouped by task in <see cref="MenuCategory"/> order: an assumable shadow's
    /// assume items and a separator, then Runway (<see cref="SharedMenuGroups.Tower"/>), Taxi (<see cref="Taxi"/>), Vectors,
    /// Approach and Pattern (all three only while the phase shows the flight commands, <see cref="HidesFlightCommands"/>),
    /// Pilot reports and Track and coordination, each left out when it has nothing in it, then the sim-control items
    /// (Warp…, Release to live feed) after a separator.
    /// </summary>
    private static void AddAllCommands(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (aircraft is { IsLiveTraffic: true })
        {
            SharedMenuGroups.AddLiveTrafficAssume(items, aircraft, context, host);
            // A command sent to an airborne shadow auto-assumes it server-side, so the groups below apply as they are.
            items.Add(new Separator());
        }

        bool hidden = HidesFlightCommands(aircraft?.CurrentPhase, aircraft?.IsOnGround ?? false);
        AddGroup(items, SharedMenuGroups.Tower(aircraft, context, host));
        AddGroup(items, Taxi(aircraft, context, host));
        if (!hidden)
        {
            AddGroup(items, SharedMenuGroups.Vectors(aircraft, context, host));
            AddGroup(items, SharedMenuGroups.Approach(aircraft, context, host));
            AddGroup(items, SharedMenuGroups.Pattern(aircraft, context, host));
        }

        AddGroup(items, SharedMenuGroups.PilotReports(aircraft, context, host, hidden));
        AddGroup(items, SharedMenuGroups.TrackAndCoordination(aircraft, context, host, commandsTheAircraft: true));
        SharedMenuGroups.AddSimControl(items, aircraft, context, host);
        if ((items.Count > 0) && (items[^1] is Separator))
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    /// <summary>Adds <paramref name="group"/>, or nothing when it is null (a group with nothing in it).</summary>
    private static void AddGroup(ItemCollection items, MenuItem? group)
    {
        if (group is not null)
        {
            items.Add(group);
        }
    }

    /// <summary>
    /// The Taxi group: the ground-movement block (<see cref="AddGroundMovement"/>), then Draw taxi route… (which starts on
    /// the primary ground view, the host showing it first). Null when nothing applies.
    /// </summary>
    private static MenuItem? Taxi(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = MenuCategories.Label(MenuCategory.Taxi) };
        AddGroundMovement(menu.Items, aircraft, context, host);
        SharedMenuGroups.AddIfApplicable(menu.Items, MenuIds.GroundDrawTaxiRoute, aircraft, context, host);
        return (menu.Items.Count > 0) ? menu : null;
    }

    /// <summary>The ground-movement items, each where its own predicate allows it, in menu order.</summary>
    private static readonly string[] GroundMovementIds =
    [
        MenuIds.GroundPushbackTo,
        MenuIds.GroundPushRoute,
        MenuIds.GroundHoldPosition,
        MenuIds.GroundTaxiToRunway,
        MenuIds.GroundTaxiPreset,
        MenuIds.GroundAirTaxi,
        MenuIds.GroundLandHelicopter,
        MenuIds.GroundHoldShort,
        MenuIds.GroundFollow,
        MenuIds.GroundGiveWay,
        MenuIds.GroundBreakConflict,
        MenuIds.GroundResumeTaxi,
        MenuIds.GroundCrossRunway,
    ];

    /// <summary>
    /// The ground-movement block, each item by its predicate: Push back, the face items, Push back to…, Push route…, Hold
    /// position, Taxi to runway, Preset taxi route, Air taxi (a helicopter on the ground), Land (an airborne helicopter), Hold
    /// short of…, Follow…, Give way to…, Ignore ground conflicts (15 s), Resume taxi, then Cross. A submenu with nothing to
    /// list (no other ground traffic, no stand, no hold-short target, no preset route) is left out; Taxi to runway, Air taxi
    /// and Land build their rows only when they open, so each is there whenever its predicate allows it. Push route…
    /// starts its tug move on the primary ground view, which the host shows first.
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

    /// <summary>The callsigns the RPO items act on: the click's selection, else the clicked aircraft.</summary>
    private static List<string> RpoCallsigns(MenuClick click) =>
        (click.Selection.Count > 0) ? [.. click.Selection.Select(a => a.Callsign)] : [click.Callsign];
}
