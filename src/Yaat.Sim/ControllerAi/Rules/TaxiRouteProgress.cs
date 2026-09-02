using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>An uncleared hold-short bar ahead on the taxi route and how far along the route it is.</summary>
public sealed record PendingHoldShort(HoldShortPoint Point, double DistanceFt);

/// <summary>
/// What the Ground brain reads off a taxi route: the next uncleared runway-crossing bar (and whether the aircraft is
/// already stopped at it), the distance left to the departure-runway bar, and which end of a crossed runway to name.
/// Distances walk the route from the aircraft's position through the remaining segments, so they are along-route, not
/// straight-line.
/// </summary>
public static class TaxiRouteProgress
{
    /// <summary>
    /// The first uncleared runway-crossing bar ahead, or null when there is none — or when an uncleared hold-short of
    /// another kind (an explicit taxiway hold, the departure-runway bar) comes first, since the aircraft stops there
    /// before any crossing beyond it matters. An aircraft holding short at a crossing reports that bar at distance 0.
    /// </summary>
    public static PendingHoldShort? NextUnclearedCrossing(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort: { Reason: HoldShortReason.RunwayCrossing, IsCleared: false } bar })
        {
            return new PendingHoldShort(bar, 0);
        }

        if (aircraft.Phases?.CurrentPhase is not TaxiingPhase)
        {
            return null;
        }

        return WalkAhead(aircraft, layout, point => point.Reason == HoldShortReason.RunwayCrossing);
    }

    /// <summary>Along-route distance to the departure-runway bar, or null when the route has none or the aircraft is not taxiing toward it.</summary>
    public static double? DistanceToDestinationBarFt(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if (aircraft.Phases?.CurrentPhase is not TaxiingPhase)
        {
            return null;
        }

        return WalkAhead(aircraft, layout, point => point.Reason == HoldShortReason.DestinationRunway)?.DistanceFt;
    }

    /// <summary>The end of a crossed runway the aircraft is nearest to — the one to name in <c>CROSS</c>.</summary>
    public static string NearestCrossingEnd(AircraftState aircraft, string combinedTarget, AirportGroundLayout? layout) =>
        RunwayCrossingEnd.Nearest(aircraft, combinedTarget, layout);

    /// <summary>
    /// Walks the remaining segments: the first leg is the straight distance from the aircraft to the current segment's
    /// far node, every later leg is the edge length. Stops at the first uncleared hold-short — returning it when it
    /// satisfies <paramref name="wanted"/>, null otherwise (the aircraft has to stop there first).
    /// </summary>
    private static PendingHoldShort? WalkAhead(AircraftState aircraft, AirportGroundLayout? layout, Func<HoldShortPoint, bool> wanted)
    {
        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null || layout is null)
        {
            return null;
        }

        double distanceNm = 0;
        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            var segment = route.Segments[i];
            distanceNm +=
                i == route.CurrentSegmentIndex ? GeoMath.DistanceNm(aircraft.Position, segment.Edge.ToNode.Position) : segment.Edge.DistanceNm;
            if (route.GetHoldShortAt(segment.ToNodeId) is { IsCleared: false } bar)
            {
                return wanted(bar) ? new PendingHoldShort(bar, distanceNm * GeoMath.FeetPerNm) : null;
            }
        }

        return null;
    }
}
