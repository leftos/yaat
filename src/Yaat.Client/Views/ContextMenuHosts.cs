using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

/// <summary>
/// The radar's <see cref="IMenuHost"/>, built per right-click: commands go through the radar view model's send path,
/// input, list, filtered-list and warp pickers and the Command… and Note… flyouts open through <see cref="MenuPopups"/>
/// at the pointer on the radar canvas, fix names and field elevations come from the radar view model, and favorites are
/// built for the right-clicked aircraft model. Route drawing is the radar's own canvas item, never the host's.
/// </summary>
internal sealed class RadarMenuHost(RadarView view, RadarViewModel radar, MainViewModel? main, AircraftModel? aircraft) : IMenuHost
{
    /// <summary>The message the ground-traffic member throws: only the ground view offers the follow and give-way submenus.</summary>
    private const string NoGroundTrafficItems = "The radar menu has no ground follow or give way items; only the ground view lists ground traffic";

    /// <summary>The message both hold-short members throw: only the ground view offers hold short and previews its route.</summary>
    private const string NoHoldShortItem = "The radar menu has no hold short item; only the ground view offers hold short and previews taxi routes";

    /// <summary>The message every pushback member throws: only the ground view offers the pushback faces, push back to and push route.</summary>
    private const string NoPushbackItems = "The radar menu has no pushback items; only the ground view offers pushback faces, stands and push routes";

    /// <summary>The message the preset-taxi member throws: only the ground view offers preset taxi routes.</summary>
    private const string NoPresetTaxiItem = "The radar menu has no preset taxi route item; only the ground view offers preset taxi routes";

    /// <summary>The message the assume-selected member throws: only the aircraft list selects several aircraft at once.</summary>
    private const string NoAssumeSelectedItem =
        "The radar menu has no assume-selected item; only the aircraft list assumes a multi-selection of live traffic";

    public Task SendAsync(string callsign, string command, string initials) => radar.SendRawCommandAsync(callsign, initials, command);

    /// <summary>
    /// The radar serves the free-text popup, both pickers and the warp popup. It has no flight-plan editor, no route
    /// drawing (its Draw route is a canvas item the view builds), no ground map and no multi-selection, so those
    /// families' members throw.
    /// </summary>
    public MenuHostCapabilities Capabilities =>
        MenuHostCapabilities.InputPopup | MenuHostCapabilities.ListPicker | MenuHostCapabilities.FilteredListPicker | MenuHostCapabilities.Warp;

    public void ShowInputPopup(string placeholder, BlankInput blank, Func<string, Task> onSubmit) =>
        MenuPopups.ShowInput(view.Canvas, placeholder, "", 0, blank, onSubmit);

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        MenuPopups.ShowList(view.Canvas, items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        MenuPopups.ShowFilteredList(view.Canvas, sortedNames, priorityItems, onPick);

    public string[]? FixNames => radar.FixNames;

    public double GetFieldElevation(string? destination) => radar.GetFieldElevation(destination);

    public void EnterDrawRoute(string callsign) =>
        throw new NotSupportedException("The radar menu has no catalog route item; its Draw route is a canvas item the view builds");

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        MenuPopups.ShowWarp(view.Canvas, new MenuPopups.WarpSeed(callsign, "", heading, altitude, speed), onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) =>
        MenuPopups.ShowCommand(
            view.Canvas,
            callsign,
            command =>
                main is not null
                    ? main.SendGatedCommandForViewAsync(aircraft, callsign, command, initials)
                    : radar.SendRawCommandAsync(callsign, initials, command)
        );

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        MenuPopups.ShowNote(view.Canvas, callsign, currentNote, sendCommand);

    public void OpenFlightPlanEditor() =>
        throw new NotSupportedException("The radar menu has no flight-plan item; Ctrl-clicking an aircraft opens the editor");

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => throw new NotSupportedException(NoGroundTrafficItems);

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => throw new NotSupportedException(NoHoldShortItem);

    public void SetRoutePreview(TaxiRoute? route) => throw new NotSupportedException(NoHoldShortItem);

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => throw new NotSupportedException(NoPushbackItems);

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => throw new NotSupportedException(NoPushbackItems);

    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) => throw new NotSupportedException(NoPresetTaxiItem);

    public void EnterPushRoute(string callsign) => throw new NotSupportedException(NoPushbackItems);

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) => throw new NotSupportedException(NoAssumeSelectedItem);

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}

