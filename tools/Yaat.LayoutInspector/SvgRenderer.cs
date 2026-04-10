using System.Globalization;
using System.Text;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector;

/// <summary>
/// Renders the airport ground layout as a high-resolution SVG showing all nodes,
/// edges, arcs, and runways. Supports highlighting specific taxiways, nodes, or
/// runways with labels. Supports a route overlay to trace a specific path.
/// </summary>
public sealed class SvgRenderer
{
    private readonly AirportGroundLayout _layout;

    // Coordinate transform state
    private double _minLat,
        _maxLat,
        _minLon,
        _maxLon;
    private int _width,
        _height;
    private const int Margin = 40;

    // Highlight sets
    private readonly HashSet<string> _highlightTaxiways = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _highlightNodes = [];
    private readonly HashSet<string> _highlightRunways = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _nodeAnnotations = [];

    // Route overlay: ordered list of node IDs to draw as a thick colored path
    private readonly List<int> _routeNodeIds = [];

    public SvgRenderer(AirportGroundLayout layout)
    {
        _layout = layout;
    }

    public void HighlightTaxiway(string name) => _highlightTaxiways.Add(name);

    public void HighlightNode(int id) => _highlightNodes.Add(id);

    public void HighlightRunway(string designator) => _highlightRunways.Add(designator);

    public void AnnotateNode(int id, string text) => _nodeAnnotations[id] = text;

    public void AddRouteNode(int id) => _routeNodeIds.Add(id);

    private bool HasHighlights => _highlightTaxiways.Count > 0 || _highlightRunways.Count > 0 || _highlightNodes.Count > 0;

    public string Render(int width, int height)
    {
        _width = width;
        _height = height;
        ComputeBounds();

        // Collect route node IDs into a set for fast lookup
        var routeNodeSet = new HashSet<int>(_routeNodeIds);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {_width} {_height}\" font-family=\"Consolas, monospace\" font-size=\"9\">"
        );
        sb.AppendLine($"<rect width=\"{_width}\" height=\"{_height}\" fill=\"#0d1117\"/>");

        // Title
        sb.AppendLine(
            $"<text x=\"{_width / 2}\" y=\"22\" text-anchor=\"middle\" fill=\"#c9d1d9\" font-size=\"14\" font-weight=\"bold\">{_layout.AirportId} Ground Layout</text>"
        );

        // Layer 1: Runway surfaces
        foreach (var rwy in _layout.Runways)
        {
            RenderRunwaySurface(sb, rwy);
        }

        // Layer 2: Non-highlighted edges (very dim)
        foreach (var edge in _layout.Edges)
        {
            if (!IsHighlightedEdge(edge))
            {
                RenderEdge(sb, edge, highlighted: false);
            }
        }

        foreach (var arc in _layout.Arcs)
        {
            if (!IsHighlightedEdge(arc))
            {
                RenderArc(sb, arc, highlighted: false);
            }
        }

        // Layer 3: Highlighted edges
        foreach (var edge in _layout.Edges)
        {
            if (IsHighlightedEdge(edge))
            {
                RenderEdge(sb, edge, highlighted: true);
            }
        }

        foreach (var arc in _layout.Arcs)
        {
            if (IsHighlightedEdge(arc))
            {
                RenderArc(sb, arc, highlighted: true);
            }
        }

        // Layer 4: Route overlay (thick bright line on top of everything)
        if (_routeNodeIds.Count >= 2)
        {
            RenderRoute(sb);
        }

        // Layer 5: Nodes — only render relevant ones
        foreach (var node in _layout.Nodes.Values)
        {
            RenderNode(sb, node, routeNodeSet);
        }

        // Layer 6: Annotations (topmost)
        foreach (var (nodeId, text) in _nodeAnnotations)
        {
            if (_layout.Nodes.TryGetValue(nodeId, out var node))
            {
                RenderAnnotation(sb, node, text);
            }
        }

