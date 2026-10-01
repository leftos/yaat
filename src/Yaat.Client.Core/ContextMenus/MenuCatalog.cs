using System.Globalization;
using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Every context-menu action the catalog knows, one <see cref="MenuCatalogEntry"/> per <see cref="MenuIds"/>
/// identifier. A leaf's builder sends its command text through <see cref="IMenuHost.SendAsync"/>; an input leaf
/// opens the host's input popup and formats the submitted text into the command; a host leaf asks the host for the
/// item itself, for the entries that open a host surface or read the surface's own state — the warp popup, the
/// flight-plan editor, the data-block toggle and reset, the nav route and the measure item; and a value submenu
/// builds a whole submenu of command items from its own label and the menu context, one per value — the leader
/// directions, the J-ring radii and the cone lengths.
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

    private static MenuCatalogEntry Leaf(string id, string label, string command, Func<IMenuAircraft?, MenuContext, bool> isApplicable) =>
        new(id, label, MenuFlightRules.Both, isApplicable, (_, context, host) => BuildSend(label, command, context, host));

    private static MenuCatalogEntry InputLeaf(string id, string label, string placeholder, Func<string, string> format) =>
        new(id, label, MenuFlightRules.Both, Always, (_, context, host) => BuildInput(label, placeholder, format, context, host));

    /// <summary>
    /// An entry whose item the host builds for a surface of its own rather than from a command text: the warp popup,
    /// the flight-plan editor, and the display toggles and measure item that read and drive the surface's own state.
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
    /// The "Reset to student position" item, which the surface offers only while the data block sits away from the
    /// position the student sees it in; null otherwise.
    /// </summary>
    private static MenuItem? BuildResetDataBlockPosition(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!host.HasManualDataBlockOffset(context.Callsign))
        {
            return null;
        }

        var item = new MenuItem { Header = label };
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
