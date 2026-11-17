using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// Builds the polyline and the optional heading tail that the radar's "Show nav route" overlay and the Scope's pilot lens
/// draw for an aircraft. Pure helpers, no UI dependency beyond <see cref="DrawnWaypoint"/> and <see cref="VectorTail"/>.
/// Everything else the overlay draws (route-end courses, final approach courses, coded legs, holds, queued vectors) arrives
/// from the server as <see cref="AircraftModel.NavRouteShapes"/>; nothing here reads CIFP.
/// </summary>
internal static class ShownRouteBuilder
{
    /// <summary>Length of the heading tail drawn from a vectored aircraft.</summary>
    public const double TailLengthNm = 5.0;

    /// <summary>
    /// The current <see cref="AircraftModel.NavRouteFixes"/> as the server sent them (positions and crossing-restriction
    /// labels), and a heading tail from the aircraft along its assigned heading when it is being vectored with no route
    /// fixes; the server sends no shape for an assigned heading.
    /// </summary>
    public static (List<DrawnWaypoint> Waypoints, VectorTail? Tail) BuildPrimary(AircraftModel ac)
    {
        List<DrawnWaypoint> waypoints = ResolveNavigationRouteWaypoints(ac);
        VectorTail? tail =
            (waypoints.Count == 0) && (ac.AssignedHeading is { } hdg)
                ? new VectorTail(ac.Position.Lat, ac.Position.Lon, hdg.Degrees, TailLengthNm)
                : null;
        return (waypoints, tail);
    }

    private static List<DrawnWaypoint> ResolveNavigationRouteWaypoints(AircraftModel ac)
    {
        // Draw the server-provided positions verbatim rather than re-resolving fix names locally.
        // The server route already carries arc-densification vertices, custom fixes, and FRD fixes
        // with exact coordinates — re-resolving by name would drop the synthetic ones (breaking arcs)
        // and could disagree with the flown position. Synthetic arc vertices arrive with an empty
        // name and render as bare polyline points.
        var result = new List<DrawnWaypoint>(ac.NavRouteFixes.Count);
        foreach (NavRouteFixDto fix in ac.NavRouteFixes)
        {
            result.Add(new DrawnWaypoint(fix.Name, fix.Lat, fix.Lon, fix.RestrictionTokens));
        }
        return result;
    }
}
