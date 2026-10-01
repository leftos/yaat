using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Every context-menu action the catalog knows, one <see cref="MenuCatalogEntry"/> per <see cref="MenuIds"/>
/// identifier. A leaf's builder sends its command text through <see cref="IMenuHost.SendAsync"/>; an input leaf
/// opens the host's input popup and formats the submitted text into the command; and a few entries open a host
/// surface of their own — the warp popup and the flight-plan editor — instead of sending a command at all.
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
    ];

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
    /// An entry whose item the host builds for a surface of its own rather than from a command text — the warp popup
    /// and the flight-plan editor today. <paramref name="build"/> receives the entry's own label, so the item's text
    /// lives in one place.
    /// </summary>
    private static MenuCatalogEntry HostLeaf(
        string id,
        string label,
        Func<IMenuAircraft?, MenuContext, bool> isApplicable,
        Func<string, IMenuAircraft?, MenuContext, IMenuHost, MenuItem?> build
    ) => new(id, label, MenuFlightRules.Both, isApplicable, (aircraft, context, host) => build(label, aircraft, context, host));

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
