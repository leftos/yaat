using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The groups more than one surface builds — the menu header (title, Command…, Note…), live traffic, track, squawk, ask
/// pilot, coordination, data block, favorites, the menu foot (warp, release to live feed, delete), and All Commands' task
/// groups (<see cref="MenuCategory"/>) runway, vectors, approach, pattern, pilot reports and track and coordination — assembled from
/// <see cref="MenuCatalog"/> entries. Every surface offers the input pickers — handoff, point out, squawk code, custom
/// say. The For section (<see cref="AddForSection"/>) is a group of its own. Whether a group is offered at all stays with the
/// caller. A canvas-only group such as the Display submenu is not built here: its surface builds it with
/// <see cref="CanvasMenuItems"/> and hands it to <see cref="AircraftMenuBuilder"/> as the view section.
/// </summary>
public static class SharedMenuGroups
{
    /// <summary>
    /// The Track submenu: each track state's group of items, an untracked track's single Initiate Track, a separator
    /// between the groups that show and none where they do not. Overlapping states show the union of their groups.
    /// </summary>
    public static MenuItem Track(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Track" };
        foreach (string[] group in TrackGroups)
        {
            AddBlockIfAnyApplies(menu.Items, group, aircraft, context, host);
        }

        return menu;
    }

    /// <summary>
    /// The Track submenu's groups in order: the untracked item, then the owned track's handoff, point out and drop, then
    /// the handoff in progress's accept and cancel, then the pending pointout's acknowledge.
    /// </summary>
    private static readonly string[][] TrackGroups =
    [
        [MenuIds.TrackTrack],
        [MenuIds.TrackInitiateHandoff, MenuIds.TrackPointOut, MenuIds.TrackDrop],
        [MenuIds.TrackAcceptHandoff, MenuIds.TrackCancelHandoff],
        [MenuIds.TrackAcknowledgePointout],
    ];

    /// <summary>The Squawk flyout, headed "Squawk" (<see cref="SquawkFlyout"/>).</summary>
    public static MenuItem Squawk(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) => SquawkFlyout("Squawk", aircraft, context, host);

    /// <summary>
    /// The Squawk flyout headed <paramref name="header"/>, Track and coordination's Squawk block and the Squawk… entry alike: the code box
    /// row first, always open and holding the focus as the flyout opens (<see cref="MenuCatalog.SquawkCodeRow"/>), then
    /// after a separator Squawk assigned code and the presets, then after another separator Ident.
    /// </summary>
    public static MenuItem SquawkFlyout(string header, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = header };
        InlineBoxRow codeRow = MenuCatalog.SquawkCodeRow(context, host);
        menu.Items.Add(codeRow.Item);
        menu.Items.Add(new Separator());
        foreach (string id in SquawkPresetIds)
        {
            TryAdd(menu.Items, SquawkRow(id, aircraft, context, host));
        }

