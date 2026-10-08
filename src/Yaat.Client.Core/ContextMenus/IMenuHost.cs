using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a catalog entry's builder needs from the surface that owns the menu: the send path, the popups and flyouts, the
/// ground choices and route drawing. A surface's own canvas items — every view's view section — are not here: the view
/// builds them with <see cref="CanvasMenuItems"/> from its own canvas state.
/// </summary>
public interface IMenuHost
{
    /// <summary>The controller's session settings — initials, solo-training flag, "VFR commands for IFR aircraft" — the menu is built with.</summary>
    MenuSession Session { get; }

    /// <summary>For <paramref name="callsign"/>, sends <paramref name="command"/> on behalf of <paramref name="initials"/>.</summary>
    Task SendAsync(string callsign, string command, string initials);

    /// <summary>
    /// For <paramref name="callsign"/>, sends <paramref name="command"/>, text the controller authored, on behalf of
    /// <paramref name="initials"/> through the "VFR commands for IFR aircraft" gate, as typed input and favorites go.
    /// </summary>
    Task SendGatedAsync(string callsign, string command, string initials);

    /// <summary>
    /// Opens the surface's free-text input showing <paramref name="placeholder"/>, holding <paramref name="initialText"/>
    /// with the caret at <paramref name="caretIndex"/>, and hands the submitted text to <paramref name="onSubmit"/>. A
    /// blank submit follows <paramref name="blank"/>: <see cref="BlankInput.Closes"/> closes the popup without calling
    /// it, <see cref="BlankInput.Submits"/> hands it <c>""</c>.
    /// </summary>
    void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task> onSubmit);

    /// <summary>
    /// The point at <paramref name="position"/> as a fix-radial-distance the point menu's Direct to, Hold and Warp here
    /// items name; null while the surface's fixes are not loaded or none is near enough.
    /// </summary>
    string? DescribePoint(LatLon position);

    /// <summary>
    /// The Taxi here choices for <paramref name="callsign"/> to <paramref name="node"/>, routed to
    /// <paramref name="runwayEnd"/> when a threshold click names one: one disabled "No route found" row when no route
    /// reaches it, one route's choice when one does, else one "Taxi here" submenu over every route's choice; each route
    /// previews on hover. Empty when the aircraft has no node to start from.
    /// </summary>
    IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd);

    /// <summary>
    /// The hold-short nodes Taxi to runway offers <paramref name="callsign"/> for <paramref name="runwayEnd"/> of
    /// <paramref name="runwayName"/> after a click on its surface at <paramref name="click"/>: the route-nearest hold
    /// short, the one nearest the click, and the route-nearest of those at that end's threshold (full length), each node
    /// once with every reason it was picked, in that order. Empty when the aircraft or the runway is not found.
    /// </summary>
    IReadOnlyList<RunwayHoldShortTarget> GetRunwayHoldShortTargets(string callsign, string runwayName, string runwayEnd, LatLon click);

    /// <summary>
    /// The text and caret the point menu's Custom taxi… input opens with at <paramref name="node"/>, naming
    /// <paramref name="runwayEnd"/> when a threshold click names one.
    /// </summary>
    MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node, string? runwayEnd);

    /// <summary>
    /// The taxiways meeting at <paramref name="node"/>, each once, leaving out runway centerlines and the ramp, which the
    /// point menu's title names a taxi node by; empty when the node is not in the surface's ground layout.
    /// </summary>
    IReadOnlyList<string> GetNodeTaxiwayNames(GroundNodeDto node);

    /// <summary>
    /// The taxiway the hold-short <paramref name="node"/> sits on, which the point menu's title names a hold short by;
    /// null when the node is not in the surface's ground layout or no named taxiway leads to it.
    /// </summary>
    string? GetHoldShortTaxiwayName(GroundNodeDto node);

    /// <summary>
    /// Whether the tug planner finds a move for <paramref name="callsign"/> from where it stands to
    /// <paramref name="node"/>, which the point menu's Push to needs; false when the aircraft or the node is not found.
    /// </summary>
    bool CanTugReach(string callsign, GroundNodeDto node);

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

    /// <summary>
    /// Opens the surface's titled picker of <paramref name="list"/>'s rows, with its selected row centred, and hands the
    /// row the controller picks to <paramref name="onPick"/>; a row that cannot be picked (<see cref="MenuRichRow.IsPickable"/>)
    /// is never handed over.
    /// </summary>
    void ShowRichListPopup(MenuRichList list, Action<MenuRichRow> onPick);

    /// <summary>
    /// The minimum vectoring altitude sector covering <paramref name="position"/>: its name and floor in feet MSL; null
    /// when none covers it.
    /// </summary>
    (string Sector, int FloorFtMsl)? GetMva(LatLon position);

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
    /// Every other aircraft on the ground in the sim, nearest to <paramref name="callsign"/> first, each as seen from it
    /// (<see cref="RelativeGeometry.GroundTrafficRow"/>), which the Follow… and Give way to… submenus list; never a delayed
    /// spawn. Empty when there are none or <paramref name="callsign"/> is not found.
    /// </summary>
    IReadOnlyList<MenuGroundTrafficRow> GetGroundTrafficRows(string callsign);

    /// <summary>
    /// Whether <paramref name="otherCallsign"/> stands on the taxi route <paramref name="callsign"/> still has ahead of it,
    /// which the ground For line names (<c>on SWA602's route</c>); false when either is not found or there is no route.
    /// </summary>
    bool IsOnTaxiRoute(string callsign, string otherCallsign);

    /// <summary>
    /// Highlights <paramref name="callsign"/> on the ground view while a traffic row under the pointer names it, replacing
    /// the aircraft highlighted before; null clears it. A highlight the controller set by hand stays.
    /// </summary>
    void HighlightAircraft(string? callsign);

    /// <summary>
    /// The other airborne aircraft nearest <paramref name="callsign"/>, nearest first and capped by the host, each as seen
    /// from it (<see cref="RelativeGeometry.TrafficRow"/>), which the Report traffic in sight… list offers; never a delayed
    /// spawn or a live-traffic shadow. Empty when there are none or <paramref name="callsign"/> is not found.
    /// </summary>
    IReadOnlyList<MenuTrafficRow> GetNearbyTraffic(string callsign);

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
