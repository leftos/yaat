using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>
/// Guards against the stable-anchor ID collision bug: when ExtendWithStableAnchors redirects
/// a cut to a pre-existing node ID, that ID can collide with a cut ID from a different junction
/// in the executor's cutNode dictionary, causing corner arc/chord endpoints to resolve to the
/// wrong tangent-cut node hundreds or thousands of feet away.
/// Also guards against zero-distance edges emitted by the V2 edge-split when a tangent cut
/// lands coincident with an existing Spot or Parking endpoint node.
/// </summary>
public class FilletCornerSpanGuardTests
{
    private const double MaxSaneCornerSpanFt = 300.0;

    /// <summary>
    /// Edges shorter than this are zero-distance no-ops (same threshold as
    /// <see cref="GeometricAdmissibility.NoOpEdgeThresholdNm"/>).
    /// </summary>
    private const double ZeroDistanceEdgeThresholdFt = GeometricAdmissibility.NoOpEdgeThresholdNm * GeoMath.FeetPerNm;

    private readonly ITestOutputHelper _output;

    public FilletCornerSpanGuardTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("sfo")]
    [InlineData("oak")]
    [InlineData("fll")]
    public void V2_CornerEdgesAndChords_NoSpanExceedsMaxSane(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        new FilletArcGenerator().Apply(layout);

        var violations = new List<string>();

        foreach (var edge in layout.Edges)
        {
            string origin = edge.Origin ?? "";
            if (!origin.StartsWith("V2:corner", StringComparison.Ordinal))
            {
                continue;
            }

            double distFt = edge.DistanceNm * GeoMath.FeetPerNm;
            if (distFt > MaxSaneCornerSpanFt)
            {
                violations.Add(
                    $"{edge.Nodes[0].Id}->{edge.Nodes[1].Id} {distFt:F0}ft [{origin}] " + $"from={edge.Nodes[0].Origin} to={edge.Nodes[1].Origin}"
                );
            }
        }

        foreach (var arc in layout.Arcs)
        {
            string origin = arc.Origin ?? "";
            if (!origin.StartsWith("V2:corner", StringComparison.Ordinal))
            {
                continue;
            }

            double distFt = arc.DistanceNm * GeoMath.FeetPerNm;
            if (distFt > MaxSaneCornerSpanFt)
            {
                violations.Add($"ARC {arc.Nodes[0].Id}->{arc.Nodes[1].Id} {distFt:F0}ft [{origin}]");
            }
        }

        foreach (var v in violations.Take(10))
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("sfo")]
    [InlineData("oak")]
    [InlineData("fll")]
    public void V2_EdgeSplit_NoZeroDistanceEdges(string shortId)
    {
        TestVnasData.EnsureInitialized();

        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        new FilletArcGenerator().Apply(layout);

        var seen = new HashSet<(int, int)>();
        var violations = new List<string>();

        foreach (var edge in layout.Edges)
        {
            int a = edge.Nodes[0].Id;
            int b = edge.Nodes[1].Id;
            if (!seen.Add((Math.Min(a, b), Math.Max(a, b))))
            {
                continue;
            }

            double distFt = edge.DistanceNm * GeoMath.FeetPerNm;
            if (distFt < ZeroDistanceEdgeThresholdFt)
            {
                violations.Add(
                    $"{a} ({edge.Nodes[0].Type}:{edge.Nodes[0].Name}) <-> {b} ({edge.Nodes[1].Type}:{edge.Nodes[1].Name})"
                        + $" twy={edge.TaxiwayName} dist={distFt:F2}ft origin={edge.Origin}"
                        + $" @ {edge.Nodes[0].Position.Lat:F6},{edge.Nodes[0].Position.Lon:F6}"
                );
            }
        }

        foreach (var v in violations)
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Guards against duplicate corner arcs: when the cut-redirect merges two arms' collinear
    /// tangent cuts onto one shared node, two corners (e.g. A/A and A/A8) emit geometrically
    /// coincident arcs between the SAME node pair — one single-name ("A"), one membership
    /// ("A - A8"). The membership twin is redundant (identical curve) yet makes the segment carry
    /// a membership label that requirement ① must work around. The executor must keep only one
    /// corner arc per node pair, preferring the single-name one.
    /// </summary>
    [Theory]
    [InlineData("sfo")]
    [InlineData("oak")]
    [InlineData("fll")]
    public void V2_CornerArcs_NoDuplicateNodePairs(string shortId)
    {
        TestVnasData.EnsureInitialized();

        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        new FilletArcGenerator().Apply(layout);

        var byPair = new Dictionary<(int, int), List<GroundArc>>();
        foreach (var arc in layout.Arcs)
        {
            if (arc.Origin?.StartsWith("V2:corner", StringComparison.Ordinal) != true)
            {
                continue;
            }

            var key = (Math.Min(arc.Nodes[0].Id, arc.Nodes[1].Id), Math.Max(arc.Nodes[0].Id, arc.Nodes[1].Id));
            if (!byPair.TryGetValue(key, out var list))
            {
                list = [];
                byPair[key] = list;
            }

            list.Add(arc);
        }

        var violations = byPair
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"#{kv.Key.Item1}<->#{kv.Key.Item2}: {string.Join(" | ", kv.Value.Select(a => $"[{a.TaxiwayName}] {a.Origin}"))}")
            .ToList();

        foreach (var v in violations)
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// The final V2 graph has no two coincident intersection nodes — guaranteed entirely at
    /// construction/plan time, with no post-execute node merge. Same-junction cross-arm and
    /// cross-junction coincident tangent cuts are merged in the plan
    /// (<c>SharedArmTangentPass.ApplyCrossArmCoalesce</c> + <c>ApplyGlobalCoincidentCutCoalesce</c>);
    /// runway-centerline projections reuse a pre-existing coincident node instead of minting one
    /// (<c>RunwayCrossingDetector.ResolveCenterlineProjectionNode</c>). <c>FilletGraphNormalizer</c>
    /// no longer merges coincident nodes, so this guard now proves those producers leave none.
    /// </summary>
    [Theory]
    [InlineData("sfo")]
    [InlineData("oak")]
    [InlineData("fll")]
    public void V2_NoCoincidentIntersectionNodes(string shortId)
    {
        TestVnasData.EnsureInitialized();

        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        new FilletArcGenerator().Apply(layout);

        var nodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).ToList();
        var violations = new List<string>();
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                double distFt = GeoMath.DistanceNm(nodes[i].Position, nodes[j].Position) * GeoMath.FeetPerNm;
                if (distFt < ZeroDistanceEdgeThresholdFt)
                {
                    continue;
                }

                if (distFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    violations.Add($"#{nodes[i].Id} ({nodes[i].Origin}) <-> #{nodes[j].Id} ({nodes[j].Origin}) {distFt:F2}ft");
                }
            }
        }

        foreach (var v in violations.Take(20))
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }
}
