using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The groups more than one surface builds — the menu header (title, Command…, Note…), live traffic, track, squawk, ask
/// pilot, coordination, data block, favorites, the menu foot (warp, release to live feed, delete), and the flight groups
/// heading, altitude, speed, navigation, hold, approach, procedures, tower and pattern — assembled from
/// <see cref="MenuCatalog"/> entries. Every surface offers the input pickers — handoff, point out, squawk code, custom
/// say. The relative items (<see cref="AddRelative"/>) are a group of their own. Whether a group is offered at all stays with the
/// caller. A group whose items are canvas-only in nature — the Display submenu — takes the surface's prebuilt items
/// instead of the entries: the surface builds them with <see cref="CanvasMenuItems"/> from its own canvas state and only
/// the placing stays here.
/// </summary>
public static class SharedMenuGroups
{
    /// <summary>The Track submenu: track and drop, then the handoff and pointout items.</summary>
    public static MenuItem Track(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Track" };
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackTrack, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackDrop, aircraft, context, host));
        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackAcceptHandoff, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackInitiateHandoff, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackCancelHandoff, aircraft, context, host));
        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackPointOut, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.TrackAcknowledgePointout, aircraft, context, host));
        return menu;
    }

    /// <summary>The Squawk submenu: the code items, then Ident.</summary>
    public static MenuItem Squawk(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Squawk" };
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkCode, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkRandom, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkVfr, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkNormal, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkStandby, aircraft, context, host));
        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.SquawkIdent, aircraft, context, host));
        return menu;
    }

    /// <summary>The "Ask pilot to say…" submenu: the say queries and the free-text one.</summary>
    public static MenuItem AskPilot(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Ask pilot to say…" };
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotAltitude, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotHeading, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotSpeed, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotMach, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotPosition, aircraft, context, host));
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotExpectedApproach, aircraft, context, host));
        menu.Items.Add(new Separator());
        TryAdd(menu.Items, TryLeaf(MenuIds.AskPilotCustom, aircraft, context, host));
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
    /// The header every aircraft menu opens with: the bold, disabled title — the callsign and the type the aircraft filed
    /// (<see cref="IMenuAircraft.DisplayAircraftType"/>), or the bare callsign with no aircraft model or no type — then
    /// the state line (<see cref="StateLineItem"/>) and the rows under it, the route summary (<see cref="RouteSummaryItem"/>)
    /// and the hold status (<see cref="HoldStatusItem"/>) the aircraft's own state raises, then the release items every
    /// view offers where they apply (Release (HFR) while the aircraft is held for release, then Check release window while
    /// it has a call-for-release window), then a separator, the free-text Command… and Note… (which ask the host for its
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
        TryAdd(items, StateLineItem(aircraft));
        TryAdd(items, RouteSummaryItem(aircraft));
        TryAdd(items, HoldStatusItem(aircraft));

        AddIfApplicable(items, MenuIds.CoordinationReleaseHeld, aircraft, context, host);
        AddIfApplicable(items, MenuIds.CoordinationCheckReleaseWindow, aircraft, context, host);

        items.Add(new Separator());
        TryAdd(items, TryLeaf(MenuIds.AircraftCommand, aircraft, context, host));
        TryAdd(items, TryLeaf(MenuIds.AircraftNote, aircraft, context, host));
        items.Add(new Separator());
    }

    /// <summary>The header title: <c>{callsign} · {type}</c>, or the bare callsign when there is no aircraft model or no type.</summary>
    private static string HeaderTitle(IMenuAircraft? aircraft, string callsign) =>
        ((aircraft is null) || string.IsNullOrWhiteSpace(aircraft.DisplayAircraftType)) ? callsign : $"{callsign} · {aircraft.DisplayAircraftType}";

    /// <summary>
    /// The header a point menu opens with: the bold, disabled title <c>{callsign} · {type} → {place}</c>
    /// (<see cref="PointPlace"/>), then on a radar point (<see cref="IsRadarPoint"/>) a dimmed row naming the point from the
    /// aircraft (<see cref="RadarPointDetail"/>), then a separator.
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
    /// segment whose rounded value is zero counts as none. The row is a disabled, dimmed label. Null without an aircraft
    /// or when no segment is left.
    /// </summary>
    private static MenuItem? StateLineItem(IMenuAircraft? aircraft)
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

        return new MenuItem
        {
            Header = string.Join(" · ", segments),
            IsEnabled = false,
            FontSize = 11,
            Opacity = 0.8,
        };
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
    /// The sim-control items that close All Commands: a separator (unless the list already ends in one), Warp… where it
    /// applies (every view offers it, never a surface live-traffic shadow), then "Release to live feed" when the aircraft
    /// was assumed from the feed (<see cref="AircraftCommandApplicability.CanUnassume"/>). Nothing, not even the
    /// separator, when neither applies.
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
    /// The Approach submenu: the approach clearances (each with its "(other)" grouped picker beside a default approach),
    /// the visual approach (with its "(other)" runway picker beside a default runway), the in-sight requests, then the
    /// Report when… submenu. The header names the active approach, else the expected one.
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
        foreach (string id in MenuCatalog.ApproachPickerIds)
        {
            TryAdd(menu.Items, TryLeaf(id, aircraft, context, host));
            AddCompanion(menu.Items, MenuCatalog.BuildApproachOther(id, aircraft, context, host));
        }

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
    /// The aircraft list's multi-selection live-traffic item, placed after the Delete item: a separator and
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

    /// <summary>
    /// Taxi to runway on a runway-surface point: one <c>Taxi to {end}</c> submenu per end of each runway under the click,
    /// when the entry's own gate allows it, otherwise nothing. The items come from
    /// <see cref="MenuCatalog.BuildTaxiToRunwayEnds"/> rather than from the entry's builder, since one click offers several.
    /// </summary>
    public static void AddTaxiToRunwayEnds(ItemCollection items, IMenuAircraft aircraft, MenuContext context, IMenuHost host)
    {
        if ((context.Click.Point is not { } point) || !IsApplicable(MenuIds.PointTaxiToRunway, aircraft, context))
        {
            return;
        }

        foreach (MenuItem end in MenuCatalog.BuildTaxiToRunwayEnds(point, context, host))
        {
            items.Add(end);
        }
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
