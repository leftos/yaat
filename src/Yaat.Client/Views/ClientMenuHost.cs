using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

/// <summary>
/// The one <see cref="IMenuHost"/> every view builds per right-click, with the control the menu opened on as
/// <paramref name="anchor"/>. Commands go through <see cref="MainViewModel.SendCommandForViewAsync"/>, so a failed send
/// is shown in the status line on every view; the popups and flyouts open through <see cref="MenuPopups"/> at the
/// pointer on the anchor; the session, favorites, RPO items, ground traffic and multi-selection assume come from the main
/// view model; fix names and field elevations from its primary radar view model; the ground choices from its primary
/// ground view model, which owns the layout the other ground windows mirror; and a route preview shows on every ground
/// view. Route drawing and tug moves start on the primary ground view.
/// </summary>
internal sealed class ClientMenuHost(MainViewModel main, AircraftModel? aircraft, Control anchor) : IMenuHost
{
    private static readonly ILogger Log = AppLog.CreateLogger("ClientMenuHost");

    /// <summary>The most aircraft the Follow… and Give way to… submenus list.</summary>
    private const int MaxGroundTraffic = 12;

    public MenuSession Session => SessionOf(main);

    /// <summary>
    /// Every family the client serves. The capability flags stay declared, all of them, until the catalog stops reading
    /// them.
    /// </summary>
    public MenuHostCapabilities Capabilities =>
        MenuHostCapabilities.InputPopup
        | MenuHostCapabilities.ListPicker
        | MenuHostCapabilities.FilteredListPicker
        | MenuHostCapabilities.Warp
        | MenuHostCapabilities.FlightPlanEditor
        | MenuHostCapabilities.DrawRoute
        | MenuHostCapabilities.GroundMovement
        | MenuHostCapabilities.MultiSelectAssume;

    public Task SendAsync(string callsign, string command, string initials) => main.SendCommandForViewAsync(callsign, command, initials);

