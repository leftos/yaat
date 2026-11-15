using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Pins that the per-tick ground cost cuts change no result: the pair prefilter
/// (<see cref="GroundConflictDetector.IsBeyondSearchRange"/>) never skips a pair the range gate would keep, and the
/// memoised hold-short setback (<see cref="TaxiRoute.HoldShortSetbackNm(int, int, HoldShortPoint)"/>) equals the walk a
/// route with an empty memo makes, after a crossing is appended and after a bar's stop moves.
/// </summary>
public class GroundTickCostEquivalenceTests(ITestOutputHelper output)
{
    private const int MaxNodes = 400;
    private const string Clearance = "TAXI A L F 28L";
    private const double BarMoveDeg = 1e-5;

    [Fact]
    public void IsBeyondSearchRange_NeverSkipsAPairInsideTheGate()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        List<GroundNode> all = [.. layout.Nodes.Values];
        int step = Math.Max(1, (all.Count + MaxNodes - 1) / MaxNodes);
        List<LatLon> nodes = [.. all.Where((_, i) => i % step == 0).Select(n => n.Position)];
        int skipped = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                if (!GroundConflictDetector.IsBeyondSearchRange(nodes[i].Lat, nodes[j].Lat))
                {
                    continue;
                }

                skipped++;
                double distNm = GeoMath.DistanceNm(nodes[i], nodes[j]);
                Assert.True(distNm > GroundConflictDetector.SearchRangeNm, $"skipped a pair {distNm:F4} nm apart, inside the gate");
            }
        }

        output.WriteLine($"{nodes.Count} nodes, {skipped} pairs skipped");
        Assert.True(skipped > 0, "the prefilter skipped no pair: the test is vacuous");
    }

    /// <summary>
    /// Routes from the SFO departure clearance of <c>QuickCommandSimAcceptanceTests.HoldShortOf28L</c> (<c>TAXI A L F 28L</c>
    /// from spots 1 and 3): every bar against every start segment, then with a crossing of 28L appended as the CROSS handler
    /// appends it (the values already read are unchanged: the walk reads no segment past the bar's), then with a bar's stop moved.
    /// </summary>
    [Fact]
    public void HoldShortSetbackMemo_MatchesTheUnmemoisedWalk()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        foreach (string spot in new[] { "1", "3" })
        {
            AircraftState aircraft = SfoGroundHarness.SpawnAtSpot(ground, "DEP" + spot, "B738", spot);
            CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, Clearance);
            Assert.True(result.Success, result.Message);
            TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{Clearance} left no route");
            Assert.Contains(route.HoldShortPoints, bar => bar.Latitude is not null);

            Dictionary<(HoldShortPoint Bar, int BarSegment, int Start), double> beforeAppend = AssertMemoMatchesFreshWalk(route);
            HoldShortPoint terminal = route.HoldShortPoints[^1];
            route.Segments.AddRange(CrossingOf(ground.Layout, terminal).Segments);
            foreach (((HoldShortPoint bar, int barSegment, int start), double setbackNm) in beforeAppend)
            {
                Assert.Equal(setbackNm, route.HoldShortSetbackNm(barSegment, start, bar));
            }

            AssertMemoMatchesFreshWalk(route);

            HoldShortPoint moved = route.HoldShortPoints.First(bar => bar.Latitude is not null);
            moved.Latitude += BarMoveDeg;
            moved.Longitude += BarMoveDeg;
            AssertMemoMatchesFreshWalk(route);
        }
    }

    /// <summary>
    /// Every bar of <paramref name="route"/> at every segment ending at its node, from every start segment up to that one: the
    /// memoised setback, read twice so the second read is a memo hit, equals a fresh route's over the same segments and bars.
    /// Returns every value compared, by bar, bar segment and start segment.
    /// </summary>
    private static Dictionary<(HoldShortPoint Bar, int BarSegment, int Start), double> AssertMemoMatchesFreshWalk(TaxiRoute route)
    {
        var compared = new Dictionary<(HoldShortPoint Bar, int BarSegment, int Start), double>();
        foreach (HoldShortPoint bar in route.HoldShortPoints)
        {
            for (int barSegment = 0; barSegment < route.Segments.Count; barSegment++)
            {
                if (route.Segments[barSegment].ToNodeId != bar.NodeId)
                {
                    continue;
                }

                for (int start = 0; start <= barSegment; start++)
                {
                    var fresh = new TaxiRoute { Segments = [.. route.Segments], HoldShortPoints = [.. route.HoldShortPoints] };
                    route.HoldShortSetbackNm(barSegment, start, bar);
                    double memoised = route.HoldShortSetbackNm(barSegment, start, bar);
                    Assert.Equal(fresh.HoldShortSetbackNm(barSegment, start, bar), memoised);
                    compared[(bar, barSegment, start)] = memoised;
                }
            }
        }

        Assert.True(compared.Count > 0, "no bar lies at the end of a segment of the route");
        return compared;
    }

    /// <summary>
    /// The shortest route from <paramref name="terminal"/> to another hold-short bar of its runway, found the way the CROSS
    /// handler's completed-route crossing finds its candidates (<see cref="TaxiPathfinder.FindRoute"/>).
    /// </summary>
    private static TaxiRoute CrossingOf(AirportGroundLayout layout, HoldShortPoint terminal)
    {
        RunwayIdentifier runway = layout.Nodes[terminal.NodeId].RunwayId ?? throw new InvalidOperationException("the terminal bar has no runway");
        TaxiRoute? best = layout
            .Nodes.Values.Where(n =>
                (n.Id != terminal.NodeId) && (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } other) && other.Equals(runway)
            )
            .Select(n => TaxiPathfinder.FindRoute(layout, terminal.NodeId, n.Id, AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large))
            .OfType<TaxiRoute>()
            .MinBy(r => r.TotalDistanceNm);
        Assert.NotNull(best);
        return best;
    }
}
