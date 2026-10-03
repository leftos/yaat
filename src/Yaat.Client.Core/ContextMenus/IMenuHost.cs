using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a catalog entry's builder needs from the surface that owns the menu: the send path, the popups and flyouts, the
/// ground choices and route drawing. A surface's own canvas items — every view's view section — are not here: the view builds them with <see cref="CanvasMenuItems"/> from its own canvas state.
/// </summary>
public interface IMenuHost
{
    /// <summary>The controller's session settings — initials, solo-training flag, "VFR commands for IFR aircraft" — the menu is built with.</summary>
    MenuSession Session { get; }

    /// <summary>For <paramref name="callsign"/>, sends <paramref name="command"/> on behalf of <paramref name="initials"/>.</summary>
    Task SendAsync(string callsign, string command, string initials);

    /// <summary>
    /// Opens the surface's free-text input showing <paramref name="placeholder"/>, holding <paramref name="initialText"/>
    /// with the caret at <paramref name="caretIndex"/>, and hands the submitted text to <paramref name="onSubmit"/>. A
    /// blank submit follows <paramref name="blank"/>: <see cref="BlankInput.Closes"/> closes the popup without calling
    /// it, <see cref="BlankInput.Submits"/> hands it <c>""</c>.
    /// </summary>
    void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task> onSubmit);

    /// <summary>
    /// The point at <paramref name="position"/> as a fix-radial-distance the point menu's Direct to, Hold and Warp here
    /// items name; null while the surface's fixes are not loaded or none is near enough. Every host serves it, so it
    /// belongs to no <see cref="MenuHostCapabilities"/> family.
    /// </summary>
    string? DescribePoint(LatLon position);

    /// <summary>
    /// The Taxi here choices for <paramref name="callsign"/> to <paramref name="node"/>, routed to
    /// <paramref name="runwayEnd"/> when a threshold click names one: one disabled "No route found" row when no route
    /// reaches it, one route's choice when one does, else one "Taxi here" submenu over every route's choice; each route
    /// previews on hover. Empty when the aircraft has no node to start from.
    /// </summary>
    IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd);

    /// <summary>The text and caret the point menu's Custom taxi… input opens with at <paramref name="node"/>.</summary>
    MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node);

    /// <summary>
    /// The families of members this surface serves. The catalog hides an entry whose
    /// <see cref="MenuCatalogEntry.Requires"/> names a flag the surface does not declare.
    /// </summary>
    MenuHostCapabilities Capabilities { get; }

    /// <summary>
    /// Opens the surface's list popup over <paramref name="items"/>, with <paramref name="selected"/> (or the item
    /// closest to it) highlighted when it is not null, and hands the picked item to <paramref name="onPick"/>.
    /// </summary>
    void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick);

    /// <summary>
    /// Opens the surface's type-to-filter popup over <paramref name="sortedNames"/>, listing
    /// <paramref name="priorityItems"/> until the controller types, and hands the picked name to <paramref name="onPick"/>.
    /// </summary>
    void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick);

    /// <summary>Every fix name the surface's filtered fix pickers offer, sorted; null while the navigation data is not loaded.</summary>
    string[]? FixNames { get; }

    /// <summary>The field elevation in feet of <paramref name="destination"/>, or of the surface's primary airport when it has none.</summary>
    double GetFieldElevation(string? destination);

    /// <summary>Puts the surface into drawing a route for <paramref name="callsign"/>.</summary>
    void EnterDrawRoute(string callsign);

    /// <summary>
    /// Opens the surface's warp popup for <paramref name="callsign"/>, seeded with the position <paramref name="frd"/>
    /// (<c>""</c> for none), <paramref name="heading"/>, <paramref name="altitude"/> and <paramref name="speed"/>, and
    /// hands the submitted position, heading, altitude and speed to <paramref name="onSubmit"/>.
    /// </summary>
    void ShowWarpPopup(string callsign, string frd, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit);

    /// <summary>
    /// Opens the surface's free-text command popup for <paramref name="callsign"/>, and sends what the controller types
    /// on behalf of <paramref name="initials"/> through the VFR gate, as typed input goes.
    /// </summary>
    void ShowCommandFlyout(string callsign, string initials);

    /// <summary>
    /// Opens the surface's note popup for <paramref name="callsign"/>, prefilled with <paramref name="currentNote"/>, and
    /// hands the finished <c>NOTE</c> command it builds to <paramref name="sendCommand"/>.
    /// </summary>
    void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand);

    /// <summary>Opens the flight-plan editor for <paramref name="callsign"/>.</summary>
    void OpenFlightPlanEditor(string callsign);

    /// <summary>
    /// The callsigns of a capped number of the other aircraft on the ground (the host sets the cap), nearest to
    /// <paramref name="callsign"/> first, which the Follow… and Give way to… submenus list; empty when there are none.
    /// </summary>
    IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign);

    /// <summary>
    /// The Hold short of… choices for <paramref name="callsign"/>'s taxi route: each target's text, its finished
    /// <c>HS</c> command, and the route to it the surface previews on hover; empty when the route offers none.
    /// </summary>
    IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign);

    /// <summary>Previews <paramref name="route"/> on the surface; null clears the preview.</summary>
    void SetRoutePreview(TaxiRoute? route);

    /// <summary>
    /// The pushback facings at <paramref name="callsign"/>'s stand, one per taxiway leaving it: each item's text and
    /// its finished <c>PUSH FACE</c> command, no preview; empty when the stand offers none.
    /// </summary>
    IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign);

    /// <summary>
    /// The named stands <paramref name="callsign"/> can be pushed back to, nearest first and capped by the host: each
    /// node's name and its finished <c>PUSH</c> command, no preview; empty when there are none.
    /// </summary>
    IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign);

    /// <summary>
    /// The airport's preset taxi routes that can be walked from <paramref name="callsign"/>'s node: each route's name
    /// and its finished <c>TAXI</c> command, no preview; empty when none applies.
    /// </summary>
    IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign);

    /// <summary>Puts the surface into drawing a tug move for <paramref name="callsign"/>.</summary>
    void EnterPushRoute(string callsign);

    /// <summary>
    /// Builds the Favorite Commands submenu. <paramref name="aircraft"/> is null when the menu has no aircraft model;
    /// the submenu itself is never null.
    /// </summary>
    MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context);

    /// <summary>
    /// The room's control items for <paramref name="callsigns"/> (take, give up, give and unassign control), opening with
    /// a separator; empty when the room has no one to hand control to.
    /// </summary>
    IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns);

    /// <summary>
    /// Assumes control of the live-traffic shadows <paramref name="callsigns"/> names, in the order given. One host serves
    /// every view; only the list's click carries a multi-row selection, so only its call is ever non-empty.
    /// </summary>
    Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns);
}
