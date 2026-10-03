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

/// <summary>A right-clicked point: a map position on the radar, a taxi node or a runway surface on the ground.</summary>
/// <param name="Position">Where the click landed: the map position, the taxi node's position, or the clicked spot on a runway surface.</param>
/// <param name="Node">
/// The taxi node clicked, or the hold-short node a runway-threshold click resolves to; null on the radar and on a runway surface.
/// </param>
/// <param name="RunwayEnd">The runway end a threshold click names, which the taxi choices route to; null otherwise.</param>
/// <param name="SurfaceRunways">
/// The runways (e.g. <c>"28R/10L"</c>) whose surface a ground click landed on, nearest centerline first, which Taxi to
/// runway offers; <c>[]</c> on every other point.
/// </param>
/// <param name="WarpNode">
/// The ground node nearest a runway-surface click, which Warp here warps to when <paramref name="Node"/> is null; null on
/// every other point.
/// </param>
public sealed record MenuPoint(
    LatLon Position,
    GroundNodeDto? Node,
    string? RunwayEnd,
    IReadOnlyList<string> SurfaceRunways,
    GroundNodeDto? WarpNode
);
