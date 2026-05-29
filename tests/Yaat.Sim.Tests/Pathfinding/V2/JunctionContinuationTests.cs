using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Requirement ①: on the collapsed-junction geometry that fillet V2 produces, the
/// V2 pathfinder must prefer a single-name continuation over a junction arc that
/// matches the walked taxiway only by membership (e.g. an <c>A - Q1</c> arc when
/// walking <c>A</c>), and must never backtrack onto the segment it just traversed.
///
/// These run pathfinder-V2 ON fillet-V2 — the actual ship configuration, which no
/// other test currently exercises (the existing repros run V1-on-Legacy, where the
/// failure does not occur because Legacy fillets do not collapse junctions the same
/// way). See docs/plans/pathfinderv2/default-flip-triage.md (fillet-sweep req ①).
/// </summary>
public class JunctionContinuationTests
{
    private readonly ITestOutputHelper _output;

    public JunctionContinuationTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin the shared NavData/profile singletons before any layout build to avoid
        // cross-class races (see CLAUDE.md "Static singleton test races").
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout? V2Layout(string airport) => new TestAirportGroundData(FilletMode.V2).GetLayout(airport);

    /// <summary>
    /// FLL DAL880 <c>TAXI T T4 B B1 HS 10L</c> from parking. On fillet V2 the
    /// T/T4/B junction (J75 area) collapses and retains membership-named junction
    /// arcs (<c>C1 - B</c>, <c>B - C</c>) plus parallel single-name edges; the V2
    /// walker gates candidates by membership and can pick the wrong one, producing
    /// the <c>766↔767</c> backtrack. The resolved route must contain no backtrack.
    /// </summary>
    [Fact]
    public void Fll_ResolveExplicitPath_TT4BB1_OnV2_HasNoBacktrack()
    {
        var layout = V2Layout("FLL");
        if (layout is null)
        {
            _output.WriteLine("fll.geojson not found — skipping");
            return;
        }

        // DAL880's actual parking position at t=230 (from the bundle snapshot).
        const double ParkLat = 26.073763899148627;
        const double ParkLon = -80.14425458893693;
        var startNode = layout.Nodes.Values.OrderBy(n => GeoMath.DistanceNm(ParkLat, ParkLon, n.Position.Lat, n.Position.Lon)).First();

        var route = new TaxiPathfinderV2().ResolveExplicitPath(
            layout,
            startNode.Id,
            ["T", "T4", "B", "B1"],
            out string? failReason,
            new ExplicitPathOptions
            {
                DestinationRunway = "10L",
                ExplicitHoldShorts = ["10L"],
                AirportId = "FLL",
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        _output.WriteLine($"Route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            _output.WriteLine($"  {seg.TaxiwayName, -8} #{seg.FromNodeId} → #{seg.ToNodeId}");
        }

        AssertNoBacktrack(route, layout);
    }

    /// <summary>
    /// Fails if the route reverses onto the segment it just traversed (an explicit
    /// node-pair backtrack <c>i→j</c> then <c>j→i</c>) or makes a &gt;150° bearing
    /// flip between consecutive segments (a geometric U-turn).
    /// </summary>
    private static void AssertNoBacktrack(TaxiRoute route, AirportGroundLayout layout)
    {
        for (int i = 1; i < route.Segments.Count; i++)
        {
            var prev = route.Segments[i - 1];
            var curr = route.Segments[i];

            if ((curr.FromNodeId == prev.ToNodeId) && (curr.ToNodeId == prev.FromNodeId))
            {
                Assert.Fail($"Backtrack at segment {i - 1}→{i}: #{prev.FromNodeId}→#{prev.ToNodeId} then #{curr.FromNodeId}→#{curr.ToNodeId}.");
            }

            if (
                !layout.Nodes.TryGetValue(prev.FromNodeId, out var pFrom)
                || !layout.Nodes.TryGetValue(prev.ToNodeId, out var pTo)
                || !layout.Nodes.TryGetValue(curr.FromNodeId, out var cFrom)
                || !layout.Nodes.TryGetValue(curr.ToNodeId, out var cTo)
            )
            {
                continue;
            }

            double prevBearing = GeoMath.BearingTo(pFrom.Position, pTo.Position);
            double currBearing = GeoMath.BearingTo(cFrom.Position, cTo.Position);
            double diff = Math.Abs(NormalizeAngleDiff(currBearing - prevBearing));

            if (diff > 150)
            {
                Assert.Fail(
                    $"U-turn at segment {i - 1}→{i}: {prev.TaxiwayName} #{prev.FromNodeId}→#{prev.ToNodeId} (brg {prevBearing:F0}°) "
                        + $"vs {curr.TaxiwayName} #{curr.FromNodeId}→#{curr.ToNodeId} (brg {currBearing:F0}°) differ by {diff:F0}°."
                );
            }
        }
    }

    private static double NormalizeAngleDiff(double deg)
    {
        while (deg > 180)
        {
            deg -= 360;
        }
        while (deg < -180)
        {
            deg += 360;
        }
        return deg;
    }
}
