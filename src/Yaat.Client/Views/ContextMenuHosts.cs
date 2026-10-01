using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// The radar's <see cref="IMenuHost"/>, built per right-click: commands go through the radar view model's send path,
/// input and warp pickers open the radar's popups, the display items read and drive the radar canvas, and favorites
/// are built for the right-clicked aircraft model.
/// </summary>
internal sealed class RadarMenuHost(RadarView view, RadarViewModel radar, MainViewModel? main, AircraftModel? aircraft) : IMenuHost
{
    public Task SendAsync(string callsign, string command, string initials) => radar.SendRawCommandAsync(callsign, initials, command);

    public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) =>
        Dispatcher.UIThread.Post(() => view.ShowInputPopup(placeholder, onSubmit));

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
