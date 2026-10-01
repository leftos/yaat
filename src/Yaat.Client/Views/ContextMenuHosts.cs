using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// The radar's <see cref="IMenuHost"/>, built per right-click: commands go through the radar view model's send path,
/// input, list, filtered-list and warp pickers open the radar's popups, fix names, field elevations and route drawing
/// come from the radar view model, the display items read and drive the radar canvas, and favorites are built for the
/// right-clicked aircraft model.
/// </summary>
internal sealed class RadarMenuHost(RadarView view, RadarViewModel radar, MainViewModel? main, AircraftModel? aircraft) : IMenuHost
{
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

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}

/// <summary>
/// The ground view's <see cref="IMenuHost"/>, built per right-click: commands go through the ground view model's send
/// path, the data-block reset reads and drives the ground canvas, the measure item latches the ground view model's
/// measurement, route drawing starts a taxi route for the right-clicked aircraft, and favorites are built for the
/// right-clicked aircraft model. The ground has no input, list, filtered-list or warp popup, no fix or altitude
/// picker, no flight-plan item, and no mini data block or nav route.
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

    public void EnterDrawRoute(string callsign)
    {
        if (aircraft is null || !string.Equals(aircraft.Callsign, callsign, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The ground view draws a taxi route only for the right-clicked aircraft, not '{callsign}'");
        }

        ground.StartDrawRoute(aircraft);
    }

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

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}
