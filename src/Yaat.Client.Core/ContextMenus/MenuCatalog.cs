using System.Globalization;
using Avalonia.Controls;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Every context-menu action the catalog knows, one <see cref="MenuCatalogEntry"/> per <see cref="MenuIds"/>
/// identifier. A leaf's builder sends its command text through <see cref="IMenuHost.SendAsync"/>; an input leaf
/// opens the host's input popup and formats the submitted text into the command; a list or filtered-list picker opens
/// the host's list popup over values the catalog computes (headings, altitudes, speeds, fixes) and formats the pick
/// into the command, choosing its form from the data present when the menu is built; a host leaf asks the host for the
/// item itself, for the entries that open a host surface or read the surface's own state — the warp popup, the
/// flight-plan editor, the data-block toggle and reset, the nav route, the measure item and route drawing; and a value submenu
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
    ];

    /// <summary>The headings the heading pickers list, 005 to 360 in fives.</summary>
    private const int HeadingStep = 5;

    /// <summary>The trailing ellipsis a picker label carries while it opens a popup that is not the plain route-fix list.</summary>
    private const string Ellipsis = "...";

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

        return BuildInput(label, "Fix name", input => $"{command} {input}", context, host);
    }

    /// <summary>The Draw route item, which puts the host into drawing a route for the aircraft.</summary>
    private static MenuItem BuildDrawRoute(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => host.EnterDrawRoute(context.Callsign);
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