    public void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task> onSubmit) =>
        MenuPopups.ShowInput(anchor, placeholder, initialText, caretIndex, blank, onSubmit);

    /// <summary>The point as the primary radar view model's fixes name it (<see cref="FrdResolver.ToFrd"/>); null before they load.</summary>
    public string? DescribePoint(LatLon position) => main.Radar.Fixes is { } fixes ? FrdResolver.ToFrd(position.Lat, position.Lon, fixes) : null;

    /// <summary>
    /// The primary ground view model's routes from the aircraft's nearest node to <paramref name="node"/>, with the
    /// aircraft's own category and wake class. A named Spot is the taxi's spot destination, a named Parking or Helipad
    /// its stand; the destination runway is <paramref name="runwayEnd"/>, else a hold-short node's runway End1.
    /// </summary>
    public IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd)
    {
        if (FindAircraft(callsign) is not { } ac)
        {
            return [];
        }

        GroundViewModel ground = main.Ground;
        if (ground.GetAircraftNearestNodeId(ac) is not { } fromNodeId)
        {
            return [];
        }

        TaxiSpotDestination? spot = SpotDestinationFor(node);
        string? destRunway = DestinationRunwayFor(node, runwayEnd);
        List<TaxiRoute> routes = ground.FindRoutesToNode(fromNodeId, node.Id, GroundViewModel.CategoryFor(ac), GroundViewModel.WakeClassFor(ac));
        if (routes.Count == 0)
        {
            return [new MenuCommandChoice("No route found", null, null, [])];
        }

        List<MenuCommandChoice> perRoute = [.. routes.Select(route => RouteChoice(ground, route, spot, destRunway)).OfType<MenuCommandChoice>()];
        return (routes.Count == 1) ? perRoute : [new MenuCommandChoice("Taxi here", null, null, perRoute)];
    }

    /// <summary>The taxi's named destination at <paramref name="node"/>: a named Spot (<c>$</c>), a named Parking or Helipad stand (<c>@</c>); else null.</summary>
    private static TaxiSpotDestination? SpotDestinationFor(GroundNodeDto node) =>
        node.Type switch
        {
            "Spot" when node.Name is not null => new TaxiSpotDestination(node.Name, IsTaxiSpot: true),
            "Parking" or "Helipad" when node.Name is not null => new TaxiSpotDestination(node.Name, IsTaxiSpot: false),
            _ => null,
        };

    /// <summary>The runway a taxi to <paramref name="node"/> ends at: <paramref name="runwayEnd"/> when a threshold click names one, else a hold-short node's runway End1, else none.</summary>
    private static string? DestinationRunwayFor(GroundNodeDto node, string? runwayEnd)
    {
        if (runwayEnd is not null)
        {
            return runwayEnd;
        }

        return ((node.Type == "RunwayHoldShort") && (node.RunwayId is not null)) ? RunwayIdentifier.Parse(node.RunwayId).End1 : null;
    }

    /// <summary>
    /// One route's Taxi choice. To a runway: a submenu previewing the route over the departure and hold-short variants
    /// (null when there are none). Otherwise one crossing variant is a leaf sending it, several a submenu previewing the
    /// route over them, and none leaves the route out.
    /// </summary>
    private static MenuCommandChoice? RouteChoice(GroundViewModel ground, TaxiRoute route, TaxiSpotDestination? spot, string? destRunway)
    {
        string label = $"Taxi {(spot is not null ? $"to {spot.Name} {ground.GetTaxiwayDisplayName(route)}" : ground.GetTaxiwayDisplayName(route))}";
        if (destRunway is not null)
        {
            List<(string Label, string Command, TaxiRoute Preview)?> destVariants = ground.BuildTaxiDestVariants(route, destRunway, spot);
            if (destVariants.Count == 0)
            {
                return null;
            }

            return new MenuCommandChoice(
                label,
                null,
                route,
                [
                    .. destVariants.Select(v =>
                        v is { } variant ? new MenuCommandChoice(variant.Label, variant.Command, variant.Preview, []) : MenuCommandChoice.Separator
                    ),
                ]
            );
        }

        List<(string Label, string Command, TaxiRoute Preview)> variants = ground.BuildTaxiCrossingVariants(route, spot, pathOverride: null);
        return variants.Count switch
        {
            0 => null,
            1 => new MenuCommandChoice(label, variants[0].Command, variants[0].Preview, []),
            _ => new MenuCommandChoice(label, null, route, [.. variants.Select(v => new MenuCommandChoice(v.Label, v.Command, v.Preview, []))]),
        };
    }

    /// <summary>
    /// The Custom taxi… seed for <paramref name="node"/>: a named stand or spot after the caret (<c>TAXI  @STAND</c>,
    /// <c>TAXI  $SPOT</c>), a hold-short node's runway before it (<c>RWY 30 TAXI </c>, caret at the end), else the node's
    /// first taxiway after it (<c>TAXI  E</c>) or a bare <c>TAXI </c>.
    /// </summary>
    public MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node)
    {
        const string taxiPrefix = "TAXI ";
        if (SpotDestinationFor(node) is { } spot)
        {
            return new MenuTextSeed($"{taxiPrefix} {spot.Token}", taxiPrefix.Length);
        }

        switch (node.Type)
        {
            case "RunwayHoldShort" when node.RunwayId is not null:
                string runwayText = $"RWY {RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(node.RunwayId).End1)} {taxiPrefix}";
                return new MenuTextSeed(runwayText, runwayText.Length);

            default:
                List<string> names = main.Ground.GetNodeTaxiwayNames(node.Id);
                return (names.Count > 0)
                    ? new MenuTextSeed($"{taxiPrefix} {names[0]}", taxiPrefix.Length)
                    : new MenuTextSeed(taxiPrefix, taxiPrefix.Length);
        }
    }

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        MenuPopups.ShowList(anchor, items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        MenuPopups.ShowFilteredList(anchor, sortedNames, priorityItems, onPick);

    /// <summary>The primary radar view model's fix names: every fix in the navigation database once it is loaded, else null.</summary>
    public string[]? FixNames => main.Radar.FixNames;

    /// <summary>
    /// The navdata elevation of <paramref name="destination"/>, else of the scenario's primary airport, from the primary
    /// radar view model.
    /// </summary>
    public double GetFieldElevation(string? destination) => main.Radar.GetFieldElevation(destination);

    public void ShowWarpPopup(string callsign, string frd, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        MenuPopups.ShowWarp(anchor, new MenuPopups.WarpSeed(callsign, frd, heading, altitude, speed), onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) =>
        MenuPopups.ShowCommand(anchor, callsign, command => main.SendGatedCommandForViewAsync(FindAircraft(callsign), callsign, command, initials));

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        MenuPopups.ShowNote(anchor, callsign, currentNote, sendCommand);

    public void OpenFlightPlanEditor(string callsign) => OpenFlightPlanEditor(main, aircraft, callsign);

    /// <summary>
    /// Opens the flight-plan editor on <paramref name="callsign"/>'s aircraft — <paramref name="aircraft"/> when it is that
    /// one, else that aircraft in <paramref name="main"/>'s list — and logs instead when it is no longer in the list.
    /// </summary>
    internal static void OpenFlightPlanEditor(MainViewModel main, AircraftModel? aircraft, string callsign)
    {
        if (FindAircraft(main, aircraft, callsign) is not { } ac)
        {
            Log.LogWarning("Edit flight plan: {Callsign} is no longer in the aircraft list; no editor opened", callsign);
            return;
        }

        FlightPlanEditorManager.Open(ac, main);
    }

    /// <summary>
    /// Shows the primary ground view (<see cref="MainViewModel.ShowPrimaryGroundView"/>), then starts drawing a taxi route
    /// for <paramref name="callsign"/> on it.
    /// </summary>
    public void EnterDrawRoute(string callsign)
    {
        if (FindAircraft(callsign) is not { } ac)
        {
            Log.LogWarning("Draw taxi route: {Callsign} is no longer in the aircraft list; no route drawing started", callsign);
            return;
        }

        main.ShowPrimaryGroundView();
        main.Ground.StartDrawRoute(ac);
    }

    /// <summary>
    /// Shows the primary ground view (<see cref="MainViewModel.ShowPrimaryGroundView"/>), then starts drawing a tug move
    /// for <paramref name="callsign"/> on it.
    /// </summary>
    public void EnterPushRoute(string callsign)
    {
        if (FindAircraft(callsign) is not { } ac)
        {
            Log.LogWarning("Push route: {Callsign} is no longer in the aircraft list; no tug move started", callsign);
            return;
        }

        main.ShowPrimaryGroundView();
        main.Ground.StartPushRoute(ac);
    }

    /// <summary>
    /// The other on-ground aircraft in the main view model's list, nearest <paramref name="callsign"/> first and at most
    /// <see cref="MaxGroundTraffic"/>; empty when <paramref name="callsign"/> is not in the list.
    /// </summary>
    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign)
    {
        if (FindAircraft(callsign) is not { } ac)
        {
            return [];
        }

        return
        [
            .. main
                .Aircraft.Where(other => (other.Callsign != callsign) && other.IsOnGround)
                .OrderBy(other => GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, other.Position.Lat, other.Position.Lon))
                .Take(MaxGroundTraffic)
                .Select(other => other.Callsign),
        ];
    }

    /// <summary>The primary ground view model's hold-short targets on the aircraft's route, each sending <c>HS</c> with its route preview.</summary>
    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign)
    {
        if (FindAircraft(callsign) is not { } ac)
        {
            return [];
        }

        GroundViewModel ground = main.Ground;
        return
        [
            .. ground
                .GetHoldShortTargets(ac)
                .Select(t => new MenuCommandChoice(t.DisplayName, $"HS {t.Target}", ground.FindHoldShortPreviewRoute(ac, t.Target), [])),
        ];
    }

    /// <summary>Previews <paramref name="route"/> on every ground view, so it shows on whichever ground window is open.</summary>
    public void SetRoutePreview(TaxiRoute? route)
    {
        foreach (GroundViewModel ground in main.AllGroundViews)
        {
            ground.PreviewRoute = route;
        }
    }

    /// <summary>The primary ground view model's pushback facings at the aircraft's node, each sending <c>PUSH FACE</c>.</summary>
    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) =>
        FindAircraft(callsign) is { } ac ? main.Ground.GetPushbackFaceChoices(ac) : [];

    /// <summary>The primary ground view model's nearest named stands for the aircraft, each sending <c>PUSH</c>.</summary>
    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) =>
        FindAircraft(callsign) is { } ac ? main.Ground.GetPushbackToChoices(ac) : [];

    /// <summary>The primary ground view model's preset taxi routes walkable from the aircraft's node, each sending <c>TAXI</c>.</summary>
    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) =>
        FindAircraft(callsign) is { } ac ? main.Ground.GetPresetTaxiChoices(ac) : [];

    /// <summary>Assumes the selected shadows through the main view model, which owns the bulk-assume call.</summary>
    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) => main.AssumeSelectedLiveTrafficAsync([.. callsigns]);

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);

    public IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns) => RpoItems(main, callsigns);

    /// <summary>The controller's session settings as <paramref name="main"/> holds them.</summary>
    internal static MenuSession SessionOf(MainViewModel main) =>
        new(main.Preferences.UserInitials, main.SessionSoloTrainingMode, main.VfrCommandsForIfr);

    /// <summary>
    /// The room's control items for <paramref name="callsigns"/> as <see cref="MainViewModel.BuildRpoMenuItems"/> builds
    /// them, detached from the scratch menu they were built into so the caller can place them.
    /// </summary>
    internal static IReadOnlyList<Control> RpoItems(MainViewModel main, IReadOnlyList<string> callsigns)
    {
        var scratch = new ContextMenu();
        main.BuildRpoMenuItems(scratch, [.. callsigns]);
        List<Control> items = [.. scratch.Items.OfType<Control>()];
        scratch.Items.Clear();
        return items;
    }

    /// <summary>The menu's own aircraft when it is <paramref name="callsign"/>'s, else that aircraft in the main view model's list.</summary>
    private AircraftModel? FindAircraft(string callsign) => FindAircraft(main, aircraft, callsign);

    private static AircraftModel? FindAircraft(MainViewModel main, AircraftModel? aircraft, string callsign) =>
        ((aircraft is not null) && string.Equals(aircraft.Callsign, callsign, StringComparison.Ordinal))
            ? aircraft
            : main.Aircraft.FirstOrDefault(a => string.Equals(a.Callsign, callsign, StringComparison.Ordinal));
}