        RenderLegend(sb);
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private void ComputeBounds()
    {
        _minLat = double.MaxValue;
        _maxLat = double.MinValue;
        _minLon = double.MaxValue;
        _maxLon = double.MinValue;

        foreach (var node in _layout.Nodes.Values)
        {
            _minLat = Math.Min(_minLat, node.Latitude);
            _maxLat = Math.Max(_maxLat, node.Latitude);
            _minLon = Math.Min(_minLon, node.Longitude);
            _maxLon = Math.Max(_maxLon, node.Longitude);
        }

        double padLat = (_maxLat - _minLat) * 0.05 + 0.0001;
        double padLon = (_maxLon - _minLon) * 0.05 + 0.0001;
        _minLat -= padLat;
        _maxLat += padLat;
        _minLon -= padLon;
        _maxLon += padLon;
    }

    private (double X, double Y) ToSvg(double lat, double lon)
    {
        double x = Margin + (lon - _minLon) / (_maxLon - _minLon) * (_width - 2 * Margin);
        double y = Margin + (1.0 - (lat - _minLat) / (_maxLat - _minLat)) * (_height - 2 * Margin);
        return (x, y);
    }

    private bool IsHighlightedEdge(IGroundEdge edge)
    {
        if (!HasHighlights)
        {
            return false;
        }

        foreach (string twy in _highlightTaxiways)
        {
            if (edge.MatchesTaxiway(twy))
            {
                return true;
            }
        }

        foreach (string rwy in _highlightRunways)
        {
            if (edge.MatchesRunway(rwy))
            {
                return true;
            }
        }

        if (_highlightNodes.Contains(edge.Nodes[0].Id) || _highlightNodes.Contains(edge.Nodes[1].Id))
        {
            return true;
        }

        return false;
    }

    // --- Runway surface ---

    private void RenderRunwaySurface(StringBuilder sb, GroundRunway rwy)
    {
        if (rwy.Coordinates.Count < 2)
        {
            return;
        }

        var (x1, y1) = ToSvg(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon);
        var (x2, y2) = ToSvg(rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);

        double rwyLengthPx = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        double rwyLengthNm = GeoMath.DistanceNm(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon, rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);
        double pxPerNm = rwyLengthPx / Math.Max(rwyLengthNm, 0.01);
        double widthPx = (rwy.WidthFt / GeoMath.FeetPerNm) * pxPerNm;

        bool highlighted = false;
        var rwyId = RunwayIdentifier.Parse(rwy.Name);
        foreach (string h in _highlightRunways)
        {
            if (rwyId.Contains(h))
            {
                highlighted = true;
            }
        }

        string color = highlighted ? "#2d333b" : "#1c2128";
        sb.AppendLine(
            $"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{color}\" stroke-width=\"{F(widthPx)}\" stroke-linecap=\"round\"/>"
        );

        string clColor = highlighted ? "#444c56" : "#2d333b";
        sb.AppendLine(
            $"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{clColor}\" stroke-width=\"1.5\" stroke-dasharray=\"12,8\"/>"
        );

        double mx = (x1 + x2) / 2;
        double my = (y1 + y2) / 2;
        double angle = Math.Atan2(y1 - y2, x2 - x1) * 180.0 / Math.PI;
        if (angle > 90)
        {
            angle -= 180;
        }
        if (angle < -90)
        {
            angle += 180;
        }

        sb.AppendLine(
            $"<text x=\"{F(mx)}\" y=\"{F(my - widthPx / 2 - 4)}\" text-anchor=\"middle\" fill=\"#484f58\" font-size=\"11\" transform=\"rotate({F(angle)},{F(mx)},{F(my - widthPx / 2 - 4)})\">{rwy.Name}</text>"
        );
    }

    // --- Edges ---

