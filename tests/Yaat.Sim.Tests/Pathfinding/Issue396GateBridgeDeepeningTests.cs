using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Issue #396: the parking→taxiway start bridge (<c>SegmentExpander.BridgeStartToTaxiway</c>) capped its
/// BFS at three hops. From SFO gate B4 the only M3-carrying node within three hops is a corner-arc node
/// entered heading ESE whose sole M3 edge departs WNW — an inadmissible U-turn — while the real M3/RAMP
/// junction sits four hops down the straight gate lead-out. The bridge committed to the dead end and
/// <c>TAXI M3 M2 A H B M1 1L</c> failed "No valid path from M3 to M2 — transition infeasible".
///
/// The bridge now deepens its search only when every shallow candidate lacks an admissible onward
/// continuation; gates with a good three-hop entry keep today's pick.
/// </summary>
public class Issue396GateBridgeDeepeningTests
{
    private readonly ITestOutputHelper _output;

    public Issue396GateBridgeDeepeningTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout? LoadSfo() => TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");

    private static TaxiRoute? ResolveFromGate(
        AirportGroundLayout layout,
        string parking,
        string[] path,
        string destinationRunway,
        List<string> diag,
        out string? failReason
    )
    {
        var gate = layout.FindParkingByName(parking);
        Assert.True(gate is not null, $"parking {parking} not found in the SFO layout");

        return TaxiPathfinder.ResolveExplicitPath(
            layout,
            gate.Id,
            [.. path],
            out failReason,
            new ExplicitPathOptions
            {
                DestinationRunway = destinationRunway,
                AirportId = "SFO",
                StartHeadingTrue = gate.TrueHeading?.Degrees,
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Jet
        );
    }

    private static int BridgeEdgeCount(List<string> diag)
    {
        var line = diag.FirstOrDefault(l => l.StartsWith("[bridge] start=", StringComparison.Ordinal));
        Assert.True(line is not null, "expected a [bridge] diagnostic line");
        var match = Regex.Match(line, @"\((\d+) edges");
        Assert.True(match.Success, $"unparseable bridge line: {line}");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public void TaxiM3M2FromGateB4_EntersM3AtTheRampJunction()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var diag = new List<string>();
        var route = ResolveFromGate(layout, "B4", ["M3", "M2", "A", "H", "B", "M1"], "1L", diag, out string? failReason);
        foreach (var line in diag.Where(l => l.StartsWith("[bridge]", StringComparison.Ordinal)))
        {
            _output.WriteLine(line);
        }

        Assert.True(route is not null, $"TAXI M3 M2 A H B M1 1L from B4 must resolve: {failReason}");
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => s.TaxiwayName)));

        // The route must enter M3 at the ramp junction (several RAMP lead-outs meet M3 there) heading
        // south along the lane — not through the corner arc that forces a U-turn.
        var firstM3 = route.Segments.First(s => s.Edge.Edge.MatchesTaxiway("M3"));
        Assert.True(
            firstM3.Edge.FromNode.Edges.Count(e => e.IsRamp) >= 2,
            $"first M3 segment should depart the M3/RAMP junction, departed node {firstM3.FromNodeId} instead"
        );
        Assert.InRange(firstM3.Edge.DepartureBearing, 195.0, 220.0);

        // No pirouette between consecutive segments: every turn along the route is within the jet limit.
        // (The initial swing out of the gate — parked heading 55° onto a 210° lead-out — is the accepted
        // no-pushback-model artifact; PartialRoute.StartAt carries no arrival bearing, so it is not gated.)
        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet);
        for (int i = 1; i < route.Segments.Count; i++)
        {
            var prev = route.Segments[i - 1].Edge;
            var cur = route.Segments[i].Edge;
            if (GeometricAdmissibility.IsNoOpEdge(prev.Edge) || GeometricAdmissibility.IsNoOpEdge(cur.Edge))
            {
                continue;
            }

            double delta = RouteCostFunction.HeadingDelta(prev.ArrivalBearing, cur.DepartureBearing);
            Assert.True(delta <= limit, $"segment {i} ({cur.TaxiwayName} {cur.FromNodeId}->{cur.ToNodeId}) turns {delta:F0}° > {limit:F0}°");
        }
    }

    [Fact]
    public void TaxiYHBFromGateB13_KeepsShallowBridge()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var diag = new List<string>();
        var route = ResolveFromGate(layout, "B13", ["Y", "H", "B", "M1"], "1L", diag, out string? failReason);
        Assert.True(route is not null, $"TAXI Y H B M1 1L from B13 must resolve: {failReason}");

        // A gate whose three-hop bridge already reaches a usable entry must not be re-searched deeper.
        Assert.DoesNotContain(diag, l => l.Contains("deepened", StringComparison.OrdinalIgnoreCase));
        Assert.True(BridgeEdgeCount(diag) <= 3, "B13's bridge onto Y should stay within the shallow three-hop reach");
    }
}
