using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What the user right-clicked to open an aircraft menu, and what was selected at that moment.
/// </summary>
/// <param name="Callsign">The aircraft the menu commands: the right-clicked one, or on a point click the selected one.</param>
/// <param name="PreviousSelection">
/// The aircraft selected before the right-click, the sender of the relative items; null when nothing else was selected.
/// </param>
/// <param name="Point">
/// The map position, taxi node or runway threshold right-clicked with an aircraft selected; null on an aircraft click.
/// </param>
/// <param name="Selection">
/// The rows the multi-selection items act on: on the single-select list, the right-clicked row; <c>[]</c> on the canvases.
/// </param>
public sealed record MenuClick(string Callsign, IMenuAircraft? PreviousSelection, MenuPoint? Point, IReadOnlyList<IMenuAircraft> Selection);

/// <summary>A right-clicked point: a map position on the radar, a taxi node on the ground.</summary>
/// <param name="Position">Where the click landed: the map position, or the taxi node's position.</param>
/// <param name="Node">The taxi node clicked, or the hold-short node a runway-threshold click resolves to; null on the radar.</param>
/// <param name="RunwayEnd">The runway end a threshold click names, which the taxi choices route to; null otherwise.</param>
public sealed record MenuPoint(LatLon Position, GroundNodeDto? Node, string? RunwayEnd);