    private void RenderEdge(StringBuilder sb, GroundEdge edge, bool highlighted)
    {
        var (x1, y1) = ToSvg(edge.Nodes[0].Latitude, edge.Nodes[0].Longitude);
        var (x2, y2) = ToSvg(edge.Nodes[1].Latitude, edge.Nodes[1].Longitude);

        string color = EdgeColor(edge, highlighted);
        double width = highlighted ? 2.0 : 0.7;
        double opacity = highlighted ? 0.85 : 0.2;
        string dash = edge.IsRunwayCenterline ? "stroke-dasharray=\"6,4\"" : "";

        sb.AppendLine(
            $"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{color}\" stroke-width=\"{F(width)}\" {dash} opacity=\"{F(opacity)}\"/>"
        );

        // Only label highlighted edges that are long enough to read
        if (highlighted)
        {
            double lenPx = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (lenPx > 40)
            {
                double mx = (x1 + x2) / 2;
                double my = (y1 + y2) / 2;
                double distFt = edge.DistanceNm * GeoMath.FeetPerNm;
                string label = $"{edge.TaxiwayName} ({distFt:F0}ft)";
                double angle = Math.Atan2(y2 - y1, x2 - x1) * 180.0 / Math.PI;
                if (angle > 90)
                {
                    angle -= 180;
                }
                if (angle < -90)
                {
                    angle += 180;
                }
                sb.AppendLine(
                    $"<text x=\"{F(mx)}\" y=\"{F(my - 3)}\" text-anchor=\"middle\" fill=\"{color}\" font-size=\"7\" opacity=\"0.8\" transform=\"rotate({F(angle)},{F(mx)},{F(my - 3)})\">{label}</text>"
                );
            }
        }
    }

    // --- Arcs ---

    private void RenderArc(StringBuilder sb, GroundArc arc, bool highlighted)
    {
        var bezier = arc.ToBezier();
        var points = new List<(double X, double Y)>();
        const int samples = 16;
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            var (lat, lon) = bezier.Evaluate(t);
            points.Add(ToSvg(lat, lon));
        }

        string color = ArcColor(arc, highlighted);
        double width = highlighted ? 2.0 : 0.7;
        double opacity = highlighted ? 0.85 : 0.2;

        var pathD = new StringBuilder();
        pathD.Append($"M {F(points[0].X)},{F(points[0].Y)}");
        for (int i = 1; i < points.Count; i++)
        {
            pathD.Append($" L {F(points[i].X)},{F(points[i].Y)}");
        }

        sb.AppendLine(
            $"<path d=\"{pathD}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(width)}\" stroke-dasharray=\"4,3\" opacity=\"{F(opacity)}\"/>"
        );

        // Label at midpoint for highlighted arcs only, if long enough
        if (highlighted)
        {
            double totalLen = 0;
            for (int i = 1; i < points.Count; i++)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                totalLen += Math.Sqrt(dx * dx + dy * dy);
            }

