using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class FilletVisualizerTests
{
    private readonly ITestOutputHelper _output;

    public FilletVisualizerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OAK_VisualizeFillet()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        // GeoJsonParser now auto-applies fillets
        var layout = GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);

        // Pick a node near where the old node 52 was (~37.7213, -122.2208) to visualize arcs in that area
        var probe = layout.Nodes.Values.OrderBy(n => GeoMath.DistanceNm(n.Position, new LatLon(37.7213, -122.2208))).First();

        var visibleNodes = Collect2Hops(layout, probe.Id);
        string svg = GenerateSvg(layout, probe.Id, visibleNodes, $"Filleted area near node {probe.Id}");

        _output.WriteLine($"Probe node {probe.Id} ({probe.Type}): {probe.Edges.Count} edges");
        foreach (var e in probe.Edges)
        {
            string type = e is GroundArc a ? $"ARC MinR={a.MinRadiusOfCurvatureFt:F1}ft" : "EDGE";
            _output.WriteLine($"  {type} {e.TaxiwayName}: {e.Nodes[0].Id}--{e.Nodes[1].Id}");
        }

        _output.WriteLine($"\nLayout: {layout.Nodes.Count} nodes, {layout.Edges.Count} edges, {layout.Arcs.Count} arcs");

        string outPath = Path.Combine(".tmp", "fillet-viz.html");
        Directory.CreateDirectory(".tmp");
        File.WriteAllText(
            outPath,
            $"""
            <!DOCTYPE html>
            <html>
            <head><title>Fillet Visualization — OAK</title></head>
            <body style="background: #1a1a1a; color: white; font-family: monospace; padding: 20px;">
            <h2>Filleted area near node {probe.Id}</h2>
            {svg}
            </body>
            </html>
            """
        );

        _output.WriteLine($"\nVisualization: {Path.GetFullPath(outPath)}");
    }

    private static HashSet<int> Collect2Hops(AirportGroundLayout layout, int startId)
    {
        var result = new HashSet<int> { startId };
        if (!layout.Nodes.TryGetValue(startId, out var startNode))
        {
            return result;
        }

        foreach (var e in startNode.Edges)
        {
            int nid = e.OtherNodeId(startId);
            result.Add(nid);
            if (layout.Nodes.TryGetValue(nid, out var neighbor))
            {
                foreach (var e2 in neighbor.Edges)
                {
                    result.Add(e2.OtherNodeId(nid));
                }
            }
        }

        return result;
    }

    private static string GenerateSvg(AirportGroundLayout layout, int highlightNodeId, HashSet<int> visibleNodes, string title)
    {
        var positions = new List<(int Id, double Lat, double Lon)>();
        foreach (var nid in visibleNodes)
        {
            if (layout.Nodes.TryGetValue(nid, out var n))
            {
                positions.Add((nid, n.Position.Lat, n.Position.Lon));
            }
        }

        if (positions.Count == 0)
        {
            return "<p>No nodes to display</p>";
        }

        double minLat = positions.Min(p => p.Lat);
        double maxLat = positions.Max(p => p.Lat);
        double minLon = positions.Min(p => p.Lon);
        double maxLon = positions.Max(p => p.Lon);

        double padLat = (maxLat - minLat) * 0.15 + 0.0001;
        double padLon = (maxLon - minLon) * 0.15 + 0.0001;
        minLat -= padLat;
        maxLat += padLat;
        minLon -= padLon;
        maxLon += padLon;

        int svgW = 800;
        int svgH = 800;

        double ScaleX(double lon) => (lon - minLon) / (maxLon - minLon) * svgW;
        double ScaleY(double lat) => svgH - ((lat - minLat) / (maxLat - minLat) * svgH);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{svgW}\" height=\"{svgH}\" xmlns=\"http://www.w3.org/2000/svg\" style=\"background: #222;\">");

        // Draw edges (only if both endpoints are visible)
        foreach (var edge in layout.Edges)
        {
            int id0 = edge.Nodes[0].Id;
            int id1 = edge.Nodes[1].Id;
            if (!visibleNodes.Contains(id0) || !visibleNodes.Contains(id1))
            {
                continue;
            }

            double x1 = ScaleX(edge.Nodes[0].Position.Lon);
            double y1 = ScaleY(edge.Nodes[0].Position.Lat);
            double x2 = ScaleX(edge.Nodes[1].Position.Lon);
            double y2 = ScaleY(edge.Nodes[1].Position.Lat);

            string color =
                edge.IsRunwayCenterline ? "#ff6666"
                : edge.TaxiwayName == "RAMP" ? "#888"
                : "#66aaff";

            sb.AppendLine(Fmt($"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"{color}\" stroke-width=\"2\" />"));

            double mx = (x1 + x2) / 2;
            double my = (y1 + y2) / 2;
            sb.AppendLine(
                Fmt(
                    $"<text x=\"{mx}\" y=\"{my - 5}\" fill=\"{color}\" font-size=\"10\" text-anchor=\"middle\">{edge.TaxiwayName} ({id0}-{id1})</text>"
                )
            );
        }

        // Draw arcs
        foreach (var arc in layout.Arcs)
        {
            int id0 = arc.Nodes[0].Id;
            int id1 = arc.Nodes[1].Id;
            if (!visibleNodes.Contains(id0) || !visibleNodes.Contains(id1))
            {
                continue;
            }

            var bezier = arc.ToBezier();
            const int steps = 20;
            var arcPoints = new List<(double X, double Y)>();
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                var (lat, lon) = bezier.Evaluate(t);
                arcPoints.Add((ScaleX(lon), ScaleY(lat)));
            }

            for (int s = 0; s < arcPoints.Count - 1; s++)
            {
                sb.AppendLine(
                    Fmt(
                        $"<line x1=\"{arcPoints[s].X}\" y1=\"{arcPoints[s].Y}\" x2=\"{arcPoints[s + 1].X}\" y2=\"{arcPoints[s + 1].Y}\" stroke=\"#44ff44\" stroke-width=\"2\" />"
                    )
                );
            }

            var mid = arcPoints[arcPoints.Count / 2];
            sb.AppendLine(
                Fmt(
                    $"<text x=\"{mid.X}\" y=\"{mid.Y - 5}\" fill=\"#44ff44\" font-size=\"9\" text-anchor=\"middle\">{arc.TaxiwayName} arc ({id0}-{id1}) MinR={arc.MinRadiusOfCurvatureFt:F0}ft</text>"
                )
            );
        }

        // Draw nodes
        foreach (var nid in visibleNodes)
        {
            if (!layout.Nodes.TryGetValue(nid, out var node))
            {
                continue;
            }

            double x = ScaleX(node.Position.Lon);
            double y = ScaleY(node.Position.Lat);

            string fill = node.Type switch
            {
                GroundNodeType.RunwayHoldShort => "#ffaa00",
                GroundNodeType.Parking => "#00ff00",
                GroundNodeType.Spot => "#ffff00",
                _ when nid == highlightNodeId => "#ff0000",
                _ => "#ffffff",
            };

            int r = nid == highlightNodeId ? 8 : 4;

            sb.AppendLine(Fmt($"<circle cx=\"{x}\" cy=\"{y}\" r=\"{r}\" fill=\"{fill}\" stroke=\"white\" stroke-width=\"1\" />"));
            sb.AppendLine(Fmt($"<text x=\"{x + 8}\" y=\"{y + 4}\" fill=\"{fill}\" font-size=\"12\" font-weight=\"bold\">{nid}</text>"));
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
