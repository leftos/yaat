using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// The radar's <see cref="IMenuHost"/>, built per right-click: commands go through the radar view model's send path,
/// input and warp pickers open the radar's popups, and favorites are built for the right-clicked aircraft model.
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

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}

/// <summary>
/// The aircraft list's <see cref="IMenuHost"/>, built per right-click: commands go straight to the server
/// connection, favorites are built for the right-clicked aircraft model, and the flight-plan editor opens on it.
/// The list has no input or warp popup.
/// </summary>
internal sealed class ListMenuHost(MainViewModel main, AircraftModel aircraft) : IMenuHost
{
    public Task SendAsync(string callsign, string command, string initials) => main.Connection.SendCommandAsync(callsign, command, initials);

    public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) =>
        throw new NotSupportedException("The aircraft list has no input popup; list menus never build input pickers");

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        throw new NotSupportedException("The aircraft list has no warp popup; list menus never build the warp item");

    public void OpenFlightPlanEditor() => FlightPlanEditorManager.Open(aircraft, main);

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}