            if (totalLen > 50)
            {
                int mid = points.Count / 2;
                double mx = points[mid].X;
                double my = points[mid].Y;
                double distFt = arc.DistanceNm * GeoMath.FeetPerNm;
                sb.AppendLine(
                    $"<text x=\"{F(mx)}\" y=\"{F(my - 4)}\" text-anchor=\"middle\" fill=\"{color}\" font-size=\"7\" opacity=\"0.8\">{arc.TaxiwayName} ({distFt:F0}ft)</text>"
                );
            }
        }
    }

    // --- Route overlay ---

    private void RenderRoute(StringBuilder sb)
    {
        // Draw the route as a thick bright polyline connecting route nodes in order.
        // For arc segments, sample the bezier. For straight segments, draw line.
        var pathD = new StringBuilder();
        bool first = true;

        for (int i = 0; i < _routeNodeIds.Count - 1; i++)
        {
            int fromId = _routeNodeIds[i];
            int toId = _routeNodeIds[i + 1];

            if (!_layout.Nodes.TryGetValue(fromId, out var fromNode) || !_layout.Nodes.TryGetValue(toId, out var toNode))
            {
                continue;
            }

            // Find the connecting edge or arc
            GroundArc? connectingArc = null;
            foreach (var arc in _layout.Arcs)
            {
                if ((arc.Nodes[0].Id == fromId && arc.Nodes[1].Id == toId) || (arc.Nodes[0].Id == toId && arc.Nodes[1].Id == fromId))
                {
                    connectingArc = arc;
                    break;
                }
            }

            if (connectingArc is not null)
            {
                // Sample bezier
                var bezier = connectingArc.ToBezier();
                bool reversed = connectingArc.Nodes[0].Id != fromId;
                const int samples = 16;
                for (int s = 0; s <= samples; s++)
                {
                    double t = reversed ? 1.0 - (double)s / samples : (double)s / samples;
                    var (lat, lon) = bezier.Evaluate(t);
                    var (x, y) = ToSvg(lat, lon);
                    pathD.Append(first ? $"M {F(x)},{F(y)}" : $" L {F(x)},{F(y)}");
                    first = false;
                }
            }
            else
            {
                // Straight line
                var (x1, y1) = ToSvg(fromNode.Latitude, fromNode.Longitude);
                if (first)
                {
                    pathD.Append($"M {F(x1)},{F(y1)}");
                    first = false;
                }

                var (x2, y2) = ToSvg(toNode.Latitude, toNode.Longitude);
                pathD.Append($" L {F(x2)},{F(y2)}");
            }
        }

        if (pathD.Length > 0)
        {
            // Glow behind
            sb.AppendLine(
                $"<path d=\"{pathD}\" fill=\"none\" stroke=\"#ff6a00\" stroke-width=\"6\" opacity=\"0.25\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>"
            );
            // Main route line
            sb.AppendLine(
                $"<path d=\"{pathD}\" fill=\"none\" stroke=\"#ff6a00\" stroke-width=\"2.5\" opacity=\"0.9\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>"
            );
        }

        // Draw route node markers (numbered circles)
        for (int i = 0; i < _routeNodeIds.Count; i++)
        {
            if (!_layout.Nodes.TryGetValue(_routeNodeIds[i], out var node))
            {
                continue;
            }

            var (x, y) = ToSvg(node.Latitude, node.Longitude);
            sb.AppendLine($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"6\" fill=\"#ff6a00\" stroke=\"#0d1117\" stroke-width=\"1.5\"/>");
            sb.AppendLine(
                $"<text x=\"{F(x)}\" y=\"{F(y + 3)}\" text-anchor=\"middle\" fill=\"#0d1117\" font-size=\"7\" font-weight=\"bold\">{i}</text>"
            );
        }
    }

    // --- Nodes ---

    private void RenderNode(StringBuilder sb, GroundNode node, HashSet<int> routeNodeSet)
    {
        var (x, y) = ToSvg(node.Latitude, node.Longitude);
        bool highlighted = _highlightNodes.Contains(node.Id);
        bool onRoute = routeNodeSet.Contains(node.Id);
        bool isImportant = node.Type is GroundNodeType.RunwayHoldShort or GroundNodeType.Spot or GroundNodeType.Parking;

        // Skip rendering plain intersection nodes that aren't highlighted/annotated/on-route
        // unless they're on a highlighted taxiway
        bool onHighlightedTaxiway = false;
        foreach (string twy in _highlightTaxiways)
        {
            if (node.Edges.Any(e => e.MatchesTaxiway(twy)))
            {
                onHighlightedTaxiway = true;
                break;
            }
        }

        bool render = highlighted || onRoute || isImportant || onHighlightedTaxiway || _nodeAnnotations.ContainsKey(node.Id);
        if (!render && HasHighlights)
        {
            return; // Skip unrelated intersection nodes entirely
        }

        string color = onRoute ? "#ff6a00" : NodeColor(node);
        double r = NodeRadius(node, highlighted || onRoute);
        string fill = isImportant ? color : "none";
        double strokeWidth = (highlighted || onRoute) ? 2.0 : 0.8;
        double opacity = (highlighted || onRoute || isImportant) ? 1.0 : 0.3;

        // Route nodes are already rendered by RenderRoute — skip circle but keep label
        if (!onRoute)
        {
            sb.AppendLine(
                $"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"{F(r)}\" fill=\"{fill}\" stroke=\"{color}\" stroke-width=\"{F(strokeWidth)}\" opacity=\"{F(opacity)}\"/>"
            );
        }

        // Label: only show for highlighted, route, annotated, or important nodes
        bool showLabel = highlighted || onRoute || isImportant || _nodeAnnotations.ContainsKey(node.Id);
        if (showLabel)
        {
            string label = $"#{node.Id}";
            if (node.Name is not null)
            {
                label += $" {node.Name}";
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rId)
            {
                label += $" HS({rId})";
            }

            sb.AppendLine($"<text x=\"{F(x + r + 3)}\" y=\"{F(y + 3)}\" fill=\"{color}\" font-size=\"8\" opacity=\"0.9\">{label}</text>");
        }
    }

    // --- Annotations ---

    private void RenderAnnotation(StringBuilder sb, GroundNode node, string text)
    {
        var (x, y) = ToSvg(node.Latitude, node.Longitude);

        double textWidth = text.Length * 5.5 + 12;
        double boxY = y - 32;
        sb.AppendLine(
            $"<rect x=\"{F(x - textWidth / 2)}\" y=\"{F(boxY)}\" width=\"{F(textWidth)}\" height=\"18\" fill=\"#161b22\" stroke=\"#f0883e\" stroke-width=\"1\" rx=\"3\" opacity=\"0.95\"/>"
        );
        sb.AppendLine(
            $"<text x=\"{F(x)}\" y=\"{F(boxY + 13)}\" text-anchor=\"middle\" fill=\"#f0883e\" font-size=\"9\" font-weight=\"bold\">{Esc(text)}</text>"
        );
        sb.AppendLine(
            $"<line x1=\"{F(x)}\" y1=\"{F(boxY + 18)}\" x2=\"{F(x)}\" y2=\"{F(y - 6)}\" stroke=\"#f0883e\" stroke-width=\"1\" opacity=\"0.7\"/>"
        );
    }

    // --- Legend ---

    private void RenderLegend(StringBuilder sb)
    {
        int lx = 12;
        int ly = _height - 140;
        sb.AppendLine($"<rect x=\"{lx}\" y=\"{ly}\" width=\"210\" height=\"130\" fill=\"#0d1117\" stroke=\"#30363d\" rx=\"4\" opacity=\"0.95\"/>");
        sb.AppendLine($"<text x=\"{lx + 10}\" y=\"{ly + 16}\" fill=\"#c9d1d9\" font-size=\"10\" font-weight=\"bold\">Legend</text>");

        (string Color, string Label)[] items =
        [
            ("#58a6ff", "RWY centerline edge"),
            ("#3fb950", "Taxiway edge"),
            ("#d2a8ff", "Fillet arc (dashed)"),
            ("#8b949e", "Ramp edge"),
            ("#f85149", "Hold-short node"),
            ("#3fb950", "Spot node"),
            ("#58a6ff", "Parking node"),
            ("#d29922", "Taxiway intersection"),
            ("#ff6a00", "Route overlay"),
        ];

        for (int i = 0; i < items.Length; i++)
        {
            sb.AppendLine($"<text x=\"{lx + 20}\" y=\"{ly + 32 + i * 11}\" fill=\"{items[i].Color}\" font-size=\"8\">{items[i].Label}</text>");
        }
    }

    // --- Colors ---

    private static string EdgeColor(GroundEdge edge, bool highlighted)
    {
        if (edge.IsRunwayCenterline)
        {
            return highlighted ? "#79c0ff" : "#58a6ff";
        }
        if (edge.IsRamp)
        {
            return "#8b949e";
        }
        return highlighted ? "#56d364" : "#3fb950";
    }

    private static string ArcColor(GroundArc arc, bool highlighted)
    {
        if (arc.IsRunwayJunction)
        {
            return highlighted ? "#bc8cff" : "#8957e5";
        }
        return highlighted ? "#d2a8ff" : "#8957e5";
    }

    private static string NodeColor(GroundNode node) =>
        node.Type switch
        {
            GroundNodeType.RunwayHoldShort => "#f85149",
            GroundNodeType.Spot => "#3fb950",
            GroundNodeType.Parking => "#58a6ff",
            GroundNodeType.Helipad => "#a371f7",
            _ => "#d29922",
        };

    private static double NodeRadius(GroundNode node, bool highlighted) =>
        node.Type switch
        {
            GroundNodeType.RunwayHoldShort => highlighted ? 7 : 4,
            GroundNodeType.Spot => highlighted ? 6 : 3,
            GroundNodeType.Parking => highlighted ? 6 : 3,
            _ => highlighted ? 5 : 2,
        };

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
