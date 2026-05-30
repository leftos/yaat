using System.Text;
using System.Text.Json;
using Yaat.LayoutInspector.Tick;
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
    private TickRecording? _tickRecording;

    public HtmlRenderer(AirportGroundLayout layout) => _layout = layout;

    public void HighlightTaxiway(string name) => _highlightTaxiways.Add(name);

    public void HighlightNode(int id) => _highlightNodes.Add(id);

    public void HighlightRunway(string designator) => _highlightRunways.Add(designator);

    public void AnnotateNode(int id, string text) => _nodeAnnotations[id] = text;

    public void AddRouteNode(int id) => _routeNodeIds.Add(id);

    /// <summary>
    /// Embed the TickRecording (aircraft metadata + per-tick events) into the
    /// rendered HTML. The template renders one trail + silhouette per aircraft,
    /// using each aircraft's wingspan/length from the recording for 1:1 scale.
    /// </summary>
    public void SetTickRecording(TickRecording recording) => _tickRecording = recording;

    public string Render()
    {
        var data = BuildDataJson();
        string html = LoadAsset("inspector-template.html");
        string css = LoadAsset("inspector.css");
        string js = LoadAsset("inspector.js");
        return html.Replace("/*__CSS__*/", css).Replace("/*__JS__*/", js).Replace("/*__DATA__*/", data);
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
            writer.WriteNumber("lat", node.Position.Lat);
            writer.WriteNumber("lon", node.Position.Lon);
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
            writer.WriteString("origin", e.Origin ?? "");
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
            writer.WriteString("origin", arc.Origin ?? "");
            writer.WriteNumber("radius", Math.Round(arc.MinRadiusOfCurvatureFt, 1));
            writer.WriteNumber("maxSafe", Math.Round(arc.MaxSafeSpeedKts(AircraftCategory.Jet), 1));
            writer.WriteNumber("turnAngle", Math.Round(arc.TurnAngleDeg, 1));
            writer.WriteStartArray("names");
            foreach (string name in arc.TaxiwayNames)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteNumber("bearing0", Math.Round(arc.EdgeBearingAtNode0Deg, 1));
            writer.WriteNumber("bearing1", Math.Round(arc.EdgeBearingAtNode1Deg, 1));

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

        if (_tickRecording is not null)
        {
            writer.WriteStartArray("aircraft");
            foreach (var meta in _tickRecording.Aircraft)
            {
                writer.WriteStartObject();
                writer.WriteString("callsign", meta.Callsign);
                writer.WriteString("type", meta.Type);
                writer.WriteString("color", meta.Color);
                if (meta.WingspanFt is { } w)
                {
                    writer.WriteNumber("wingspanFt", w);
                }
                if (meta.LengthFt is { } l)
                {
                    writer.WriteNumber("lengthFt", l);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("ticks");
            foreach (var tick in _tickRecording.Ticks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("t", tick.T);
                writer.WriteString("callsign", tick.Callsign);
                writer.WriteNumber("lat", Math.Round(tick.Lat, 8));
                writer.WriteNumber("lon", Math.Round(tick.Lon, 8));
                writer.WriteNumber("hdg", Math.Round(tick.Hdg, 2));
                writer.WriteNumber("gs", Math.Round(tick.Gs, 2));
                writer.WriteString("phase", tick.Phase);
                if (tick.Status is { } status)
                {
                    writer.WriteString("status", status);
                }
                if (tick.Twy is { } twy)
                {
                    writer.WriteString("twy", twy);
                }
                if (tick.SpeedLimit is { } sl)
                {
                    writer.WriteNumber("speedLimit", Math.Round(sl, 1));
                }
                if (tick.Nav is { } nav)
                {
                    writer.WritePropertyName("nav");
                    writer.WriteStartObject();
                    writer.WriteNumber("targetNodeId", nav.TargetNodeId);
                    writer.WriteNumber("distNm", Math.Round(nav.DistNm, 4));
                    writer.WriteNumber("brgDeg", Math.Round(nav.BrgDeg, 1));
                    writer.WriteNumber("targetSpdKts", Math.Round(nav.TargetSpdKts, 1));
                    if (nav.BrakeLimitKts < 1e10)
                    {
                        writer.WriteNumber("brakeLimitKts", Math.Round(nav.BrakeLimitKts, 1));
                    }
                    if (nav.ArcLimitKts < 1e10)
                    {
                        writer.WriteNumber("arcLimitKts", Math.Round(nav.ArcLimitKts, 1));
                    }
                    if (nav.OnArc)
                    {
                        writer.WriteBoolean("onArc", true);
                    }
                    writer.WriteNumber("nodeReqSpdKts", Math.Round(nav.NodeReqSpdKts, 1));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
        return sb.ToString();
    }

    private bool IsHighlighted(IGroundEdge edge)
    {
        // If nothing is highlighted, everything is
        if ((_highlightTaxiways.Count == 0) && (_highlightRunways.Count == 0) && (_highlightNodes.Count == 0))
        {
            return true;
        }

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

    /// <summary>
    /// Load a companion render asset (template HTML, CSS, or JS). Tries the
    /// embedded resource first, then a sibling file, then walks up the source
    /// tree — so the renderer works whether LI runs from the published binary,
    /// from <c>dotnet run</c>, or in development tooling.
    /// </summary>
    private static string LoadAsset(string fileName)
    {
        var asm = typeof(HtmlRenderer).Assembly;
        using (var stream = asm.GetManifestResourceStream("Yaat.LayoutInspector." + fileName))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        string dir = Path.GetDirectoryName(asm.Location) ?? ".";
        string sibling = Path.Combine(dir, fileName);
        if (File.Exists(sibling))
        {
            return File.ReadAllText(sibling);
        }

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            string candidate = Path.Combine(d.FullName, "tools", "Yaat.LayoutInspector", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            d = d.Parent;
        }

        throw new FileNotFoundException(fileName + " not found");
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
