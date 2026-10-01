using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Radar.Flyouts;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

/// <summary>
/// The radar's <see cref="IMenuHost"/>, built per right-click: commands go through the radar view model's send path,
/// input, list, filtered-list and warp pickers open the radar's popups, fix names, field elevations and route drawing
/// come from the radar view model, the display items read and drive the radar canvas, and favorites are built for the
/// right-clicked aircraft model.
/// </summary>
internal sealed class RadarMenuHost(RadarView view, RadarViewModel radar, MainViewModel? main, AircraftModel? aircraft) : IMenuHost
{
    /// <summary>The message both taxi-route members throw: only the ground view draws taxi routes.</summary>
    private const string NoTaxiRouteItem = "The radar menu has no taxi route item; only the ground view draws taxi routes";

    /// <summary>The message both hidden-datablock members throw: only the ground view's menu hides data blocks.</summary>
    private const string NoHideDataBlockItem = "The radar menu has no hide datablock item; only the ground view hides data blocks from its menu";

    /// <summary>The message the ground-traffic member throws: only the ground view offers the follow and give-way submenus.</summary>
    private const string NoGroundTrafficItems = "The radar menu has no ground follow or give way items; only the ground view lists ground traffic";

    /// <summary>The message both hold-short members throw: only the ground view offers hold short and previews its route.</summary>
    private const string NoHoldShortItem = "The radar menu has no hold short item; only the ground view offers hold short and previews taxi routes";

    public Task SendAsync(string callsign, string command, string initials) => radar.SendRawCommandAsync(callsign, initials, command);

    public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) =>
        Dispatcher.UIThread.Post(() => view.ShowInputPopup(placeholder, onSubmit));

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        Dispatcher.UIThread.Post(() => view.ShowListPopup(items, selected, onPick));

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        Dispatcher.UIThread.Post(() => view.ShowFilteredListPopup(sortedNames, onPick, priorityItems));

    public string[]? FixNames => radar.FixNames;

    public double GetFieldElevation(string? destination) => radar.GetFieldElevation(destination);

    public void EnterDrawRoute(string callsign) => radar.EnterDrawRoute(callsign);

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        Dispatcher.UIThread.Post(() => view.ShowWarpPopup(callsign, "", heading, altitude, speed, (frd, h, a, s) => _ = onSubmit(frd, h, a, s)));

    public void OpenFlightPlanEditor() =>
        throw new NotSupportedException("The radar menu has no flight-plan item; Ctrl-clicking an aircraft opens the editor");

    public bool IsMinified(string callsign) => view.Canvas.IsMinified(callsign);

    public void ToggleMinified(string callsign) => view.Canvas.ToggleMinifiedDataBlock(callsign);

    public bool HasManualDataBlockOffset(string callsign) => view.Canvas.HasManualDataBlockOffset(callsign);

    public void ResetDataBlockOffset(string callsign) => view.Canvas.ResetDataBlockOffset(callsign);

    public bool IsPathShown(string callsign) => radar.IsPathShown(callsign);

    public void ToggleShowPath(string callsign) => radar.ToggleShowPath(callsign);

    public MenuMeasureState GetMeasureState()
    {
        if (radar.Measure is not { } measure)
        {
            return MenuMeasureState.None;
        }

        return measure.Anchor is null ? MenuMeasureState.NoAnchor : MenuMeasureState.HasAnchor;
    }

    public void MeasurePickOnAircraft(string callsign)
    {
        if (radar.Measure is { } measure)
        {
            measure.Pick(RblEndpoint.OnAircraft(callsign), RadarViewModel.MeasureView, radar.MeasureTrackLookup, RadarViewModel.MeasureUnits);
        }
    }

    public TaxiRouteDisplayMode GetTaxiRouteMode(string callsign) => throw new NotSupportedException(NoTaxiRouteItem);

    public void SetTaxiRouteMode(string callsign, TaxiRouteDisplayMode mode) => throw new NotSupportedException(NoTaxiRouteItem);

    public bool IsDataBlockHidden(string callsign) => throw new NotSupportedException(NoHideDataBlockItem);

    public void ToggleHiddenDataBlock(string callsign) => throw new NotSupportedException(NoHideDataBlockItem);

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => throw new NotSupportedException(NoGroundTrafficItems);

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => throw new NotSupportedException(NoHoldShortItem);

    public void SetRoutePreview(TaxiRoute? route) => throw new NotSupportedException(NoHoldShortItem);

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}

