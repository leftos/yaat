using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

/// <summary>
/// The aircraft list's <see cref="IMenuHost"/>, built per right-click: commands go straight to the server
/// connection, favorites are built for the right-clicked aircraft model, the flight-plan editor opens on it, and its
/// popups and flyouts open through <see cref="MenuPopups"/> at the pointer on the menu's own flyout target
/// (<paramref name="flyoutAnchor"/>). The list builds no radar display group.
/// </summary>
internal sealed class ListMenuHost(MainViewModel main, AircraftModel aircraft, Control flyoutAnchor) : IMenuHost
{
    /// <summary>The message every ground-movement member throws: the list has no ground map to list traffic or preview routes on.</summary>
    private const string NoGroundMovementItems =
        "The aircraft list has no ground map; list menus never build the hold short, follow, give way, pushback or preset taxi items";

    public MenuSession Session => ClientMenuHost.SessionOf(main);

    public IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns) => ClientMenuHost.RpoItems(main, callsigns);

    public Task SendAsync(string callsign, string command, string initials) => main.Connection.SendCommandAsync(callsign, command, initials);

    /// <summary>
    /// The aircraft list serves the free-text popup, the flight-plan editor and the multi-selection assume. Its list and
    /// warp popups open, but <see cref="MenuHostCapabilities.ListPicker"/> and <see cref="MenuHostCapabilities.Warp"/> are
    /// left undeclared so those entries stay off the list's menu until the capabilities go;
    /// <see cref="MenuHostCapabilities.FilteredListPicker"/> stays undeclared because <see cref="FixNames"/> throws. It
    /// draws no route, so route drawing throws.
    /// </summary>
    public MenuHostCapabilities Capabilities =>
        MenuHostCapabilities.InputPopup | MenuHostCapabilities.FlightPlanEditor | MenuHostCapabilities.MultiSelectAssume;

    public void ShowInputPopup(string placeholder, BlankInput blank, Func<string, Task> onSubmit) =>
        MenuPopups.ShowInput(flyoutAnchor, placeholder, "", 0, blank, onSubmit);

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        MenuPopups.ShowList(flyoutAnchor, items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        MenuPopups.ShowFilteredList(flyoutAnchor, sortedNames, priorityItems, onPick);

    // Throws rather than returning null: null would silently pick the free-text fix tier, hiding a list menu that started building fix pickers.
    public string[]? FixNames => throw new NotSupportedException("The aircraft list offers no fix pickers; list menus never build them");

    public double GetFieldElevation(string? destination) =>
        throw new NotSupportedException("The aircraft list builds no altitude picker; list menus never read a field elevation");

    public void EnterDrawRoute(string callsign) =>
        throw new NotSupportedException("The aircraft list draws no route; only the ground view's Draw taxi route item reaches this member");

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        MenuPopups.ShowWarp(flyoutAnchor, new MenuPopups.WarpSeed(callsign, "", heading, altitude, speed), onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) =>
        MenuPopups.ShowCommand(flyoutAnchor, callsign, command => main.SendGatedCommandForViewAsync(aircraft, callsign, command, initials));

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        MenuPopups.ShowNote(flyoutAnchor, callsign, currentNote, sendCommand);

    /// <summary>Opens the editor on <paramref name="callsign"/>'s aircraft, looked up as the client host does; logs when it is gone.</summary>
    public void OpenFlightPlanEditor(string callsign) => ClientMenuHost.OpenFlightPlanEditor(main, aircraft, callsign);

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public void SetRoutePreview(TaxiRoute? route) => throw new NotSupportedException(NoGroundMovementItems);

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    public void EnterPushRoute(string callsign) => throw new NotSupportedException(NoGroundMovementItems);

    /// <summary>Assumes the selected shadows through the main view model, which owns the bulk-assume call.</summary>
    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) => main.AssumeSelectedLiveTrafficAsync([.. callsigns]);

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}