        if (SquawkRow(MenuIds.SquawkIdent, aircraft, context, host) is { } ident)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ident);
        }

        FocusTheBoxOnOpen(menu, codeRow);
        return menu;
    }

    /// <summary>The Squawk flyout's preset rows, in order: Squawk assigned code, then the presets.</summary>
    private static readonly string[] SquawkPresetIds =
    [
        MenuIds.SquawkAssigned,
        MenuIds.SquawkRandom,
        MenuIds.SquawkVfr,
        MenuIds.SquawkNormal,
        MenuIds.SquawkStandby,
    ];

    /// <summary>
    /// The entry's item as a Squawk flyout row, where its predicate allows it: its label led by the glyph column (its quick-command
    /// glyph, else a blank slot as wide), so its label starts where the code box does.
    /// </summary>
    private static MenuItem? SquawkRow(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (TryLeaf(id, aircraft, context, host) is not { } item)
        {
            return null;
        }

        if (item.Header is not string label)
        {
            throw new InvalidOperationException($"The Squawk flyout row '{id}' has a {item.Header?.GetType().Name} header, not a label.");
        }

        item.Header = new GlyphColumnLabel(QuickCommandGlyphs.For(id), label);
        item.HeaderTemplate = GlyphColumnLabel.Template;
        AutomationProperties.SetName(item, label);
        return item;
    }

    /// <summary>A Squawk flyout row's header: <see cref="Label"/> after the glyph column, <see cref="Glyph"/> or a blank slot.</summary>
    private sealed record GlyphColumnLabel(QuickCommandGlyph? Glyph, string Label)
    {
        /// <summary>Draws the glyph column, then the label.</summary>
        public static FuncDataTemplate<GlyphColumnLabel> Template { get; } = new((row, _) => Build(row));

        /// <summary>The row as one line: its label.</summary>
        public override string ToString() => Label;

        private static StackPanel Build(GlyphColumnLabel row)
        {
            Control slot = (row.Glyph is { } glyph) ? QuickCommandStrip.GlyphIcon(glyph) : QuickCommandStrip.GlyphSlot();
            slot.Margin = new Thickness(0, 0, 8, 0);
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    slot,
                    new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        }
    }

    /// <summary>The Say flyout, headed "Ask pilot to say…" (<see cref="AskPilotFlyout"/>).</summary>
    public static MenuItem AskPilot(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        AskPilotFlyout("Ask pilot to say…", aircraft, context, host);

    /// <summary>
    /// The Say flyout headed <paramref name="header"/>, Pilot reports' Ask pilot to say… row and the Custom… entry alike: the six
    /// say queries, then after a separator the Custom box row, always open and holding the focus as the flyout opens
    /// (<see cref="MenuCatalog.SayCustomRow"/>).
    /// </summary>
    public static MenuItem AskPilotFlyout(string header, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = header };
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotAltitude, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotHeading, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotSpeed, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotMach, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotPosition, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotExpectedApproach, aircraft, context, host));
        menu.Items.Add(new Separator());
        InlineBoxRow customRow = MenuCatalog.SayCustomRow(context, host);
        menu.Items.Add(customRow.Item);
        FocusTheBoxOnOpen(menu, customRow);
        return menu;
    }

    /// <summary>
    /// The Scratchpad flyout headed <paramref name="header"/>, the Data Block group's Scratchpad… entry: the Scratchpad box row
    /// first, always open, holding the present Scratchpad 1 selected and the focus as the flyout opens
    /// (<see cref="MenuCatalog.ScratchpadRow"/>), then after a separator Clear Scratchpad 1 and Clear Scratchpad 2, each only while
    /// its scratchpad holds a value; with neither, no separator.
    /// </summary>
    public static MenuItem ScratchpadFlyout(string header, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = header };
        InlineBoxRow row = MenuCatalog.ScratchpadRow(aircraft, context, host);
        menu.Items.Add(row.Item);
        if (IsApplicable(MenuIds.DataBlockScratchpadClear1, aircraft, context) || IsApplicable(MenuIds.DataBlockScratchpadClear2, aircraft, context))
        {
            menu.Items.Add(new Separator());
            AddIfApplicable(menu.Items, MenuIds.DataBlockScratchpadClear1, aircraft, context, host);
            AddIfApplicable(menu.Items, MenuIds.DataBlockScratchpadClear2, aircraft, context, host);
        }

        FocusTheBoxOnOpen(menu, row);
        return menu;
    }

    /// <summary>
    /// Focuses <paramref name="row"/>'s box each time <paramref name="menu"/> opens, while it stays open: as its own submenu, or
    /// as the flyout a quick-command strip button opens over its items, where the submenu never opens.
    /// </summary>
    internal static void FocusTheBoxOnOpen(MenuItem menu, InlineBoxRow row)
    {
        menu.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.Source, menu))
            {
                row.FocusLater(() => menu.IsSubMenuOpen);
            }
        };
        QuickCommandStrip.FollowInFlyout(menu, new StripBoxFocus(row));
    }

    /// <summary>
    /// Focuses a flyout's box when the quick-command strip opens the entry's flyout, where the submenu
    /// (<see cref="MenuItem.IsSubMenuOpen"/>) never opens, unless the flyout closed before the focus ran.
    /// </summary>
    private sealed class StripBoxFocus(InlineBoxRow row) : IStripFlyoutContent
    {
        private bool _open;

        public void Opened(ItemCollection items, Action close)
        {
            _open = true;
            row.FocusLater(() => _open);
        }

        public void PointerInside(bool inside) { }

        public void Closed() => _open = false;
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
    /// The header every aircraft menu opens with, information rows only: the bold, disabled title — the callsign and the
    /// type the aircraft filed (<see cref="IMenuAircraft.DisplayAircraftType"/>), or the bare callsign with no aircraft
    /// model or no type — then the state line (<see cref="StateLineItem"/>) and the rows under it, the route summary
    /// (<see cref="RouteSummaryItem"/>) and the hold status (<see cref="HoldStatusItem"/>) the aircraft's own state raises.
    /// The caller adds the separator under it.
    /// </summary>
    public static void AddHeader(ItemCollection items, IMenuAircraft? aircraft, MenuContext context)
    {
        items.Add(
            new MenuItem
            {
                Header = HeaderTitle(aircraft, context.Callsign),
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        TryAdd(items, StateLineItem(aircraft));
        TryAdd(items, RouteSummaryItem(aircraft));
        TryAdd(items, HoldStatusItem(aircraft));
    }

    /// <summary>The free-text Command… and Note…, which ask the host for their flyouts, each where its entry builds.</summary>
    public static void AddCommandAndNote(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        TryAdd(items, TryLeaf(MenuIds.AircraftCommand, aircraft, context, host));
        TryAdd(items, TryLeaf(MenuIds.AircraftNote, aircraft, context, host));
    }

    /// <summary>The header title: <c>{callsign} · {type}</c>, or the bare callsign when there is no aircraft model or no type.</summary>
    private static string HeaderTitle(IMenuAircraft? aircraft, string callsign) =>
        ((aircraft is null) || string.IsNullOrWhiteSpace(aircraft.DisplayAircraftType)) ? callsign : $"{callsign} · {aircraft.DisplayAircraftType}";

    /// <summary>
    /// The header a point menu opens with: the bold, disabled title <c>{callsign} · {type} → {place}</c>
    /// (<see cref="PointPlace"/>), then on a radar point (<see cref="IsRadarPoint"/>) a dimmed row naming the point from the
    /// aircraft (<see cref="RadarPointDetail"/>), then a separator. A point strip puts its blank header line in under the
    /// title (<see cref="QuickCommandLabelLine.Ensure"/>).
    /// </summary>
    public static void AddPointHeader(ItemCollection items, IMenuAircraft aircraft, MenuPoint point, MenuContext context, IMenuHost host)
    {
        items.Add(
            new MenuItem
            {
                Header = $"{HeaderTitle(aircraft, context.Callsign)} → {PointPlace(point, host)}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        if (IsRadarPoint(point))
        {
            items.Add(DetailRow(RadarPointDetail(aircraft, point, host)));
        }

        items.Add(new Separator());
    }

    /// <summary>Whether <paramref name="point"/> is a radar map point: no taxi node and no runway surface under it.</summary>
    public static bool IsRadarPoint(MenuPoint point) => (point.Node is null) && (point.SurfaceRunways.Count == 0);

    /// <summary>
    /// Whether the point menu's header names <paramref name="point"/> by its fix-radial-distance: a radar point the host
    /// can describe.
    /// </summary>
    public static bool PointHeaderShowsFrd(MenuPoint point, IMenuHost host) =>
        IsRadarPoint(point) && (host.DescribePoint(point.Position) is not null);

    /// <summary>A disabled, dimmed label row under a header or an item.</summary>
    public static MenuItem DetailRow(string header) =>
        new()
        {
            Header = header,
            IsEnabled = false,
            FontSize = 11,
            Opacity = 0.8,
        };

    /// <summary>
    /// What the point menu's title says the click lands on: at a taxi node, <see cref="NodePlace"/>; on a runway surface,
    /// <c>runway {designator}</c> for the first runway's first end; anywhere else <c>this point</c>.
    /// </summary>
    private static string PointPlace(MenuPoint point, IMenuHost host)
    {
        if (point.Node is { } node)
        {
            return NodePlace(node, point.RunwayEnd, host);
        }

        if (point.SurfaceRunways.Count > 0)
        {
            return $"runway {RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(point.SurfaceRunways[0]).End1)}";
        }

        return ThisPoint;
    }

    private const string ThisPoint = "this point";

    /// <summary>
    /// A taxi node's place: a hold-short node a threshold click resolved to, <c>HS {runway} at {taxiway}</c> (or
    /// <c>HS {runway}</c> when the node sits on no named taxiway); a named stand, <c>parking {name}</c>; a named spot,
    /// <c>spot {name}</c>; any other node, the taxiways meeting at it, ordinal-sorted and joined with <c> / </c>, or
    /// <c>this point</c> when none does.
    /// </summary>
    private static string NodePlace(GroundNodeDto node, string? runwayEnd, IMenuHost host)
    {
        if ((node.Type == "RunwayHoldShort") && (runwayEnd is not null))
        {
            string runway = RunwayIdentifier.ToDisplayDesignator(runwayEnd);
            return host.GetHoldShortTaxiwayName(node) is { Length: > 0 } taxiway ? $"HS {runway} at {taxiway}" : $"HS {runway}";
        }

        switch (node)
        {
            case { Type: "Parking", Name: { Length: > 0 } stand }:
                return $"parking {stand}";
            case { Type: "Spot", Name: { Length: > 0 } spot }:
                return $"spot {spot}";
        }

        List<string> taxiways = [.. host.GetNodeTaxiwayNames(node).Order(StringComparer.Ordinal)];
        return (taxiways.Count > 0) ? string.Join(" / ", taxiways) : ThisPoint;
    }

    /// <summary>
    /// The radar point menu's second header row: <c>{FRD} · {d} nm, bearing {brg} from the aircraft</c>, or without the
    /// FRD when the host cannot name the point. The distance is great-circle, whole nm, one decimal under 1 nm; the
    /// bearing is magnetic, at the variation of the aircraft's position, as three digits.
    /// </summary>
    private static string RadarPointDetail(IMenuAircraft aircraft, MenuPoint point, IMenuHost host)
    {
        double distanceNm = GeoMath.DistanceNm(aircraft.Position, point.Position);
        MagneticHeading bearing = new TrueHeading(GeoMath.BearingTo(aircraft.Position, point.Position)).ToMagnetic(
            MagneticDeclination.GetDeclination(aircraft.Position)
        );
        string fromAircraft = $"{FormatDistanceNm(distanceNm)} nm, bearing {bearing.ToDisplayString()} from the aircraft";
        return host.DescribePoint(point.Position) is { } frd ? $"{frd} · {fromAircraft}" : fromAircraft;
    }

    /// <summary>A distance in nm as the radar point header shows it: one decimal while it rounds under 1 nm, else whole nm.</summary>
    private static string FormatDistanceNm(double distanceNm)
    {
        double tenths = Math.Round(distanceNm, 1, MidpointRounding.AwayFromZero);
        return (tenths < 1.0)
            ? tenths.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(distanceNm, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The header's one-line state row: what the aircraft is doing now, as segments joined with <c> · </c>. A landing
    /// roll shows <c>Landing · runway 28R · 62 kt</c>, another ground aircraft <c>Taxiing · S/RAMP · KSAN</c> (the
    /// taxiway else the parking spot, then the ground airport), an airborne one
    /// <c>Approach · 3,000 ft · 180 kt · KOAK rwy 30</c>. Each segment is left out when it has no value, and a numeric
    /// segment whose rounded value is zero counts as none. The row is the quick-command strip's header line
    /// (<see cref="QuickCommandLabelLine"/>), a disabled, dimmed label at rest. Null without an aircraft or when no segment
    /// is left.
    /// </summary>
    private static QuickCommandLabelLine? StateLineItem(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return null;
        }

        List<string> segments = aircraft.IsOnGround ? GroundStateSegments(aircraft) : AirborneStateSegments(aircraft);
        if (segments.Count == 0)
        {
            return null;
        }

        return new QuickCommandLabelLine(string.Join(" · ", segments));
    }

    /// <summary>
    /// The state line's segments for an aircraft on the ground: a landing roll (fixed-wing <c>Landing</c> or helicopter
    /// <c>Landing-H</c>), else the ground-movement state.
    /// </summary>
    private static List<string> GroundStateSegments(IMenuAircraft aircraft)
    {
        var segments = new List<string>();
        AddSegment(segments, PhaseDisplayNames.For(aircraft.CurrentPhase));

        if (aircraft.CurrentPhase is "Landing" or "Landing-H")
        {
            AddSegment(segments, string.IsNullOrEmpty(aircraft.AssignedRunway) ? "" : $"runway {aircraft.AssignedRunway}");
            AddSegment(segments, FormatSpeed(aircraft.GroundSpeedKnots));
            return segments;
        }

        string place = !string.IsNullOrEmpty(aircraft.CurrentTaxiway) ? aircraft.CurrentTaxiway : aircraft.ParkingSpot;
        AddSegment(segments, place);
        AddSegment(segments, aircraft.GroundAirportId ?? "");
        return segments;
    }

    /// <summary>The state line's segments for an airborne aircraft: the phase, altitude, speed and the assigned runway with its airport.</summary>
    private static List<string> AirborneStateSegments(IMenuAircraft aircraft)
    {
        var segments = new List<string>();
        AddSegment(segments, PhaseDisplayNames.For(aircraft.CurrentPhase));
        AddSegment(segments, FormatAltitude(aircraft.AltitudeFeet));
        AddSegment(segments, FormatSpeed(aircraft.IndicatedAirspeedKnots));

        if (!string.IsNullOrEmpty(aircraft.AssignedRunway))
        {
            bool departing = aircraft.CurrentPhase is "Takeoff" or "Takeoff-H" or "InitialClimb" or "DepartureProcedure";
            string airport = departing ? aircraft.Departure : aircraft.Destination;
            AddSegment(segments, string.IsNullOrEmpty(airport) ? $"rwy {aircraft.AssignedRunway}" : $"{airport} rwy {aircraft.AssignedRunway}");
        }

        return segments;
    }

    /// <summary>Adds <paramref name="segment"/> to <paramref name="segments"/> unless it is null or empty.</summary>
    private static void AddSegment(List<string> segments, string segment)
    {
        if (!string.IsNullOrEmpty(segment))
        {
            segments.Add(segment);
        }
    }

    /// <summary>The altitude segment, <c>3,000 ft</c>, rounded to the nearest 100 ft, or empty for an altitude that rounds to zero.</summary>
    private static string FormatAltitude(double feet)
    {
        long rounded = (long)Math.Round(feet / 100.0, MidpointRounding.AwayFromZero) * 100;
        return rounded == 0 ? "" : rounded.ToString("N0", CultureInfo.InvariantCulture) + " ft";
    }

    /// <summary>The speed segment, <c>180 kt</c>, rounded to whole knots, or empty for a speed that rounds to zero.</summary>
    private static string FormatSpeed(double knots)
    {
        long rounded = (long)Math.Round(knots, MidpointRounding.AwayFromZero);
        return rounded == 0 ? "" : rounded.ToString("N0", CultureInfo.InvariantCulture) + " kt";
    }

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
        string displayFixes = fixes.Count > maxDisplay ? string.Join(" ", fixes.Take(maxDisplay)) + " …" : string.Join(" ", fixes);
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
    /// already ends in one), then Delete. Every view's delayed-spawn menu keeps its own foot (<see cref="AddDelayedSpawn"/>).
    /// </summary>
    public static void AddFoot(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        AddBlockSeparator(items);
        items.Add(Delete(aircraft, context, host));
    }

    /// <summary>
    /// The sim-control items that close All Commands: a separator (unless the list already ends in one), Warp… where it applies
    /// (for a controllable airborne aircraft only, <see cref="AircraftCommandApplicability.IsAirborneControllable"/>), then
    /// "Release to live feed" when the aircraft was assumed from the feed
    /// (<see cref="AircraftCommandApplicability.CanUnassume"/>). Nothing, not even the separator, when neither applies.
    /// </summary>
    public static void AddSimControl(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (!IsApplicable(MenuIds.SimControlWarp, aircraft, context) && !IsApplicable(MenuIds.LiveTrafficUnassume, aircraft, context))
        {
            return;
        }

        AddBlockSeparator(items);
        AddIfApplicable(items, MenuIds.SimControlWarp, aircraft, context, host);
        AddIfApplicable(items, MenuIds.LiveTrafficUnassume, aircraft, context, host);
    }

    /// <summary>The Delete leaf that sends <c>DEL</c>.</summary>
    private static MenuItem Delete(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Leaf(MenuIds.SimControlDelete, aircraft, context, host);

    /// <summary>The "Edit flight plan" leaf when the aircraft's flight plan is editable, otherwise null.</summary>
    public static MenuItem? EditFlightPlan(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        IsApplicable(MenuIds.AircraftEditFlightPlan, aircraft, context) ? TryLeaf(MenuIds.AircraftEditFlightPlan, aircraft, context, host) : null;

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
    /// The All Commands Vectors group: four blocks, each under a bold label (<see cref="SectionLabel"/>) carrying its state
    /// text — Heading (<see cref="Heading"/>), Altitude (<see cref="Altitude"/>), Speed (<see cref="Speed"/>) and Route
    /// (<see cref="Route"/>) — each holding the rows its builder lists, in its order. A block with no rows is left out.
    /// Null when no block is left.
    /// </summary>
    public static MenuItem? Vectors(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = MenuCategories.Label(MenuCategory.Vectors) };
        foreach (
            MenuItem block in (MenuItem[])
                [Heading(aircraft, context, host), Altitude(aircraft, context, host), Speed(aircraft, context, host), Route(aircraft, context, host)]
        )
        {
            string label =
                block.Header as string
                ?? throw new InvalidOperationException($"The Vectors block has a {block.Header?.GetType().Name} header, not a label.");
            AddLabelledBlock(menu.Items, label, TakeItems(block));
        }

        return (menu.Items.Count > 0) ? menu : null;
    }

    /// <summary>
    /// The Track and coordination group: four blocks, each under a bold label — Track (<see cref="Track"/>'s rows), Data block
    /// (<see cref="DataBlock"/>'s rows), Squawk (the <see cref="Squawk"/> flyout) and Coordination (<see cref="Coordination"/>'s
    /// rows) — then after a separator Edit flight plan where it applies. A block with no rows is left out. A menu that cannot
    /// command the aircraft (<paramref name="commandsTheAircraft"/> false, a surface live-traffic shadow) gets only the Track,
    /// Data block and Coordination blocks. Null when nothing is left.
    /// </summary>
    public static MenuItem? TrackAndCoordination(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, bool commandsTheAircraft)
    {
        var menu = new MenuItem { Header = MenuCategories.Label(MenuCategory.TrackAndCoordination) };
        AddLabelledBlock(menu.Items, "Track", TakeItems(Track(aircraft, context, host)));
        AddLabelledBlock(menu.Items, "Data block", TakeItems(DataBlock(aircraft, context, host)));
        if (commandsTheAircraft)
        {
            AddLabelledBlock(menu.Items, "Squawk", [Squawk(aircraft, context, host)]);
        }

        AddLabelledBlock(menu.Items, "Coordination", TakeItems(Coordination(aircraft, context, host)));
        if (commandsTheAircraft && (EditFlightPlan(aircraft, context, host) is { } editFlightPlan))
        {
            AddSeparatedBlock(menu.Items, [editFlightPlan]);
        }

        return (menu.Items.Count > 0) ? menu : null;
    }

    /// <summary>
    /// The Pilot reports group, each block after a separator when one precedes it: while the phase shows the flight commands
    /// (<paramref name="hidesFlightCommands"/> false) Report field in sight and Report traffic in sight, then the Report when…
    /// flyout; then the Say flyout (<see cref="AskPilot"/>) for an aircraft that can be asked
    /// (<see cref="AircraftCommandApplicability.CanAskPilot"/>). Null when nothing applies.
    /// </summary>
    public static MenuItem? PilotReports(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, bool hidesFlightCommands)
    {
        var menu = new MenuItem { Header = MenuCategories.Label(MenuCategory.PilotReports) };
        if (!hidesFlightCommands)
        {
            var inSight = new MenuItem();
            TryAdd(inSight.Items, TryLeaf(MenuIds.ApproachReportFieldInSight, aircraft, context, host));
            TryAdd(inSight.Items, TryLeaf(MenuIds.ApproachReportTrafficInSight, aircraft, context, host));
            AddSeparatedBlock(menu.Items, TakeItems(inSight));
            if (TryLeaf(MenuIds.ApproachReportWhen, aircraft, context, host) is { } reportWhen)
            {
                AddSeparatedBlock(menu.Items, [reportWhen]);
            }
        }

        if (AircraftCommandApplicability.CanAskPilot(aircraft))
        {
            AddSeparatedBlock(menu.Items, [AskPilot(aircraft, context, host)]);
        }

        return (menu.Items.Count > 0) ? menu : null;
    }

    /// <summary>Adds a bold <paramref name="label"/> row, then <paramref name="rows"/>; nothing when there are no rows.</summary>
    private static void AddLabelledBlock(ItemCollection items, string label, List<Control> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        items.Add(SectionLabel(label));
        AddRange(items, rows);
    }

    /// <summary>Adds <paramref name="rows"/> after a separator when items precede them; nothing when there are no rows.</summary>
    private static void AddSeparatedBlock(ItemCollection items, List<Control> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        AddSeparatorIfNonEmpty(items);
        AddRange(items, rows);
    }

    /// <summary>
    /// Detaches every item of <paramref name="menu"/> and returns them in order, so they can sit in another menu (an item
    /// keeps its logical parent while it is in a collection).
    /// </summary>
    private static List<Control> TakeItems(MenuItem menu)
    {
        List<Control> rows = [.. menu.Items.OfType<Control>()];
        menu.Items.Clear();
        return rows;
    }

    /// <summary>
    /// The Vectors group's Heading block, as a submenu: present heading and the heading flyout's entries (Fly heading, Turn
    /// left, Turn right and the two degree turns). The header, the block's label, names the fix the aircraft is navigating
    /// to, else its assigned magnetic heading.
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

    /// <summary>
    /// The Vectors group's Altitude block, as a submenu: the Maintain altitude flyout, under a header (the block's label)
    /// naming the assigned altitude.
    /// </summary>
    public static MenuItem Altitude(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string header = aircraft?.AssignedAltitude is { } assigned ? $"Altitude (→ {MenuCatalog.FormatAltitude((int)assigned)})" : "Altitude";
        var menu = new MenuItem { Header = header };
        AddIfBuilt(menu.Items, MenuIds.AltitudeMaintain, aircraft, context, host);
        return menu;
    }

    /// <summary>
    /// The Vectors group's Speed block, as a submenu: the speed picker and input, resume normal speed and final approach
    /// speed, under a header (the block's label) naming the assigned speed.
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
    /// The Vectors group's Route block, as a submenu headed <c>Route</c>, or <c>Route (→ {fix})</c> while the aircraft is
    /// navigating to a fix: Direct to…, Append direct to… (while navigating), Proceed on course, the cross and depart fix
    /// flyouts, the join airway flyout, the two join-radial entries, each opening the join radial flyout on its own side,
    /// then Climb via SID.
    /// </summary>
    public static MenuItem Route(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string header = aircraft is { NavigatingTo.Length: > 0 } ? $"Route (→ {aircraft.NavigatingTo})" : "Route";
        var menu = new MenuItem { Header = header };
        TryAdd(menu.Items, TryLeaf(MenuIds.NavigationDirectTo, aircraft, context, host));
        AddIfApplicable(menu.Items, MenuIds.NavigationAppendDirectTo, aircraft, context, host);
        AddIfApplicable(menu.Items, MenuIds.NavigationOnCourse, aircraft, context, host);
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresCrossFix, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresDepartFix, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinAirway, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinRadialOutbound, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresJoinRadialInbound, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.ProceduresClimbViaSid, aircraft, context, host));
        return menu;
    }

    /// <summary>
    /// The holds, as a submenu whose rows the Approach group's last block takes: for an IFR aircraft in the air Hold at
    /// fix…, opening the holding-pattern flyout; hold at present position with left and right turns; then for an aircraft
    /// offered VFR maneuvers the left and right 360s at a fix, each opening the 360s flyout.
    /// </summary>
    public static MenuItem Hold(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Hold" };
        // The entry's applicability says only "in the air"; its flight-rules default is applied here as the quick strip applies it.
        MenuFlightRules holdPatternRules = MenuCatalog.Get(MenuIds.HoldPattern).DefaultFlightRules;
        if ((aircraft is not null) && QuickCommandResolver.MatchesFlightRules(holdPatternRules, MenuIds.HoldPattern, aircraft, context))
        {
            AddIfApplicable(menu.Items, MenuIds.HoldPattern, aircraft, context, host);
        }

        TryAdd(menu.Items, TryLeaf(MenuIds.HoldPresentLeft, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.HoldPresentRight, aircraft, context, host));
        AddIfApplicable(menu.Items, MenuIds.HoldFixLeft, aircraft, context, host);
        AddIfApplicable(menu.Items, MenuIds.HoldFixRight, aircraft, context, host);
        return menu;
    }

    /// <summary>
    /// The Approach group, in three blocks, each after a separator when one precedes it: the approach entries (each opening
    /// its verb's approach flyout) and the visual approach for an IFR aircraft (the Cleared approach flyout opened on its
    /// visual row); Join STAR (the tabbed STAR flyout, which holds Descend via STAR), Descend via STAR only while Join STAR
    /// offers no flyout, then PTAC; then the holds (<see cref="Hold"/>'s rows). The header names the active approach, else
    /// the expected one. Null when no block has a row.
    /// </summary>
    public static MenuItem? Approach(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        string label = MenuCategories.Label(MenuCategory.Approach);
        string header = aircraft switch
        {
            { ActiveApproachId: { Length: > 0 } active } => $"{label} ({active})",
            { ExpectedApproach: { Length: > 0 } expected } => $"{label} (exp: {expected})",
            _ => label,
        };
        var menu = new MenuItem { Header = header };
        var pickers = new MenuItem();
        foreach (string id in MenuCatalog.ApproachPickerIds)
        {
            TryAdd(pickers.Items, TryLeaf(id, aircraft, context, host));
        }

        AddIfApplicable(pickers.Items, MenuIds.ApproachClearedVisual, aircraft, context, host);
        AddSeparatedBlock(menu.Items, TakeItems(pickers));

        var star = new MenuItem();
        MenuItem? joinStar = TryLeaf(MenuIds.ProceduresJoinStar, aircraft, context, host);
        TryAdd(star.Items, joinStar);
        if (joinStar is not { IsEnabled: true })
        {
            TryAdd(star.Items, TryLeaf(MenuIds.ProceduresDescendViaStar, aircraft, context, host));
        }

        TryAdd(star.Items, TryLeaf(MenuIds.ProceduresPtac, aircraft, context, host));
        AddSeparatedBlock(menu.Items, TakeItems(star));
        AddSeparatedBlock(menu.Items, TakeItems(Hold(aircraft, context, host)));
        return (menu.Items.Count > 0) ? menu : null;
    }

    /// <summary>
    /// The Runway group (the tower's clearances), state-aware: the departure clearances, then the landing and option
    /// clearances with go around and cancel landing clearance while any of cleared to land, go around or cancel landing
    /// clearance applies, then the runway exits after touchdown, each block after a separator when items precede it. Null
    /// when nothing applies, so the caller omits the submenu.
    /// </summary>
    public static MenuItem? Tower(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = MenuCategories.Label(MenuCategory.Runway) };
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
    /// The Pattern group, state-aware: the pattern entries that apply, each its runway flyout, then the leg turns, the
    /// spacing adjustments, the orbits, Circle airport, then Make left closed traffic, Make right closed traffic and Follow
    /// traffic…, each block after a separator when it has an item and items precede it. Null when nothing applies, so the
    /// caller omits the submenu.
    /// </summary>
    public static MenuItem? Pattern(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = MenuCategories.Label(MenuCategory.Pattern) };
        foreach (string id in PatternEntryIds)
        {
            if (IsApplicable(id, aircraft, context))
            {
                TryAdd(menu.Items, TryLeaf(id, aircraft, context, host));
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

    /// <summary>
    /// The Pattern group's maneuver blocks, in menu order: leg turns, spacing, orbits, Circle airport, then closed traffic
    /// and Follow traffic….
    /// </summary>
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
        [MenuIds.PatternMakeLeftTraffic, MenuIds.PatternMakeRightTraffic, MenuIds.PatternFollow],
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
    /// Adds the entry's item when the entry applies to the aircraft, otherwise nothing, and returns whether it added one.
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
    /// The For section, which opens the clicked aircraft's menu under the header while another aircraft is selected
    /// (<see cref="MenuContext.PreviousSelection"/>): the bold label <c>For {selected} (selected)</c>, then for an airborne
    /// pair (<see cref="RelativeTraffic.OffersAirborneRelative"/>) the dimmed line saying where the clicked aircraft is
    /// from the selected one (<see cref="RelativeGeometry.Describe(IMenuAircraft, IMenuAircraft)"/>), Report in sight, and
    /// Follow once the selected aircraft has reported the clicked one in sight; for a ground pair
    /// (<see cref="RelativeTraffic.OffersGroundRelative"/>) the dimmed line saying what the clicked aircraft is doing,
    /// where, how far ahead or behind and whether it is on the selected one's taxi route
    /// (<see cref="RelativeGeometry.DescribeGround"/>, <see cref="IMenuHost.IsOnTaxiRoute"/>), Follow and Give way to.
    /// Each item sends as the selected aircraft and shows its quick-command glyph. Then a separator and the bold label
    /// <c>For {clicked}</c> over the clicked aircraft's own items. Adds nothing when there is no previous selection or
    /// neither pair applies.
    /// </summary>
    public static void AddForSection(ItemCollection items, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if ((context.PreviousSelection is not { } selected) || (aircraft is null))
        {
            return;
        }

        bool airborne = RelativeTraffic.OffersAirborneRelative(aircraft, context);
        if (!airborne && !RelativeTraffic.OffersGroundRelative(aircraft, context))
        {
            return;
        }

        items.Add(SectionLabel($"For {selected.Callsign} (selected)"));
        if (airborne)
        {
            items.Add(DetailRow(RelativeGeometry.Describe(selected, aircraft)));
            AddForItem(items, MenuIds.RelativeReportInSight, aircraft, context, host);
            AddForItem(items, MenuIds.RelativeFollow, aircraft, context, host);
        }
        else
        {
            items.Add(DetailRow(RelativeGeometry.DescribeGround(selected, aircraft, host.IsOnTaxiRoute(selected.Callsign, aircraft.Callsign))));
            AddForItem(items, MenuIds.GroundRelativeFollow, aircraft, context, host);
            AddForItem(items, MenuIds.GroundRelativeGiveWay, aircraft, context, host);
        }

        items.Add(new Separator());
        items.Add(SectionLabel($"For {context.Callsign}"));
    }

    /// <summary>The quick-command glyph each For-section item shows, by its catalog id.</summary>
    private static readonly Dictionary<string, string> ForSectionGlyphIds = new(StringComparer.Ordinal)
    {
        [MenuIds.RelativeReportInSight] = MenuIds.ApproachReportTrafficInSight,
        [MenuIds.RelativeFollow] = MenuIds.GroundFollow,
        [MenuIds.GroundRelativeFollow] = MenuIds.GroundFollow,
        [MenuIds.GroundRelativeGiveWay] = MenuIds.GroundGiveWay,
    };

    /// <summary>Adds a For-section item where its predicate allows it, with its glyph as the item's icon.</summary>
    private static void AddForItem(ItemCollection items, string id, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        if (!IsApplicable(id, aircraft, context) || (TryLeaf(id, aircraft, context, host) is not { } item))
        {
            return;
        }

        if (QuickCommandGlyphs.For(ForSectionGlyphIds[id]) is { } glyph)
        {
            item.Icon = QuickCommandStrip.GlyphIcon(glyph);
        }

        items.Add(item);
    }

    /// <summary>A bold, disabled label naming what the items under it are: the aircraft they command, or a traffic list's section.</summary>
    internal static MenuItem SectionLabel(string header) =>
        new()
        {
            Header = header,
            IsEnabled = false,
            FontWeight = FontWeight.Bold,
        };

    /// <summary>
    /// The delayed-spawn items <see cref="AircraftMenuBuilder"/> places on every view, in order: Spawn now, the Change
    /// spawn delay submenu and Delete. They replace the phase-aware command groups when the aircraft is a delayed spawn.
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
    /// The assume-selected live-traffic item, placed after the Delete item: a separator and
    /// "Assume selected live traffic (N)" when the click's selection (<see cref="MenuClick.Selection"/>) holds two or
    /// more assumable shadows (<see cref="AircraftCommandApplicability.CanAssume"/>) to assume at once and the entry's
    /// own gate allows it, otherwise nothing. The item itself comes from <see cref="MenuCatalog.BuildAssumeSelected"/>
    /// rather than from its own entry's builder.
    /// </summary>
    public static void AddAssumeSelected(ContextMenu menu, MenuContext context, IMenuHost host)
    {
        List<string> selectedShadows = [.. context.Click.Selection.Where(AircraftCommandApplicability.CanAssume).Select(a => a.Callsign)];
        if ((selectedShadows.Count < 2) || !IsApplicable(MenuIds.LiveTrafficAssumeSelected, null, context))
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

    /// <summary>The Favorite Commands submenu, which the catalog entry has the host build.</summary>
    public static MenuItem Favorites(IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Leaf(MenuIds.FavoritesMenu, aircraft, context, host);

    /// <summary>The entry's item, or null when the entry's own state hides it.</summary>
    private static MenuItem? TryLeaf(string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        MenuCatalog.Get(id).Build(aircraft, context, host);

    /// <summary>The entry's item, which must exist (Delete, Favorites).</summary>
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

    /// <summary>Adds the entry's item, or nothing when its own state hides it (the item is null).</summary>
    private static void AddIfBuilt(ItemCollection items, string id, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        TryAdd(items, TryLeaf(id, aircraft, context, host));
}
