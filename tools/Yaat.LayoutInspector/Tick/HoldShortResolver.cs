using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// One named hold-short location referenced by tick-table output: a taxiway
/// letter (e.g. "K") and the list of ground-layout hold-short nodes that
/// terminate that taxiway on the referenced runway. Tick rows are then
/// compared against these nodes to produce <c>along_K</c> / <c>dist_K</c>
/// columns.
/// </summary>
public sealed record ExitRef(string Taxiway, List<GroundNode> HoldShortNodes);

/// <summary>
/// Bridges <see cref="CliOptions.TickHoldShorts"/> names (e.g. "K,D,Q") to
/// concrete <see cref="GroundNode"/> instances on the runway's hold-short
/// line. Used exclusively by <see cref="Commands.TickTableCommand"/>.
/// </summary>
public static class HoldShortResolver
{
    /// <summary>
    /// Finds all hold-short nodes on <paramref name="runwayId"/> whose adjacent
    /// edges match the requested <paramref name="taxiway"/> name. Mirrors how
    /// LandingPhase / RunwayExitPhase link a taxiway letter to a hold-short
    /// terminus — a hold-short node doesn't carry a taxiway name itself, only
    /// its edges do.
    /// </summary>
    public static List<GroundNode> Find(AirportGroundLayout layout, string runwayId, string taxiway)
    {
        var result = new List<GroundNode>();
        foreach (var node in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiway))
                {
                    result.Add(node);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Signed along-track distance and straight-line distance, both in feet,
    /// from the aircraft position to the nearest-ahead hold-short node in the
    /// given exit ref. "Ahead" wins over "behind" even when the behind node is
    /// closer by straight line — otherwise a just-passed hold-short distorts
    /// the display at exactly the moment where we need to understand what's
    /// still reachable.
    /// </summary>
    public static (double AlongFt, double DistFt) NearestDistances(double lat, double lon, ExitRef er, RunwayReference refLine)
    {
        if (er.HoldShortNodes.Count == 0)
        {
            return (0, 0);
        }

        double bestAheadAlongNm = double.MaxValue;
        double bestAheadStraightNm = 0;
        double bestBehindAlongNm = double.MinValue;
        double bestBehindStraightNm = 0;
        bool anyAhead = false;
        foreach (var n in er.HoldShortNodes)
        {
            double alongNm = GeoMath.AlongTrackDistanceNm(n.Latitude, n.Longitude, lat, lon, refLine.Heading);
            double straightNm = GeoMath.DistanceNm(lat, lon, n.Latitude, n.Longitude);
            if (alongNm >= 0 && alongNm < bestAheadAlongNm)
            {
                bestAheadAlongNm = alongNm;
                bestAheadStraightNm = straightNm;
                anyAhead = true;
            }
            else if (alongNm < 0 && alongNm > bestBehindAlongNm)
            {
                bestBehindAlongNm = alongNm;
                bestBehindStraightNm = straightNm;
            }
        }

        if (anyAhead)
        {
            return (bestAheadAlongNm * GeoMath.FeetPerNm, bestAheadStraightNm * GeoMath.FeetPerNm);
        }

        return (bestBehindAlongNm * GeoMath.FeetPerNm, bestBehindStraightNm * GeoMath.FeetPerNm);
    }
}
