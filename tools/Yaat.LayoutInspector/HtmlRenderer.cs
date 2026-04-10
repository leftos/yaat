using System.Text;
using System.Text.Json;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector;

/// <summary>
/// Renders the airport ground layout as an interactive HTML page with canvas
/// rendering, pan/zoom, and hover tooltips. Exports the layout as JSON embedded
/// in the page; all rendering happens client-side in JavaScript.
/// </summary>
public sealed class HtmlRenderer
{
    private readonly AirportGroundLayout _layout;
    private readonly HashSet<string> _highlightTaxiways = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _highlightNodes = [];
    private readonly HashSet<string> _highlightRunways = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _nodeAnnotations = [];
    private readonly List<int> _routeNodeIds = [];

    public HtmlRenderer(AirportGroundLayout layout) => _layout = layout;

    public void HighlightTaxiway(string name) => _highlightTaxiways.Add(name);

    public void HighlightNode(int id) => _highlightNodes.Add(id);

    public void HighlightRunway(string designator) => _highlightRunways.Add(designator);

    public void AnnotateNode(int id, string text) => _nodeAnnotations[id] = text;

    public void AddRouteNode(int id) => _routeNodeIds.Add(id);

    public string Render()
    {
        var data = BuildDataJson();
        string html = GetTemplate();
        return html.Replace("/*__DATA__*/", data);
    }

    private string BuildDataJson()
    {
        var sb = new StringBuilder();
        using var writer = new Utf8JsonWriter(new WriterStream(sb), new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("airportId", _layout.AirportId);

        // Nodes
        writer.WriteStartArray("nodes");
        foreach (var node in _layout.Nodes.Values)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", node.Id);
            writer.WriteNumber("lat", node.Latitude);
            writer.WriteNumber("lon", node.Longitude);
            writer.WriteString("type", node.Type.ToString());
            if (node.Name is not null)
            {
                writer.WriteString("name", node.Name);
            }
            if (node.RunwayId is { } rid)
            {
                writer.WriteString("rwyId", rid.ToString());
            }
            writer.WriteBoolean("hl", _highlightNodes.Contains(node.Id));
            if (_nodeAnnotations.TryGetValue(node.Id, out var ann))
            {
                writer.WriteString("ann", ann);
            }

            writer.WriteStartArray("edges");
            foreach (var e in node.Edges)
            {
                writer.WriteStartObject();
                writer.WriteNumber("to", e.OtherNode(node).Id);
                writer.WriteString("twy", e.TaxiwayName);
                writer.WriteNumber("ft", Math.Round(e.DistanceNm * GeoMath.FeetPerNm));
                writer.WriteBoolean("arc", e is GroundArc);
                writer.WriteBoolean("rwy", e.IsRunwayCenterline);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Straight edges
        writer.WriteStartArray("edges");
        foreach (var e in _layout.Edges)
        {
            writer.WriteStartObject();
            writer.WriteNumber("a", e.Nodes[0].Id);
            writer.WriteNumber("b", e.Nodes[1].Id);
            writer.WriteString("twy", e.TaxiwayName);
            writer.WriteNumber("ft", Math.Round(e.DistanceNm * GeoMath.FeetPerNm));
            writer.WriteBoolean("rwy", e.IsRunwayCenterline);
            writer.WriteBoolean("ramp", e.IsRamp);
            writer.WriteBoolean("hl", IsHighlighted(e));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Arcs
        writer.WriteStartArray("arcs");
        foreach (var arc in _layout.Arcs)
        {
            writer.WriteStartObject();
            writer.WriteNumber("a", arc.Nodes[0].Id);
            writer.WriteNumber("b", arc.Nodes[1].Id);
            writer.WriteString("twy", arc.TaxiwayName);
            writer.WriteNumber("ft", Math.Round(arc.DistanceNm * GeoMath.FeetPerNm));
            writer.WriteBoolean("rwyJunction", arc.IsRunwayJunction);
            writer.WriteBoolean("hl", IsHighlighted(arc));

            var bez = arc.ToBezier();
            writer.WriteStartArray("pts");
            for (int i = 0; i <= 16; i++)
            {
                double t = (double)i / 16;
                var (lat, lon) = bez.Evaluate(t);
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(lat, 7));
                writer.WriteNumberValue(Math.Round(lon, 7));
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Runways
        writer.WriteStartArray("runways");
        foreach (var rwy in _layout.Runways)
        {
            writer.WriteStartObject();
            writer.WriteString("name", rwy.Name);
            writer.WriteNumber("widthFt", rwy.WidthFt);
            bool hl = false;
            var rid = RunwayIdentifier.Parse(rwy.Name);
            foreach (var h in _highlightRunways)
            {
                if (rid.Contains(h))
                {
                    hl = true;
                }
            }
            writer.WriteBoolean("hl", hl);

            writer.WriteStartArray("coords");
            foreach (var c in rwy.Coordinates)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(c.Lat, 7));
                writer.WriteNumberValue(Math.Round(c.Lon, 7));
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("hlTaxiways");
        foreach (var t in _highlightTaxiways)
        {
            writer.WriteStringValue(t);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("hlRunways");
        foreach (var r in _highlightRunways)
        {
            writer.WriteStringValue(r);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("route");
        foreach (int id in _routeNodeIds)
        {
            writer.WriteNumberValue(id);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
        return sb.ToString();
    }

    private bool IsHighlighted(IGroundEdge edge)
    {
        foreach (var t in _highlightTaxiways)
        {
            if (edge.MatchesTaxiway(t))
            {
                return true;
            }
        }
        foreach (var r in _highlightRunways)
        {
            if (edge.MatchesRunway(r))
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

    private static string GetTemplate()
    {
        // Template is in a companion file to avoid massive string literals
        var asm = typeof(HtmlRenderer).Assembly;
        using var stream = asm.GetManifestResourceStream("Yaat.LayoutInspector.inspector-template.html");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // Fallback: read from file next to the assembly
        string dir = Path.GetDirectoryName(asm.Location) ?? ".";
        string path = Path.Combine(dir, "inspector-template.html");
        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        // Last resort: find in source tree
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            string candidate = Path.Combine(d.FullName, "tools", "Yaat.LayoutInspector", "inspector-template.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            d = d.Parent;
        }

        throw new FileNotFoundException("inspector-template.html not found");
    }

    /// <summary>Adapter so Utf8JsonWriter can write to a StringBuilder.</summary>
    private sealed class WriterStream(StringBuilder sb) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => sb.Append(Encoding.UTF8.GetString(buffer, offset, count));

        public override void Flush() { }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value) { }
    }
}
