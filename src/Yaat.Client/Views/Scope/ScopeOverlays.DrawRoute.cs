using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

public sealed partial record ScopeOverlays
{
    /// <summary>The route being drawn, or null while no route is.</summary>
    public ScopeDrawRouteOverlay? DrawRoute { get; init; }
}

/// <summary>
/// One frame of the route being drawn: the waypoints placed so far, the condition at each, the aircraft the route starts from and
/// the pointer the rubber band runs to, named by FRD.
/// </summary>
/// <param name="Waypoints">The waypoints placed so far; empty before the first.</param>
/// <param name="Conditions">The condition at each waypoint by index, or null for none.</param>
/// <param name="Origin">The drawn aircraft's position, or null when it is not in the traffic.</param>
/// <param name="Cursor">The pointer's position, or null while none is known.</param>
/// <param name="CursorLabel">The pointer's position named by FRD, or null when no fix is in range.</param>
public sealed record ScopeDrawRouteOverlay(
    IReadOnlyList<DrawnWaypoint> Waypoints,
    IReadOnlyDictionary<int, WaypointCondition>? Conditions,
    LatLon? Origin,
    LatLon? Cursor,
    string? CursorLabel
);
