using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>Tests for <see cref="TaxiPathfinder"/> on real airport layouts.</summary>
public sealed class TaxiPathfinderTests(ITestOutputHelper output)
{
    /// <summary>
    /// An aircraft on G two straight nodes north of OAK's northern 28R bar on G (further when that is still within
    /// <see cref="TaxiPathfinder.AtRunwayHoldShortRadiusFt"/> of a 28R bar), facing it: a bare <c>TAXI 28R</c> is a
    /// short run down one taxiway to that bar, so it resolves as an adjacent-runway route even where the run's last
    /// segment is the C/G fillet arc at the bar, whose joined name is not G.
    /// </summary>
    [Fact]
    public void Oak_Taxi28R_FromGNearThe28RBar_IsAnAdjacentRunwayRoute()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        GroundNode bar = layout
            .GetRunwayHoldShortNodes("28R")
            .Where(n => n.Edges.Any(e => (e is GroundEdge) && (e.TaxiwayName == "G")))
            .MaxBy(n => n.Position.Lat)!;
        // Two straight G nodes north, and further while still inside the at-the-bar radius, which answers without a route.
        GroundNode start = NextNorthOnG(NextNorthOnG(bar));
        while (layout.GetRunwayHoldShortNodes("28R").Any(n => DistanceFt(start, n) <= TaxiPathfinder.AtRunwayHoldShortRadiusFt))
        {
            start = NextNorthOnG(start);
        }

        Assert.True(
            DistanceFt(start, bar) <= TaxiPathfinder.AdjacentRunwayHoldShortMaxFt,
            $"start {start.Id} is {DistanceFt(start, bar):F0} ft from bar {bar.Id}"
        );
        var heading = new TrueHeading(GeoMath.BearingTo(start.Position, bar.Position));

        TaxiRoute? route = TaxiPathfinder.FindAdjacentRunwayRoute(
            layout,
            start,
            new StartPose(start.Position, heading.Degrees, AircraftBody.Of("B738")),
            "28R",
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        output.WriteLine($"bar {bar.Id}, start {start.Id}: {route?.ToSummary() ?? "(no route)"}");
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.Equal(bar.Id, route.Segments[^1].ToNodeId);
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.Reason);
        Assert.Equal(bar.Id, hold.NodeId);
    }

    /// <summary>
    /// An aircraft on C at OAK one straight C edge short of the node where a "C - G" fillet arc leaves C for G next to 28R's bar
    /// on G (the C/G corner by the northern 28R bar on G, the arc's G end one straight G edge short of the bar), facing along C
    /// toward the arc: the run to that bar is C, the arc, then G to the bar, so a bare <c>TAXI 28R</c> does not resolve as
    /// "already at the runway".
    /// </summary>
    [Fact]
    public void BareTaxiRunway_FromCJustBeforeArcOntoG_NotAlreadyAtRunway()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        (GroundNode bar, GroundNode arcEnd, GroundNode arcStart) = layout
            .GetRunwayHoldShortNodes("28R")
            .Where(b => RunwayHoldBars.BarTaxiwayName(b) == "G")
            .SelectMany(b => b.Edges.Where(e => e is GroundEdge { TaxiwayName: "G" }).Select(e => (Bar: b, ArcEnd: e.OtherNode(b))))
            .SelectMany(t =>
                t.ArcEnd.Edges.OfType<GroundArc>()
                    .Where(arc => arc.MatchesTaxiway("C") && arc.MatchesTaxiway("G"))
                    .Select(arc => (t.Bar, t.ArcEnd, ArcStart: arc.OtherNode(t.ArcEnd)))
            )
            .First(t => t.ArcStart.Edges.Any(e => e is GroundEdge { TaxiwayName: "C" }));
        output.WriteLine($"bar {bar.Id} on G, {DistanceFt(arcEnd, bar):F1} ft from the C - G arc's G end {arcEnd.Id}");
        GroundNode start = arcStart
            .Edges.Where(e => e is GroundEdge { TaxiwayName: "C" })
            .Select(e => e.OtherNode(arcStart))
            .MaxBy(n => DistanceFt(n, bar))!;
        Assert.True(DistanceFt(start, bar) > TaxiPathfinder.AtRunwayHoldShortRadiusFt, $"start {start.Id} is at bar {bar.Id}");
        var heading = new TrueHeading(GeoMath.BearingTo(start.Position, arcStart.Position));

        TaxiRoute? route = TaxiPathfinder.FindAdjacentRunwayRoute(
            layout,
            start,
            new StartPose(start.Position, heading.Degrees, AircraftBody.Of("B738")),
            "28R",
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        output.WriteLine($"bar {bar.Id}, arc from {arcStart.Id}, start {start.Id}: {route?.ToSummary() ?? "(no route)"}");
        Assert.Null(route);
    }

    private static double DistanceFt(GroundNode a, GroundNode b) => GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;

    /// <summary>The northernmost node one straight G edge from <paramref name="node"/>.</summary>
    private static GroundNode NextNorthOnG(GroundNode node) =>
        node.Edges.OfType<GroundEdge>().Where(e => e.TaxiwayName == "G").Select(e => e.OtherNode(node)).MaxBy(n => n.Position.Lat)!;
}
