using Avalonia.Controls;

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

    /// <summary>
    /// Builds the Favorite Commands submenu. <paramref name="aircraft"/> is null when the menu has no aircraft model;
    /// the submenu itself is never null.
    /// </summary>
    MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context);
}
