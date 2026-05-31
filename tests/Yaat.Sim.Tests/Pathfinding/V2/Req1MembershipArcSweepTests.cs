using System.Text;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Requirement ① guard (broad). Routes explicit two-token clearances <c>[X, Y]</c> for every pair
/// of connected taxiways across OAK/SFO/FLL on fillet V2, and asserts no FINAL route contains an
/// INTERIOR membership taxiway-junction arc diversion — a non-runway <c>"X - Y"</c> arc segment,
/// flanked by the same single-name taxiway on both sides, that has no identical single-name twin
/// edge between the same nodes. That signature is the route physically leaving the walked taxiway
/// onto a crossing one and returning (the <c>LocalSearchToJunction</c> gap).
///
/// <para>
/// Before the soft-penalty fix this flagged 29 real diversions (SFO 9, FLL 20). The fix
/// (<see cref="RouteCostFunction.MembershipJunctionArcContinuationCostNm"/> applied in
/// <c>SegmentExpander.LocalSearchToJunction</c>) drives it to zero. Runway-crossing arcs
/// (<c>IsRunwayJunction</c>) and benign parallel-duplicate corner arcs (an identical single-name
/// twin exists, e.g. fillet V2's coincident <c>"A"</c> and <c>"A - A8"</c> arcs) are excluded —
/// both are physically on the taxiway.
/// </para>
/// </summary>
[Trait("Category", "PathfinderGrid")]
public class Req1MembershipArcSweepTests
{
    private readonly ITestOutputHelper _output;

    public Req1MembershipArcSweepTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static readonly string[] Airports = ["OAK", "SFO", "FLL"];

    [Fact]
    public void Sweep_ConnectedTaxiwayPairs_NoInteriorMembershipArcDiversion()
    {
        var report = new StringBuilder();
        int grandFlagged = 0;
        int grandPairs = 0;

        foreach (var airport in Airports)
        {
            var layout = new TestAirportGroundData(FilletMode.V2).GetLayout(airport);
            if (layout is null)
            {
                report.AppendLine($"## {airport}: unavailable");
                continue;
            }

            var twNodes = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in layout.Nodes.Values)
            {
                foreach (var edge in node.Edges)
                {
                    foreach (var name in NamesOf(edge))
                    {
                        if (
                            name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            continue;
                        }

                        if (!twNodes.TryGetValue(name, out var set))
                        {
                            set = [];
                            twNodes[name] = set;
                        }

                        set.Add(node.Id);
                    }
                }
            }

            int pairs = 0;
            int flagged = 0;
            var names = twNodes.Keys.OrderBy(n => n).ToList();

            foreach (var x in names)
            {
                foreach (var y in names)
                {
                    if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var junctions = twNodes[x].Intersect(twNodes[y]).ToList();
                    if (junctions.Count == 0)
                    {
                        continue;
                    }

                    int startId = twNodes[x].OrderByDescending(nid => junctions.Min(j => NodeDist(layout, nid, j))).First();

                    pairs++;
                    var route = TaxiPathfinder.ResolveExplicitPath(
                        layout,
                        startId,
                        [x, y],
                        out _,
                        new ExplicitPathOptions { AirportId = airport },
                        AircraftCategory.Jet
                    );

                    if (route is null)
                    {
                        continue;
                    }

                    var diversion = FindInteriorMembershipArc(route, layout);
                    if (diversion is not null)
                    {
                        flagged++;
                        report.AppendLine($"  [{airport}] [{x},{y}] from #{startId}: {diversion}");
                    }
                }
            }

            grandPairs += pairs;
            grandFlagged += flagged;
            report.AppendLine($"## {airport}: pairs={pairs}  flagged={flagged}");
            _output.WriteLine($"{airport}: pairs={pairs} flagged={flagged}");
        }

        report.Insert(0, $"SUMMARY: pairs={grandPairs}  flagged={grandFlagged}\n\n");
        Directory.CreateDirectory(".tmp");
        File.WriteAllText(Path.Combine(".tmp", "req1-membership-sweep.log"), report.ToString());
        _output.WriteLine($"SUMMARY: pairs={grandPairs} flagged={grandFlagged}  (.tmp/req1-membership-sweep.log)");

        Assert.Equal(0, grandFlagged);
    }

    private static IEnumerable<string> NamesOf(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    private static double NodeDist(AirportGroundLayout layout, int a, int b) =>
        GeoMath.DistanceNm(layout.Nodes[a].Position, layout.Nodes[b].Position);

    /// <summary>
    /// First INTERIOR membership taxiway-junction arc segment (non-runway "X - Y" arc) flanked by
    /// the same single-name taxiway on both sides AND lacking an identical single-name twin edge
    /// between the same nodes — i.e. a real physical diversion off the walked taxiway. Null when
    /// none. Runway-crossing arcs and benign parallel-duplicate arcs (twin present) are excluded.
    /// </summary>
    private static string? FindInteriorMembershipArc(TaxiRoute route, AirportGroundLayout layout)
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
            if (prev is null || next is null || !string.Equals(prev, next, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasSingleNameTwin(layout, segs[i].FromNodeId, segs[i].ToNodeId, prev))
            {
                continue;
            }

            return $"interior membership arc '{arc.TaxiwayName}' #{segs[i].FromNodeId}->#{segs[i].ToNodeId} flanked by '{prev}'";
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
            if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }
}
