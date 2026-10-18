using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A runway's hold-short bars and the band they mark: the bar nodes of each runway, which side of its centreline a point lies
/// on, how far from the centreline it lies, and whether it is nearer the centreline than the bars on its side. Shared by the
/// follower's hold-line oracle and the turn-about footprint check.
/// </summary>
public static class RunwayHoldBars
{
    /// <summary>
    /// Each layout's runway hold-short bar nodes by runway (<see cref="RunwayBarIndex"/>), built on first use and held with the
    /// layout — a new layout instance starts a new index — so a tick's hold-line checks never scan every node of the layout.
    /// </summary>
    private static readonly ConditionalWeakTable<AirportGroundLayout, RunwayBarIndex> BarIndexes = [];

    /// <summary>
    /// A layout's runway hold-short bar nodes with a runway, and those of each runway asked for so far with the side of its
    /// centreline each lies on, by the runway's airport and identifier.
    /// </summary>
    private sealed class RunwayBarIndex(GroundNode[] bars)
    {
        private readonly ConcurrentDictionary<(string AirportId, RunwayIdentifier Id), RunwayBarSides> _byRunway = new();

        public static RunwayBarIndex For(AirportGroundLayout layout) =>
            new([.. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))]);

        public RunwayBarSides Of(RunwayInfo runway) =>
            _byRunway.TryGetValue((runway.AirportId, runway.Id), out RunwayBarSides? known)
                ? known
                : _byRunway.GetOrAdd((runway.AirportId, runway.Id), Filter(runway));

        private RunwayBarSides Filter(RunwayInfo runway)
        {
            GroundNode[] own = [.. bars.Where(n => (n.RunwayId is { } barRunway) && barRunway.Overlaps(runway.Id))];
            return new RunwayBarSides(own, [.. own.Select(bar => IsRightOfCentreline(runway, bar.Position))]);
        }
    }

    /// <summary>A runway's bar nodes and, for each, whether it lies right of the centreline (end 1 to end 2).</summary>
    /// <param name="Bars">The bar nodes.</param>
    /// <param name="IsRight">For each bar, in order, true right of the centreline.</param>
    private sealed record RunwayBarSides(GroundNode[] Bars, bool[] IsRight);

    /// <summary>The runway hold-short bar nodes of <paramref name="runway"/>.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <returns>Its bar nodes, either side of the centreline.</returns>
    public static GroundNode[] RunwayBars(AirportGroundLayout layout, RunwayInfo runway) => SidesOf(layout, runway).Bars;

    private static RunwayBarSides SidesOf(AirportGroundLayout layout, RunwayInfo runway) =>
        BarIndexes.GetValue(layout, RunwayBarIndex.For).Of(runway);

    /// <summary>The runway hold-short bar nodes of <paramref name="runway"/> on <paramref name="point"/>'s side of its centreline.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>Its bar nodes on that side, in the layout's order.</returns>
    public static IEnumerable<GroundNode> SameSideBars(AirportGroundLayout layout, RunwayInfo runway, LatLon point)
    {
        RunwayBarSides sides = SidesOf(layout, runway);
        bool right = IsRightOfCentreline(runway, point);
        for (int i = 0; i < sides.Bars.Length; i++)
        {
            if (sides.IsRight[i] == right)
            {
                yield return sides.Bars[i];
            }
        }
    }

    /// <summary>The nearest of <paramref name="runway"/>'s bars on <paramref name="point"/>'s side of its centreline.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>The bar, the first in the layout's order on a tie; null when that side has no bar.</returns>
    public static GroundNode? NearestSameSideBar(AirportGroundLayout layout, RunwayInfo runway, LatLon point)
    {
        GroundNode? nearest = null;
        double nearestNm = double.MaxValue;
        foreach (GroundNode bar in SameSideBars(layout, runway, point))
        {
            double distanceNm = GeoMath.DistanceNm(bar.Position, point);
            if (distanceNm < nearestNm)
            {
                nearest = bar;
                nearestNm = distanceNm;
            }
        }

        return nearest;
    }

    /// <summary>How far (ft) <paramref name="point"/> lies from <paramref name="runway"/>'s centreline, between its ends.</summary>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>The distance (ft).</returns>
    public static double CentrelineFt(RunwayInfo runway, LatLon point) =>
        GeoMath.DistanceToSegmentFt(point, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));

    /// <summary>Which side of <paramref name="runway"/>'s centreline, end 1 to end 2, <paramref name="point"/> lies on: true to the right.</summary>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>True right of the centreline.</returns>
    public static bool IsRightOfCentreline(RunwayInfo runway, LatLon point)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        double alongDeg = GeoMath.BearingTo(end1, new LatLon(runway.Lat2, runway.Lon2));
        return GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, point)) > 0.0;
    }

    /// <summary>
    /// Whether <paramref name="point"/> is nearer <paramref name="runway"/>'s centreline than the nearest of the runway's bars on
    /// the point's side (pavement aside); false when that side has no bar.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>True inside the bars' line.</returns>
    public static bool IsInsideBarLine(AirportGroundLayout layout, RunwayInfo runway, LatLon point) =>
        (NearestSameSideBar(layout, runway, point) is { } nearest) && (CentrelineFt(runway, point) < CentrelineFt(runway, nearest.Position));
}
