using Avalonia.Controls;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a catalog entry's builder needs from the surface that owns the menu. The desktop client adapts its view models onto this.
/// </summary>
public interface IMenuHost
{
    /// <summary>For <paramref name="callsign"/>, sends <paramref name="command"/> on behalf of <paramref name="initials"/>.</summary>
    Task SendAsync(string callsign, string command, string initials);

    /// <summary>
    /// Opens the surface's free-text input showing <paramref name="placeholder"/>, and hands the submitted text to
    /// <paramref name="onSubmit"/>.
    /// </summary>
    void ShowInputPopup(string placeholder, Func<string, Task> onSubmit);

    /// <summary>True when the host can open a free-text input popup; the catalog hides input-tier items otherwise.</summary>
    bool HasInputPopup { get; }

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
    /// Opens the surface's warp popup for <paramref name="callsign"/>, seeded with <paramref name="heading"/>,
    /// <paramref name="altitude"/> and <paramref name="speed"/>, and hands the submitted position, heading, altitude
    /// and speed to <paramref name="onSubmit"/>.
    /// </summary>
    void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit);

    /// <summary>Opens the flight-plan editor for the aircraft the menu was opened on.</summary>
    void OpenFlightPlanEditor();

    /// <summary>Whether the surface shows <paramref name="callsign"/>'s data block in its mini (compressed) form.</summary>
    bool IsMinified(string callsign);

    /// <summary>Switches <paramref name="callsign"/>'s data block between its mini and full forms.</summary>
    void ToggleMinified(string callsign);

    /// <summary>Whether <paramref name="callsign"/>'s data block has been dragged off the student position.</summary>
    bool HasManualDataBlockOffset(string callsign);

    /// <summary>Puts <paramref name="callsign"/>'s data block back on its student position.</summary>
    void ResetDataBlockOffset(string callsign);

    /// <summary>Whether <paramref name="callsign"/>'s nav route is drawn on the surface.</summary>
    bool IsPathShown(string callsign);

    /// <summary>Shows or hides <paramref name="callsign"/>'s nav route.</summary>
    void ToggleShowPath(string callsign);

    /// <summary>What the surface's measure tool is doing, which decides whether the Display menu offers a measure item.</summary>
    MenuMeasureState GetMeasureState();

    /// <summary>Latches the pending measurement's next endpoint to <paramref name="callsign"/>, so the line follows it.</summary>
    void MeasurePickOnAircraft(string callsign);

    /// <summary>How the surface draws <paramref name="callsign"/>'s taxi route: following the global setting, or always shown or hidden.</summary>
    TaxiRouteDisplayMode GetTaxiRouteMode(string callsign);

    /// <summary>Sets how the surface draws <paramref name="callsign"/>'s taxi route to <paramref name="mode"/>.</summary>
    void SetTaxiRouteMode(string callsign, TaxiRouteDisplayMode mode);

    /// <summary>Whether the surface has hidden <paramref name="callsign"/>'s data block.</summary>
    bool IsDataBlockHidden(string callsign);

    /// <summary>Hides <paramref name="callsign"/>'s data block, or shows it again when it is hidden.</summary>
    void ToggleHiddenDataBlock(string callsign);

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
    /// Assumes control of the live-traffic shadows <paramref name="callsigns"/> names, in the order given. Only the
    /// aircraft list selects several aircraft at once, so its host is the only surface that answers.
    /// </summary>
    Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns);
}
