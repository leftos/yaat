// Generate individual fillet visualizations — one intersection per diagram
using System.Globalization;
using System.Text;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

double[] angles = [20, 40, 60, 80, 90, 100, 120, 140, 160];
var allSvgs = new StringBuilder();

foreach (double angle in angles)
{
    var layout = new AirportGroundLayout { AirportId = "TEST" };
    int nextId = 0;

    // Three runway nodes: left — center (intersection) — right
    double cLat = 37.73,
        cLon = -122.22;
    double rwyLen = 0.03; // nm each side

    var (lLat, lLon) = GeoMath.ProjectPoint(cLat, cLon, new TrueHeading(270), rwyLen);
    var (rLat, rLon) = GeoMath.ProjectPoint(cLat, cLon, new TrueHeading(90), rwyLen);

    var nodeL = new GroundNode
    {
        Id = nextId++,
        Latitude = lLat,
        Longitude = lLon,
        Type = GroundNodeType.TaxiwayIntersection,
    };
    var nodeC = new GroundNode
    {
        Id = nextId++,
        Latitude = cLat,
        Longitude = cLon,
        Type = GroundNodeType.TaxiwayIntersection,
    };
    var nodeR = new GroundNode
    {
        Id = nextId++,
        Latitude = rLat,
        Longitude = rLon,
        Type = GroundNodeType.TaxiwayIntersection,
    };
    layout.Nodes[nodeL.Id] = nodeL;
    layout.Nodes[nodeC.Id] = nodeC;
    layout.Nodes[nodeR.Id] = nodeR;

    layout.Edges.Add(
        new GroundEdge
        {
            Nodes = [nodeL, nodeC],
            TaxiwayName = "RWY10/28",
            DistanceNm = GeoMath.DistanceNm(lLat, lLon, cLat, cLon),
        }
    );
    layout.Edges.Add(
        new GroundEdge
        {
            Nodes = [nodeC, nodeR],
            TaxiwayName = "RWY10/28",
            DistanceNm = GeoMath.DistanceNm(cLat, cLon, rLat, rLon),
        }
    );

    // Branch taxiway going south at the given angle, 0.03nm long
    double branchBearing = 90 + angle; // relative to runway heading 90°
    var (bLat, bLon) = GeoMath.ProjectPoint(cLat, cLon, new TrueHeading(branchBearing), 0.03);
    var nodeB = new GroundNode
    {
        Id = nextId++,
        Latitude = bLat,
        Longitude = bLon,
        Type = GroundNodeType.RunwayHoldShort,
        RunwayId = RunwayIdentifier.Parse("10/28"),
    };
    layout.Nodes[nodeB.Id] = nodeB;

    layout.Edges.Add(
        new GroundEdge
        {
            Nodes = [nodeC, nodeB],
            TaxiwayName = $"T{angle:F0}",
            DistanceNm = GeoMath.DistanceNm(cLat, cLon, bLat, bLon),
        }
    );

    layout.RebuildAdjacencyLists();
    FilletArcGenerator.Apply(layout);

    // --- Render SVG ---
    var allPos = layout.Nodes.Values.Select(n => (n.Latitude, n.Longitude)).ToList();
    foreach (var arc in layout.Arcs)
    {
        var bezier = arc.ToBezier();
        for (int s = 0; s <= 10; s++)
        {
            var (lat, lon) = bezier.Evaluate((double)s / 10);
            allPos.Add((lat, lon));
        }
    }

    double minLat = allPos.Min(p => p.Latitude) - 0.001;
    double maxLat = allPos.Max(p => p.Latitude) + 0.001;
    double minLon = allPos.Min(p => p.Longitude) - 0.001;
    double maxLon = allPos.Max(p => p.Longitude) + 0.001;

    // Maintain aspect ratio
    double cosLat = Math.Cos(cLat * Math.PI / 180);
    double spanLat = maxLat - minLat;
    double spanLon = (maxLon - minLon) * cosLat;
    if (spanLon > spanLat)
    {
        double pad = (spanLon / cosLat - spanLat) / 2;
        minLat -= pad;
        maxLat += pad;
    }
    else
    {
        double pad = (spanLat * cosLat - spanLon) / cosLat / 2;
        minLon -= pad;
        maxLon += pad;
    }

    int sz = 300;
    double ScaleX(double lon) => (lon - minLon) / (maxLon - minLon) * sz;
    double ScaleY(double lat) => sz - ((lat - minLat) / (maxLat - minLat) * sz);
    string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    var sb = new StringBuilder();
    sb.AppendLine(F($"<svg width=\"{sz}\" height=\"{sz}\" xmlns=\"http://www.w3.org/2000/svg\" style=\"background: #222;\">\n"));

    // Title
    sb.AppendLine(
        F($"<text x=\"{sz / 2}\" y=\"18\" fill=\"#ccc\" font-size=\"14\" text-anchor=\"middle\" font-family=\"Consolas\">{angle}° exit</text>")
    );

    // Straight edges
    foreach (var edge in layout.Edges)
    {
        double x1 = ScaleX(edge.Nodes[0].Longitude),
            y1 = ScaleY(edge.Nodes[0].Latitude);
        double x2 = ScaleX(edge.Nodes[1].Longitude),
            y2 = ScaleY(edge.Nodes[1].Latitude);
        string color = edge.IsRunwayCenterline ? "#ff6666" : "#4488cc";
        sb.AppendLine(F($"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"{color}\" stroke-width=\"2\" />"));
    }

    // Arcs (bezier curves)
    foreach (var arc in layout.Arcs)
    {
        var bezier = arc.ToBezier();
        var pts = new List<(double X, double Y)>();
        for (int s = 0; s <= 24; s++)
        {
            double t = (double)s / 24;
            var (lat, lon) = bezier.Evaluate(t);
            pts.Add((ScaleX(lon), ScaleY(lat)));
        }
        for (int s = 0; s < pts.Count - 1; s++)
        {
            sb.AppendLine(
                F($"<line x1=\"{pts[s].X}\" y1=\"{pts[s].Y}\" x2=\"{pts[s + 1].X}\" y2=\"{pts[s + 1].Y}\" stroke=\"#44ff44\" stroke-width=\"2.5\" />")
            );
        }

        // Arc info
        var mid = pts[pts.Count / 2];
        sb.AppendLine(
            F(
                $"<text x=\"{mid.X}\" y=\"{mid.Y - 5}\" fill=\"#44ff44\" font-size=\"9\" text-anchor=\"middle\" font-family=\"Consolas\">MinR={arc.MinRadiusOfCurvatureFt:F0}</text>"
            )
        );
    }

    // Nodes
    foreach (var node in layout.Nodes.Values)
    {
        double x = ScaleX(node.Longitude),
            y = ScaleY(node.Latitude);
        string fill = node.Type == GroundNodeType.RunwayHoldShort ? "#ffaa00" : "#ccc";
        sb.AppendLine(F($"<circle cx=\"{x}\" cy=\"{y}\" r=\"3\" fill=\"{fill}\" />"));
    }

    sb.AppendLine("</svg>");
    allSvgs.Append(sb);
}

string outPath = "X:/dev/yaat/.tmp/fillet-angle-test.html";
File.WriteAllText(
    outPath,
    $"""
<!DOCTYPE html>
<html>
<head><title>Fillet Arc Angle Test</title></head>
<body style="background: #111; color: #ccc; font-family: Consolas; padding: 20px;">
<h3>Fillet arcs at different exit angles</h3>
<p>Red = runway, Blue = taxiway, Green = fillet arcs, Orange = hold-short</p>
<p>Each diagram shows a single intersection with the taxiway exiting at the labeled angle.</p>
<div style="display: flex; flex-wrap: wrap; gap: 10px;">
{allSvgs}
</div>
</body>
</html>
"""
);
Console.WriteLine($"Written to {outPath}");
