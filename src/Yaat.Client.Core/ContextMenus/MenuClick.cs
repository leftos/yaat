namespace Yaat.Client.ContextMenus;

/// <summary>
/// What the user right-clicked to open an aircraft menu, and what was selected at that moment.
/// </summary>
/// <param name="Callsign">The aircraft the menu commands: the right-clicked one.</param>
/// <param name="PreviousSelection">
/// The aircraft selected before the right-click, the sender of the relative items; null when nothing else was selected.
/// </param>
/// <param name="Selection">
/// The rows the multi-selection items act on: on the single-select list, the right-clicked row; <c>[]</c> on the canvases.
/// </param>
public sealed record MenuClick(string Callsign, IMenuAircraft? PreviousSelection, IReadOnlyList<IMenuAircraft> Selection);
