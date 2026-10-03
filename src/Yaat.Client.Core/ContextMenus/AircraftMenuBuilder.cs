using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The aircraft context menu, built from the aircraft, the click and the host and the same whichever view opened it. A
/// view adds only its view section, the canvas items it alone can serve, which the builder places above the foot.
/// </summary>
public static class AircraftMenuBuilder
{
    /// <summary>
    /// The menu for <paramref name="aircraft"/> (null when the clicked callsign has no aircraft model), in one order: the
    /// header, Favorites, then by the aircraft's kind —
    /// <list type="bullet">
    /// <item>a delayed spawn: Spawn now, Change spawn delay and Delete, and nothing else;</item>
    /// <item>a surface live-traffic shadow: Track, Data Block and Coordination, the view section, the foot;</item>
    /// <item>
    /// any other aircraft, an assumable shadow opening with its assume items: the relative items, the ground-movement
    /// block, the phase-ordered profile groups, Preset taxi route, Draw taxi route…, Track, Data Block, Squawk, Ask pilot,
    /// Coordination, Edit flight plan, the view section after a separator, the foot;
    /// </item>
    /// </list>
    /// then "Assume selected live traffic (N)" and the RPO items for the click's selection, else for the clicked aircraft.
    /// </summary>
    /// <param name="aircraft">The aircraft the menu commands.</param>
    /// <param name="click">What was right-clicked and what was selected then.</param>
    /// <param name="host">The send path, popups, choices and session the menu's items use.</param>
    /// <param name="viewSection">
    /// The view's own canvas items for the menu's context; called once, and never for a delayed spawn.
    /// </param>
    public static ContextMenu Build(IMenuAircraft? aircraft, MenuClick click, IMenuHost host, Func<MenuContext, IReadOnlyList<Control>> viewSection)
    {
        // Radar, so the per-view groups build every picker: every view now has the popups they open.
        var context = new MenuContext(click, host.Session, MenuView.Radar);
        var menu = new ContextMenu();
        SharedMenuGroups.AddHeader(menu.Items, aircraft, context, host);
        menu.Items.Add(SharedMenuGroups.Favorites(aircraft, context, host));
        menu.Items.Add(new Separator());

        if (aircraft is { IsDelayed: true })
        {
            SharedMenuGroups.AddDelayedSpawn(menu, aircraft, context, host);
            return menu;
        }

        if (AircraftCommandApplicability.IsSurfaceShadow(aircraft))
        {
            AddSurfaceShadow(menu.Items, aircraft, context, host, viewSection(context));
        }
        else
        {
            AddCommandTree(menu.Items, aircraft, context, host, viewSection(context));
        }

        SharedMenuGroups.AddFoot(menu.Items, aircraft, context, host);
        SharedMenuGroups.AddAssumeSelected(menu, context, host);
        SharedMenuGroups.AddRange(menu.Items, host.BuildRpoItems(RpoCallsigns(click)));
        return menu;
    }

    /// <summary>
    /// A surface shadow's read-only tree: Track, Data Block and Coordination, then the view section straight after, and
    /// nothing that commands it.
    /// </summary>
    private static void AddSurfaceShadow(
        ItemCollection items,
        IMenuAircraft aircraft,
        MenuContext context,
        IMenuHost host,
        IReadOnlyList<Control> section
    )
    {
        items.Add(SharedMenuGroups.Track(aircraft, context, host, MenuView.Radar));
        items.Add(SharedMenuGroups.DataBlock(aircraft, context, host));
        items.Add(SharedMenuGroups.Coordination(aircraft, context, host));
        SharedMenuGroups.AddRange(items, section);
    }

