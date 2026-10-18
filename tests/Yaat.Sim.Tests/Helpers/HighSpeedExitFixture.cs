using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Finds a real runway's high-speed exit where an aircraft stopped just off the pavement still has its tail over the runway's strict
/// paved half-width far from any bar (YAAT-825).
/// </summary>
public static class HighSpeedExitFixture
{
    /// <summary>
    /// The first of <paramref name="designator"/>'s bars, by node id, on an exit leaving the runway between 20° and 45° off its axis
    /// where a <paramref name="type"/> with its centre just off the pavement has its tail over the strict paved half-width and more than
    /// <see cref="HoldLineFootprint.MarkedBarReachFt"/> along the runway from the nearest bar on its side. Null when there is none
    /// or the data is unavailable.
    /// </summary>
    /// <param name="airport">The airport identifier (ICAO or short code).</param>
    /// <param name="designator">The runway designator.</param>
    /// <param name="type">The aircraft type.</param>
    /// <returns>The pose, or null.</returns>
    public static ExitPose? TailOverThePavementOnAHighSpeedExit(string airport, string designator, string type)
    {
        if (RunwayBarFixture.RunwayBar(airport, designator) is not { } fixture)
        {
            return null;
        }

        (AirportGroundLayout layout, RunwayInfo runway, GroundNode _) = fixture;
        double halfFt = runway.WidthFt / 2.0;
        var body = AircraftBody.Of(type);
        foreach (GroundNode bar in RunwayHoldBars.RunwayBars(layout, runway).OrderBy(b => b.Id))
        {
            if (RunwayEntryOf(runway, bar) is not { } entry)
            {
                continue;
            }

            var heading = new TrueHeading(GeoMath.BearingTo(entry.Position, bar.Position));
            double offAxisDeg = AxisDeviationDeg(heading.Degrees, runway);
            if ((offAxisDeg < 20.0) || (offAxisDeg > 45.0))
            {
                continue;
            }

            double alongFt = 0.0;
            LatLon centre = entry.Position;
            while (RunwayHoldBars.CentrelineFt(runway, centre) <= halfFt + RunwayOccupancy.LateralSlackFt + 1.0)
            {
                alongFt += 1.0;
                centre = GeoMath.ProjectPoint(entry.Position, heading, alongFt / GeoMath.FeetPerNm);
            }

            LatLon tail = body.PointOf(AircraftBodyPart.Tail, centre, heading.Degrees);
            double tailFt = RunwayHoldBars.CentrelineFt(runway, tail);
            GroundNode? nearest = RunwayHoldBars.NearestSameSideBar(layout, runway, centre);
            double tailAlongBarFt = nearest is null
                ? double.PositiveInfinity
                : Math.Abs(GeoMath.AlongTrackDistanceNm(tail, nearest.Position, runway.TrueHeading)) * GeoMath.FeetPerNm;
            if ((tailFt < halfFt) && (tailAlongBarFt > HoldLineFootprint.MarkedBarReachFt))
            {
                return new ExitPose(layout, runway, bar, type, centre, heading, RunwayHoldBars.CentrelineFt(runway, centre), tailFt, tailAlongBarFt);
            }
        }

        return null;
    }

    /// <summary>
    /// The first node on <paramref name="runway"/>'s pavement reached from <paramref name="bar"/> walking only toward the
    /// centreline: where the bar's exit leaves the runway. Null when no such walk reaches it.
    /// </summary>
    private static GroundNode? RunwayEntryOf(RunwayInfo runway, GroundNode bar)
    {
        var seen = new HashSet<int> { bar.Id };
        var frontier = new Queue<GroundNode>([bar]);
        while (frontier.TryDequeue(out GroundNode? node))
        {
            double nodeFt = RunwayHoldBars.CentrelineFt(runway, node.Position);
            if (nodeFt <= runway.WidthFt / 2.0)
            {
                return node;
            }

            foreach (GroundNode next in node.Edges.Select(edge => edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0]))
            {
                if ((RunwayHoldBars.CentrelineFt(runway, next.Position) < nodeFt) && seen.Add(next.Id))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static double AxisDeviationDeg(double headingDeg, RunwayInfo runway)
    {
        double diff = GeoMath.AbsBearingDifference(headingDeg, runway.TrueHeading.Degrees);
        return Math.Min(diff, 180.0 - diff);
    }
}