/// <summary>
/// The ground view's <see cref="IMenuHost"/>, built per right-click: commands go through the ground view model's send
/// path, the taxi-route mode reads and drives the ground view model, the data-block hide and reset read and drive the
/// ground canvas, the measure item latches the ground view model's measurement, route drawing starts a taxi route for
/// the right-clicked aircraft, the follow and give-way submenus list the main view model's other ground traffic, hold
/// short asks the ground view model for the route's targets and previews the route to one on hover, and favorites are
/// built for the right-clicked aircraft model. The ground has no input,
/// list, filtered-list or warp popup, no fix or altitude picker, no flight-plan item, and no mini data block or nav
/// route.
/// </summary>
internal sealed class GroundMenuHost(GroundView view, GroundViewModel ground, MainViewModel? main, AircraftModel? aircraft) : IMenuHost
{
    public Task SendAsync(string callsign, string command, string initials) => ground.SendRawCommandAsync(callsign, initials, command);

    public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) =>
        throw new NotSupportedException("The ground view has no input popup; ground menus never build input pickers");

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        throw new NotSupportedException("The ground view has no list popup; ground menus never build list pickers");

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        throw new NotSupportedException("The ground view has no filtered-list popup; ground menus never build fix pickers");

    // Throws rather than returning null: null would silently pick the free-text fix tier, hiding a ground menu that started building fix pickers.
    public string[]? FixNames => throw new NotSupportedException("The ground view offers no fix pickers; ground menus never build them");

    public double GetFieldElevation(string? destination) =>
        throw new NotSupportedException("The ground view builds no altitude picker; ground menus never read a field elevation");

    public void EnterDrawRoute(string callsign) => ground.StartDrawRoute(RequireMenuAircraft(callsign));

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        throw new NotSupportedException("The ground view has no warp popup; ground menus never build the warp item");

    public void OpenFlightPlanEditor() => throw new NotSupportedException("The ground view has no flight-plan item; ground menus never build it");

    public bool IsMinified(string callsign) =>
        throw new NotSupportedException("The ground view has no mini data block; ground menus never build the mini datablock item");

    public void ToggleMinified(string callsign) =>
        throw new NotSupportedException("The ground view has no mini data block; ground menus never build the mini datablock item");

    public bool HasManualDataBlockOffset(string callsign) => view.Canvas.HasManualDataBlockOffset(callsign);

    public void ResetDataBlockOffset(string callsign) => view.Canvas.ResetDataBlockOffset(callsign);

    public bool IsPathShown(string callsign) =>
        throw new NotSupportedException("The ground view draws no nav route; ground menus never build the nav route item");

    public void ToggleShowPath(string callsign) =>
        throw new NotSupportedException("The ground view draws no nav route; ground menus never build the nav route item");

    public MenuMeasureState GetMeasureState()
    {
        if (ground.Measure is not { } measure)
        {
            return MenuMeasureState.None;
        }

        return measure.Anchor is null ? MenuMeasureState.NoAnchor : MenuMeasureState.HasAnchor;
    }

    public void MeasurePickOnAircraft(string callsign)
    {
        if (ground.Measure is { } measure)
        {
            measure.Pick(RblEndpoint.OnAircraft(callsign), GroundViewModel.MeasureView, ground.MeasureTrackLookup, GroundViewModel.MeasureUnits);
        }
    }

    public TaxiRouteDisplayMode GetTaxiRouteMode(string callsign)
    {
        RequireMenuCallsign(callsign);
        return ground.GetTaxiRouteMode(callsign);
    }

    public void SetTaxiRouteMode(string callsign, TaxiRouteDisplayMode mode)
    {
        RequireMenuCallsign(callsign);
        ground.SetTaxiRouteMode(callsign, mode);
    }

    public bool IsDataBlockHidden(string callsign)
    {
        RequireMenuCallsign(callsign);
        return view.Canvas.IsDataBlockHidden(callsign);
    }

    public void ToggleHiddenDataBlock(string callsign)
    {
        RequireMenuCallsign(callsign);
        view.Canvas.ToggleHiddenDataBlock(callsign);
    }

    /// <summary>
    /// The other on-ground aircraft in the main view model's list, nearest the right-clicked aircraft first and at most
    /// <see cref="MaxGroundTraffic"/>; empty without a main view model.
    /// </summary>
    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign)
    {
        AircraftModel ac = RequireMenuAircraft(callsign);
        if (main is null)
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

    /// <summary>
    /// The ground view model's hold-short targets on the right-clicked aircraft's route, each sending <c>HS</c> with its
    /// route preview.
    /// </summary>
    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign)
    {
        AircraftModel ac = RequireMenuAircraft(callsign);
        return
        [
            .. ground
                .GetHoldShortTargets(ac)
                .Select(t => new MenuCommandChoice(t.DisplayName, $"HS {t.Target}", ground.FindHoldShortPreviewRoute(ac, t.Target))),
        ];
    }

    public void SetRoutePreview(TaxiRoute? route) => ground.PreviewRoute = route;

    /// <summary>The most aircraft the Follow… and Give way to… submenus list.</summary>
    private const int MaxGroundTraffic = 12;

    /// <summary>
    /// The right-clicked aircraft, which must be <paramref name="callsign"/>'s: route drawing and the movement submenus
    /// act on no other.
    /// </summary>
    private AircraftModel RequireMenuAircraft(string callsign)
    {
        if ((aircraft is null) || (!string.Equals(aircraft.Callsign, callsign, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The ground view draws routes and builds movement submenus only for the right-clicked aircraft, not '{callsign}'"
            );
        }

        return aircraft;
    }

    /// <summary>
    /// Throws when <paramref name="callsign"/> is not the right-clicked aircraft's. A menu opened on an aircraft the
    /// main view model has no model for (<c>aircraft</c> is null) still reads and drives its display by callsign.
    /// </summary>
    private void RequireMenuCallsign(string callsign)
    {
        if ((aircraft is not null) && (!string.Equals(aircraft.Callsign, callsign, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"The ground view's display items act only on the right-clicked aircraft, not '{callsign}'");
        }
    }

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}

/// <summary>
/// The aircraft list's <see cref="IMenuHost"/>, built per right-click: commands go straight to the server
/// connection, favorites are built for the right-clicked aircraft model, and the flight-plan editor opens on it.
/// The list has no input or warp popup, and builds no radar display group.
/// </summary>
internal sealed class ListMenuHost(MainViewModel main, AircraftModel aircraft) : IMenuHost
{
    /// <summary>The message every display member throws: the list has no radar display to read or drive.</summary>
    private const string NoDisplayGroup = "The aircraft list has no radar display; list menus never build the display group";

    /// <summary>The message every ground display member throws: the list draws no taxi routes or data blocks.</summary>
    private const string NoGroundDisplayItems =
        "The aircraft list has no ground display; list menus never build the taxi route or hide datablock items";

    /// <summary>The message every ground-movement member throws: the list has no ground map to list traffic or preview routes on.</summary>
    private const string NoGroundMovementItems =
        "The aircraft list has no ground map; list menus never build the hold short, follow or give way submenus";

    public Task SendAsync(string callsign, string command, string initials) => main.Connection.SendCommandAsync(callsign, command, initials);

    public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) =>
        throw new NotSupportedException("The aircraft list has no input popup; list menus never build input pickers");

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        throw new NotSupportedException("The aircraft list has no list popup; list menus never build list pickers");

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        throw new NotSupportedException("The aircraft list has no filtered-list popup; list menus never build fix pickers");

    // Throws rather than returning null: null would silently pick the free-text fix tier, hiding a list menu that started building fix pickers.
    public string[]? FixNames => throw new NotSupportedException("The aircraft list offers no fix pickers; list menus never build them");

    public double GetFieldElevation(string? destination) =>
        throw new NotSupportedException("The aircraft list builds no altitude picker; list menus never read a field elevation");

    public void EnterDrawRoute(string callsign) =>
        throw new NotSupportedException("The aircraft list has no route drawing; list menus never build the Draw route item");

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        throw new NotSupportedException("The aircraft list has no warp popup; list menus never build the warp item");

    public void OpenFlightPlanEditor() => FlightPlanEditorManager.Open(aircraft, main);

    public bool IsMinified(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public void ToggleMinified(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public bool HasManualDataBlockOffset(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public void ResetDataBlockOffset(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public bool IsPathShown(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public void ToggleShowPath(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public MenuMeasureState GetMeasureState() => throw new NotSupportedException(NoDisplayGroup);

    public void MeasurePickOnAircraft(string callsign) => throw new NotSupportedException(NoDisplayGroup);

    public TaxiRouteDisplayMode GetTaxiRouteMode(string callsign) => throw new NotSupportedException(NoGroundDisplayItems);

    public void SetTaxiRouteMode(string callsign, TaxiRouteDisplayMode mode) => throw new NotSupportedException(NoGroundDisplayItems);

    public bool IsDataBlockHidden(string callsign) => throw new NotSupportedException(NoGroundDisplayItems);

    public void ToggleHiddenDataBlock(string callsign) => throw new NotSupportedException(NoGroundDisplayItems);

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public void SetRoutePreview(TaxiRoute? route) => throw new NotSupportedException(NoGroundMovementItems);

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}