    /// <summary>
    /// Everything between Favorites and the foot for an aircraft the controller commands: an assumable shadow's assume
    /// items, the relative items, the ground-movement block, the profile groups, Preset taxi route, Draw taxi route…
    /// (which starts on the primary ground view, the host showing it first), the always-present groups, Edit flight plan,
    /// then the view section after a separator.
    /// </summary>
    private static void AddCommandTree(
        ItemCollection items,
        IMenuAircraft? aircraft,
        MenuContext context,
        IMenuHost host,
        IReadOnlyList<Control> section
    )
    {
        if (aircraft is { IsLiveTraffic: true })
        {
            SharedMenuGroups.AddLiveTrafficAssume(items, aircraft, context, host);
            // A command sent to an airborne shadow auto-assumes it server-side, so the groups below apply as they are.
            items.Add(new Separator());
        }

        SharedMenuGroups.AddRelative(items, aircraft, context, host);
        AddGroundMovement(items, aircraft, context, host);
        AddProfileGroups(items, aircraft, context, host);
        SharedMenuGroups.AddIfApplicable(items, MenuIds.GroundTaxiPreset, aircraft, context, host);
        SharedMenuGroups.AddIfApplicable(items, MenuIds.GroundDrawTaxiRoute, aircraft, context, host);

        items.Add(new Separator());
        items.Add(SharedMenuGroups.Track(aircraft, context, host, MenuView.Radar));
        items.Add(SharedMenuGroups.DataBlock(aircraft, context, host));
        items.Add(SharedMenuGroups.Squawk(aircraft, context, host, MenuView.Radar));
        if (AircraftCommandApplicability.CanAskPilot(aircraft))
        {
            items.Add(SharedMenuGroups.AskPilot(aircraft, context, host, MenuView.Radar));
        }

        items.Add(SharedMenuGroups.Coordination(aircraft, context, host));
        items.Add(new Separator());
        if (SharedMenuGroups.EditFlightPlan(aircraft, context, host) is { } editFlightPlan)
        {
            items.Add(editFlightPlan);
        }

        if (section.Count > 0)
        {
            SharedMenuGroups.AddBlockSeparator(items);
            SharedMenuGroups.AddRange(items, section);
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
    /// position, Hold short of…, Follow…, Give way to…, Break conflict, Resume taxi, then Cross. A submenu with nothing to
    /// list (no other ground traffic, no stand, no hold-short target) is left out. Push route… starts its tug move on
    /// the primary ground view, which the host shows first.
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
    /// The flight and tower groups in the order <see cref="ContextMenuProfileService"/> gives the aircraft's phase: the
    /// primary groups, a separator, the secondary groups; the phase's hidden groups are left out, and so is a Tower or
    /// Pattern submenu with nothing in it.
    /// </summary>
    private static void AddProfileGroups(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(aircraft?.CurrentPhase, aircraft?.IsOnGround ?? false);
        foreach (MenuGroup group in profile.PrimaryGroups)
        {
            AddProfileGroup(items, group, aircraft, context, host);
        }

        if ((profile.PrimaryGroups.Count > 0) && (profile.SecondaryGroups.Count > 0))
        {
            items.Add(new Separator());
        }

        foreach (MenuGroup group in profile.SecondaryGroups)
        {
            AddProfileGroup(items, group, aircraft, context, host);
        }
    }

    private static void AddProfileGroup(ItemCollection items, MenuGroup group, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        MenuItem? item = group switch
        {
            MenuGroup.Heading => SharedMenuGroups.Heading(aircraft, context, host),
            MenuGroup.Altitude => SharedMenuGroups.Altitude(aircraft, context, host),
            MenuGroup.Speed => SharedMenuGroups.Speed(aircraft, context, host),
            MenuGroup.Navigation => SharedMenuGroups.Navigation(aircraft, context, host),
            MenuGroup.Hold => SharedMenuGroups.Hold(aircraft, context, host),
            MenuGroup.Approach => SharedMenuGroups.Approach(aircraft, context, host),
            MenuGroup.Procedures => SharedMenuGroups.Procedures(aircraft, context, host),
            MenuGroup.Tower => SharedMenuGroups.Tower(aircraft, context, host),
            MenuGroup.Pattern => SharedMenuGroups.Pattern(aircraft, context, host),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "The context-menu profile named a group the menu does not build"),
        };
        if (item is not null)
        {
            items.Add(item);
        }
    }

    /// <summary>The callsigns the RPO items act on: the click's selection, else the clicked aircraft.</summary>
    private static List<string> RpoCallsigns(MenuClick click) =>
        (click.Selection.Count > 0) ? [.. click.Selection.Select(a => a.Callsign)] : [click.Callsign];
}
