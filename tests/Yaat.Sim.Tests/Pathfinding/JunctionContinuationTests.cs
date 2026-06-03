using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Requirement ①: on the collapsed-junction geometry the fillet generator produces, the
/// pathfinder must prefer a single-name continuation over a junction arc that
/// matches the walked taxiway only by membership (e.g. an <c>A - Q1</c> arc when
/// walking <c>A</c>), and must never backtrack onto the segment it just traversed.
///
/// These exercise the full ground stack: the pathfinder routing over the filleted
/// graph, where collapsed junctions retain membership-named arcs that the walker must
/// not mistake for a continuation.
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

    private static AirportGroundLayout? Layout(string airport) => new TestAirportGroundData(FilletMode.Standard).GetLayout(airport);

    /// <summary>
    /// FLL DAL880 <c>TAXI T T4 B B1 HS 10L</c> from parking. The
    /// T/T4/B junction (J75 area) collapses and retains membership-named junction
    /// arcs (<c>C1 - B</c>, <c>B - C</c>) plus parallel single-name edges; the
    /// walker gates candidates by membership and can pick the wrong one, producing
    /// the <c>766↔767</c> backtrack. The resolved route must contain no backtrack.
    /// </summary>
    [Fact]
    public void Fll_ResolveExplicitPath_TT4BB1_HasNoBacktrack()
    {
        var layout = Layout("FLL");
        if (layout is null)
        {
            _output.WriteLine("fll.geojson not found — skipping");
            return;
        }

        // DAL880's actual parking position at t=230 (from the bundle snapshot).
        const double ParkLat = 26.073763899148627;
        const double ParkLon = -80.14425458893693;
        var startNode = layout.Nodes.Values.OrderBy(n => GeoMath.DistanceNm(ParkLat, ParkLon, n.Position.Lat, n.Position.Lon)).First();

        var route = TaxiPathfinder.ResolveExplicitPath(
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
    /// SFO: walking taxiway <c>A</c> through the collapsed junction at node 1160 must
    /// continue on the single-name <c>A</c> edge, not divert onto the membership-matched
    /// junction arcs <c>A - Q1</c> / <c>A - RAMP</c> that also live at that node. Pure
    /// req ① (no V-shaped-taxiway complication). The walk starts on <c>A</c> just west of
    /// 1160 and must stay on single-name <c>A</c> with no backtrack.
    /// </summary>
    [Fact]
    public void Sfo_WalkA_ThroughCollapsedJunction_StaysOnSingleNameA()
    {
        var layout = Layout("SFO");
        if (layout is null)
        {
            _output.WriteLine("sfo.geojson not found — skipping");
            return;
        }

        // Nearest node carrying an A edge to a point just west of node 1160 (37.6224, -122.3905),
        // so the natural-terminus walk on A passes eastward through the 1160 collapse.
        const double WestOf1160Lat = 37.622400;
        const double WestOf1160Lon = -122.390500;
        var startNode = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("A")))
            .OrderBy(n => GeoMath.DistanceNm(WestOf1160Lat, WestOf1160Lon, n.Position.Lat, n.Position.Lon))
            .First();
        _output.WriteLine($"Start: #{startNode.Id} at ({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6})");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode.Id,
            ["A"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DiagnosticLog = msg => _output.WriteLine(msg) },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        _output.WriteLine($"Route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            _output.WriteLine($"  {seg.TaxiwayName, -10} #{seg.FromNodeId} → #{seg.ToNodeId}");
        }

        // No segment may be a membership-only junction arc (a multi-name "X - Y" edge):
        // walking A must not silently divert onto Q1/RAMP via an A-membership arc.
        foreach (var seg in route.Segments)
        {
            Assert.False(
                seg.Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 },
                $"Walk A diverted onto membership junction arc '{seg.TaxiwayName}' (#{seg.FromNodeId}→#{seg.ToNodeId})."
            );
        }

        AssertNoBacktrack(route, layout);
    }

    /// <summary>
    /// GitHub issue #165 — the bug that prompted the whole fillet + pathfinder rewrite.
    /// SKW3404 spawns at parking D8 (KSFO) and is instructed <c>TAXI A E B B3 A B1 Z S</c>.
    /// Two failure modes the ground stack must eliminate:
    /// <list type="number">
    /// <item>The pathfinder must not pick a junction arc that points away from where the
    /// route continues, which produced 180° corners and an on-axis spin.</item>
    /// <item>The route must honor the instructed taxiway order — stay on <c>B</c> all the
    /// way to the <c>B/B3</c> junction, take <c>B3</c>, then rejoin <c>A</c>. It must not
    /// join <c>A</c> early (off <c>B</c>) and then take <c>B3</c> off <c>A</c>, which is
    /// illegal given the clearance.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Sfo_Skw3404_TaxiAEBB3AB1ZS_HonorsOrderNoSpin()
    {
        var layout = Layout("SFO");
        if (layout is null)
        {
            _output.WriteLine("sfo.geojson not found — skipping");
            return;
        }

        var d8 = layout.FindParkingByName("D8");
        if (d8 is null)
        {
            _output.WriteLine("parking D8 not found — skipping");
            return;
        }

        _output.WriteLine($"Start: D8 = #{d8.Id} at ({d8.Position.Lat:F6}, {d8.Position.Lon:F6})");

        List<string> instructed = ["A", "E", "B", "B3", "A", "B1", "Z", "S"];
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            d8.Id,
            instructed,
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DiagnosticLog = msg => _output.WriteLine(msg) },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        var runs = ExtractSingleNameRuns(route);
        _output.WriteLine($"Route: {route.Segments.Count} segments; single-name runs: {string.Join(" ", runs)}");
        foreach (var seg in route.Segments)
        {
            _output.WriteLine($"  {seg.TaxiwayName, -10} #{seg.FromNodeId} → #{seg.ToNodeId}");
        }

        _output.WriteLine($"Warnings: {string.Join(" | ", route.Warnings)}");

        // Failure mode 1: no 180° spin / backtrack anywhere.
        AssertNoBacktrack(route, layout);

        // Failure mode 2: the instructed taxiway order is honored. The single-name runs must
        // contain the instruction as an in-order subsequence, so B is fully walked to the B/B3
        // junction (B before B3) and the second A comes after B3 — never B3 taken off the first A.
        AssertOrderedSubsequence(instructed, runs);

        // On the current SFO layout (vNAS data-api), taxiway A and B1 share a direct junction
        // (node 1159, carrying an A/B1 fillet arc), so the resolver bridges A→B1 with no inserted
        // connector. There must therefore be no "A and B1 do not connect directly" notification —
        // and in particular none that drags in a cleared taxiway such as B3.
        Assert.DoesNotContain(
            route.Warnings,
            w => w.Contains("A and B1", StringComparison.OrdinalIgnoreCase) && w.Contains("do not connect", StringComparison.OrdinalIgnoreCase)
        );

        // The leading RAMP (parking bridge from D8) must not be flagged as a "not in authorized
        // path" deviation, and walking A directly to the B1 junction is on cleared taxiways.
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in authorized path", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ordered list of single-name taxiway names traversed, collapsing consecutive duplicates
    /// and skipping multi-name junction arcs (which are transitions between taxiways, not a
    /// continuation of either). This is the sequence of taxiways the route actually walks.
    /// </summary>
    private static List<string> ExtractSingleNameRuns(TaxiRoute route)
    {
        var runs = new List<string>();
        foreach (var seg in route.Segments)
        {
            if (seg.Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            if (runs.Count == 0 || !runs[^1].Equals(seg.TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                runs.Add(seg.TaxiwayName);
            }
        }

        return runs;
    }

    /// <summary>
    /// Fails unless every element of <paramref name="expected"/> appears in
    /// <paramref name="actual"/> in order (a contiguous-or-gapped subsequence). Catches a
    /// skipped taxiway (e.g. B never walked) and an out-of-order traversal (e.g. the second A
    /// before B3).
    /// </summary>
    private static void AssertOrderedSubsequence(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        int ai = 0;
        foreach (string want in expected)
        {
            bool found = false;
            while (ai < actual.Count)
            {
                if (actual[ai].Equals(want, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    ai++;
                    break;
                }

                ai++;
            }

            if (!found)
            {
                Assert.Fail(
                    $"Instructed taxiway '{want}' not found in order. Instructed=[{string.Join(" ", expected)}] walked=[{string.Join(" ", actual)}]"
                );
            }
        }
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

    /// <summary>
    /// req ① for the INTERMEDIATE-segment path (<c>LocalSearchToJunction</c>), not just the
    /// natural-terminus walk: walking taxiway X toward its junction with Y must stay on
    /// single-name X and must not divert onto a membership taxiway-junction arc (<c>"X - Z"</c>)
    /// and back. Surfaced by the membership-arc sweep (SFO Z→B diverts via <c>Z - CZ</c>;
    /// FLL A→B via <c>A - A1</c>). Runway-crossing arcs (<c>IsRunwayJunction</c>, e.g.
    /// <c>"H - RWY01L/19R"</c>) ARE continuations and remain allowed. Start = the X node
    /// farthest from the X∩Y junction, so the walk must actually traverse X.
    /// </summary>
    [Theory]
    [InlineData("SFO", "Z", "B")]
    [InlineData("FLL", "A", "B")]
    public void IntermediateWalk_StaysOnTaxiway_NoMembershipArcDiversion(string airport, string x, string y)
    {
        var layout = Layout(airport);
        if (layout is null)
        {
            _output.WriteLine($"{airport} layout unavailable — skipping");
            return;
        }

        int startId = FarthestNodeOnTaxiwayFromJunction(layout, x, y);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startId,
            [x, y],
            out string? failReason,
            new ExplicitPathOptions { AirportId = airport, DiagnosticLog = msg => _output.WriteLine(msg) },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        _output.WriteLine($"{airport} [{x},{y}] from #{startId}: {route.Segments.Count} segs");
        foreach (var seg in route.Segments)
        {
            _output.WriteLine($"  {seg.TaxiwayName, -10} #{seg.FromNodeId} → #{seg.ToNodeId}");
        }

        string? diversion = FindInteriorMembershipArcDiversion(route, layout);
        Assert.True(diversion is null, $"Route diverted off the walked taxiway onto a membership junction arc and back: {diversion}");
    }

    private static int FarthestNodeOnTaxiwayFromJunction(AirportGroundLayout layout, string x, string y)
    {
        var xNodes = NodesTouching(layout, x);
        var junctions = xNodes.Intersect(NodesTouching(layout, y)).ToList();
        Assert.NotEmpty(junctions);
        return xNodes.OrderByDescending(nid => junctions.Min(j => GeoMath.DistanceNm(layout.Nodes[nid].Position, layout.Nodes[j].Position))).First();
    }

    private static HashSet<int> NodesTouching(AirportGroundLayout layout, string taxiway)
    {
        var set = new HashSet<int>();
        foreach (var node in layout.Nodes.Values)
        {
            foreach (var edge in node.Edges)
            {
                string[] names = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
                if (names.Any(n => n.Equals(taxiway, StringComparison.OrdinalIgnoreCase)))
                {
                    set.Add(node.Id);
                    break;
                }
            }
        }

        return set;
    }

    /// <summary>
    /// Returns the first INTERIOR membership taxiway-junction arc segment (a non-runway
    /// <c>"X - Y"</c> arc) whose nearest single-name neighbours on both sides name the SAME
    /// taxiway — i.e. the route physically left that taxiway onto a membership arc and returned.
    /// Null when no such diversion exists. Runway-crossing arcs (<c>IsRunwayJunction</c>) are
    /// ignored (they continue the taxiway across a runway). A membership arc with an identical
    /// single-name twin edge between the same nodes is also ignored — that is a benign
    /// parallel-duplicate corner arc (the fillet generator emits both <c>"A"</c> and <c>"A - A8"</c> arcs
    /// at one corner); the physical path is identical, so it is cosmetic mislabelling, not a
    /// diversion onto a crossing taxiway.
    /// </summary>
    private static string? FindInteriorMembershipArcDiversion(TaxiRoute route, AirportGroundLayout layout)
    {
        var segs = route.Segments;
        for (int i = 0; i < segs.Count; i++)
        {
            if (segs[i].Edge.Edge is not GroundArc arc || arc.TaxiwayNames.Length < 2 || arc.IsRunwayJunction)
            {
                continue;
            }

            string? prev = NearestSingleName(segs, i, -1);
            string? next = NearestSingleName(segs, i, +1);
            if (prev is null || next is null || !prev.Equals(next, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasSingleNameTwin(layout, segs[i].FromNodeId, segs[i].ToNodeId, prev))
            {
                continue;
            }

            return $"'{arc.TaxiwayName}' #{segs[i].FromNodeId}→#{segs[i].ToNodeId} flanked by '{prev}'";
        }

        return null;
    }

    private static bool HasSingleNameTwin(AirportGroundLayout layout, int fromId, int toId, string taxiway)
    {
        if (!layout.Nodes.TryGetValue(fromId, out var fromNode))
        {
            return false;
        }

        foreach (var edge in fromNode.Edges)
        {
            bool single = edge is not GroundArc arc || arc.TaxiwayNames.Length == 1;
            if (single && (edge.OtherNode(fromNode).Id == toId) && edge.TaxiwayName.Equals(taxiway, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NearestSingleName(IReadOnlyList<TaxiRouteSegment> segs, int from, int step)
    {
        for (int i = from + step; i >= 0 && i < segs.Count; i += step)
        {
            if (segs[i].Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            string name = segs[i].TaxiwayName;
            if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !name.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }
}
