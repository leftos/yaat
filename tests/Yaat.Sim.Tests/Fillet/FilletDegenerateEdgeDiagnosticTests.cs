using System.Text;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Decode degenerate self-loop origins for planner op naming (OAK gate triage).</summary>
public class FilletDegenerateEdgeDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletDegenerateEdgeDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    public void DumpDegenerateEdges_WithOrigin(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGenerator().Apply(layout);

        var sb = new StringBuilder();
        sb.AppendLine($"=== {shortId} degenerate / near-degenerate edges ===");
        foreach (var edge in layout.Edges)
        {
            int id0 = edge.Nodes[0].Id;
            int id1 = edge.Nodes[1].Id;
            double distFt = edge.DistanceNm * GeoMath.FeetPerNm;
            if ((id0 != id1) && (distFt > 0.1))
            {
                continue;
            }

            sb.AppendLine($"twy={edge.TaxiwayName} {id0}->{id1} dist={distFt:F2}ft origin={edge.Origin ?? "(null)"}");
        }

        _output.WriteLine(sb.ToString());
    }

    [Fact]
    public void Oak_DumpEdgesTouchingNode753()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse("oak", File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGenerator().Apply(layout);

        const int nodeId = 753;
        var sb = new StringBuilder();
        sb.AppendLine($"=== OAK edges touching node {nodeId} ===");
        if (layout.Nodes.TryGetValue(nodeId, out var node))
        {
            sb.AppendLine($"node type={node.Type} origin={node.Origin ?? "(null)"} pos={node.Position}");
        }
        else
        {
            sb.AppendLine("(node not in layout)");
        }

        foreach (var edge in layout.Edges.Where(e => (e.Nodes[0].Id == nodeId) || (e.Nodes[1].Id == nodeId)))
        {
            int other = edge.Nodes[0].Id == nodeId ? edge.Nodes[1].Id : edge.Nodes[0].Id;
            double distFt = edge.DistanceNm * GeoMath.FeetPerNm;
            sb.AppendLine($"  -> {other} twy={edge.TaxiwayName} dist={distFt:F2}ft origin={edge.Origin ?? "(null)"}");
        }

        _output.WriteLine(sb.ToString());
    }
}