/// <summary>
/// The ground view's <see cref="IMenuHost"/>, built per right-click: commands go through the ground view model's send
/// path, route drawing starts a taxi route for the right-clicked aircraft, the follow and give-way submenus list the
/// main view model's other ground traffic, hold short asks the ground view model for the route's targets and previews
/// the route to one on hover, the pushback faces, push back to and preset taxi routes ask the ground view model for
/// their finished commands, push route starts a tug move for the right-clicked aircraft, and favorites are built for
/// the right-clicked aircraft model. Its popups and flyouts open through <see cref="MenuPopups"/> at the pointer on
/// the ground canvas. It has no fix or altitude picker and no flight-plan item.
/// </summary>
internal sealed class GroundMenuHost(GroundView view, GroundViewModel ground, MainViewModel? main, AircraftModel? aircraft) : IMenuHost
{
    public Task SendAsync(string callsign, string command, string initials) => ground.SendRawCommandAsync(callsign, initials, command);

    /// <summary>
    /// The ground view serves the free-text popup, route drawing and the ground-movement submenus. Its list and warp
    /// popups open, but <see cref="MenuHostCapabilities.ListPicker"/> and <see cref="MenuHostCapabilities.Warp"/> are
    /// left undeclared so those entries stay off the ground menu until the capabilities go;
    /// <see cref="MenuHostCapabilities.FilteredListPicker"/> stays undeclared because <see cref="FixNames"/> throws. It
    /// has no flight-plan editor and no multi-selection, so those throw.
    /// </summary>
    public MenuHostCapabilities Capabilities =>
        MenuHostCapabilities.InputPopup | MenuHostCapabilities.DrawRoute | MenuHostCapabilities.GroundMovement;

    public void ShowInputPopup(string placeholder, BlankInput blank, Func<string, Task> onSubmit) =>
        MenuPopups.ShowInput(view.Canvas, placeholder, "", 0, blank, onSubmit);

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        MenuPopups.ShowList(view.Canvas, items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        MenuPopups.ShowFilteredList(view.Canvas, sortedNames, priorityItems, onPick);

    // Throws rather than returning null: null would silently pick the free-text fix tier, hiding a ground menu that started building fix pickers.
    public string[]? FixNames => throw new NotSupportedException("The ground view offers no fix pickers; ground menus never build them");

    public double GetFieldElevation(string? destination) =>
        throw new NotSupportedException("The ground view builds no altitude picker; ground menus never read a field elevation");

    public void EnterDrawRoute(string callsign) => ground.StartDrawRoute(RequireMenuAircraft(callsign));

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        MenuPopups.ShowWarp(view.Canvas, new MenuPopups.WarpSeed(callsign, "", heading, altitude, speed), onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) =>
        MenuPopups.ShowCommand(
            view.Canvas,
            callsign,
            command =>
                main is not null
                    ? main.SendGatedCommandForViewAsync(aircraft, callsign, command, initials)
                    : ground.SendRawCommandAsync(callsign, initials, command)
        );

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        MenuPopups.ShowNote(view.Canvas, callsign, currentNote, sendCommand);

    public void OpenFlightPlanEditor() => throw new NotSupportedException("The ground view has no flight-plan item; ground menus never build it");

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

    /// <summary>The ground view model's pushback facings at the right-clicked aircraft's node, each sending <c>PUSH FACE</c>.</summary>
    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => ground.GetPushbackFaceChoices(RequireMenuAircraft(callsign));

    /// <summary>The ground view model's nearest named stands for the right-clicked aircraft, each sending <c>PUSH</c>.</summary>
    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => ground.GetPushbackToChoices(RequireMenuAircraft(callsign));

    /// <summary>The ground view model's preset taxi routes walkable from the right-clicked aircraft's node, each sending <c>TAXI</c>.</summary>
    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) => ground.GetPresetTaxiChoices(RequireMenuAircraft(callsign));

    public void EnterPushRoute(string callsign) => ground.StartPushRoute(RequireMenuAircraft(callsign));

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) =>
        throw new NotSupportedException(
            "The ground view has no assume-selected item; only the aircraft list assumes a multi-selection of live traffic"
        );

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

    public MenuItem BuildFavorites(IMenuAircraft? menuAircraft, MenuContext context) =>
        FavoritesContextMenu.Build(main, aircraft, context.Callsign, context.Initials);
}

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

    public void OpenFlightPlanEditor() => FlightPlanEditorManager.Open(aircraft, main);

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
