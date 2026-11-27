using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Imagery;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Client.Views.Authoring;

/// <summary>The kinds of airport-map feature the Airport Editor draws; any other feature is not drawn.</summary>
public enum AirportEditorFeatureKind
{
    /// <summary>A <c>runway</c> line.</summary>
    Runway,

    /// <summary>A <c>taxiway</c> line.</summary>
    Taxiway,

    /// <summary>A <c>parking</c> point.</summary>
    Parking,

    /// <summary>A <c>spot</c> point.</summary>
    Spot,

    /// <summary>A <c>helipad</c> point.</summary>
    Helipad,
}

/// <summary>One feature as the Airport Editor draws it: its index in the map, its kind, its positions and its label.</summary>
/// <param name="FeatureIndex">
/// The feature's index in <see cref="AirportMapDocument.Features"/>; a scene keeps only the features it draws, so this
/// is not the shape's own index in the scene.
/// </param>
/// <param name="Kind">What the feature is.</param>
/// <param name="Positions">The feature's positions in source order: one for a point, two or more for a line.</param>
/// <param name="Label">The feature's <c>name</c>, or null when it has none.</param>
public sealed record AirportEditorShape(int FeatureIndex, AirportEditorFeatureKind Kind, ImmutableArray<MapPosition> Positions, string? Label);

/// <summary>
/// The drawable part of an <see cref="AirportMapDocument"/>, built once per document on the UI thread and never changed,
/// so the render thread reads nothing the UI thread can change under it.
/// </summary>
/// <param name="Shapes">The features drawn, in source order.</param>
/// <param name="Bounds">The box around every feature's positions (drawn or not), or null when no feature has a position.</param>
public sealed record AirportEditorScene(ImmutableArray<AirportEditorShape> Shapes, AirportEditorBounds? Bounds)
{
    /// <summary>Gets the scene of no map.</summary>
    public static AirportEditorScene Empty { get; } = new([], null);

    /// <summary>Builds the scene of a map.</summary>
    /// <param name="document">The map, or null for none.</param>
    /// <returns>The map's scene.</returns>
    public static AirportEditorScene Build(AirportMapDocument? document)
    {
        if (document is null)
        {
            return Empty;
        }

        ImmutableArray<AirportEditorShape>.Builder shapes = ImmutableArray.CreateBuilder<AirportEditorShape>();
        for (int i = 0; i < document.Features.Count; i++)
        {
            MapFeature feature = document.Features[i];
            if (KindOf(feature) is { } kind)
            {
                shapes.Add(new AirportEditorShape(i, kind, feature.Coordinates, NameOf(feature)));
            }
        }

        return new AirportEditorScene(shapes.ToImmutable(), AirportEditorBounds.Of(document.Features.SelectMany(f => f.Coordinates)));
    }

    // A known type, matched exactly as GeoJsonParser matches it, on the geometry it needs (a line of two or more
    // positions, or a point); anything else is not drawn.
    private static AirportEditorFeatureKind? KindOf(MapFeature feature)
    {
        AirportEditorFeatureKind? kind = feature.FeatureType switch
        {
            "runway" => AirportEditorFeatureKind.Runway,
            "taxiway" => AirportEditorFeatureKind.Taxiway,
            "parking" => AirportEditorFeatureKind.Parking,
            "spot" => AirportEditorFeatureKind.Spot,
            "helipad" => AirportEditorFeatureKind.Helipad,
            _ => null,
        };
        bool isLine = kind is AirportEditorFeatureKind.Runway or AirportEditorFeatureKind.Taxiway;
        bool fits = isLine
            ? (feature.GeometryType == "LineString") && (feature.Coordinates.Length >= 2)
            : (feature.GeometryType == "Point") && (feature.Coordinates.Length >= 1);
        return fits ? kind : null;
    }

    /// <summary>
    /// The feature's name as GeoJsonParser.TryReadNameText reads it (private to Yaat.Sim, so mirrored): the key matched
    /// exactly, else case-insensitively; a string as written, a number as its JSON text, anything else none.
    /// </summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The name, or null when it has none or a blank one.</returns>
    internal static string? NameOf(MapFeature feature)
    {
        if (feature.CloneProperties() is not { } properties)
        {
            return null;
        }

        JsonNode? node = properties.TryGetPropertyValue("name", out JsonNode? exact)
            ? exact
            : properties.FirstOrDefault(p => string.Equals(p.Key, "name", StringComparison.OrdinalIgnoreCase)).Value;
        string? name = (node as JsonValue)?.GetValueKind() switch
        {
            JsonValueKind.String => node!.GetValue<string>(),
            JsonValueKind.Number => node!.ToJsonString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

/// <summary>A latitude/longitude box.</summary>
/// <param name="MinLat">The southern edge.</param>
/// <param name="MaxLat">The northern edge.</param>
/// <param name="MinLon">The western edge.</param>
/// <param name="MaxLon">The eastern edge.</param>
public sealed record AirportEditorBounds(double MinLat, double MaxLat, double MinLon, double MaxLon)
{
    /// <summary>The box around some positions.</summary>
    /// <param name="positions">The positions.</param>
    /// <returns>The box, or null when there are none.</returns>
    public static AirportEditorBounds? Of(IEnumerable<MapPosition> positions)
    {
        AirportEditorBounds? bounds = null;
        foreach (MapPosition p in positions)
        {
            bounds = bounds is null
                ? new AirportEditorBounds(p.Lat, p.Lat, p.Lon, p.Lon)
                : new AirportEditorBounds(
                    Math.Min(bounds.MinLat, p.Lat),
                    Math.Max(bounds.MaxLat, p.Lat),
                    Math.Min(bounds.MinLon, p.Lon),
                    Math.Max(bounds.MaxLon, p.Lon)
                );
        }

        return bounds;
    }
}

/// <summary>
/// What the render thread draws one Airport Editor frame from: the scene, the mode shown in the HUD, the selection,
/// what the pointer is over, and the tower-cab image drawn under it all.
/// </summary>
/// <param name="Scene">The open map's scene.</param>
/// <param name="ModeText">The canvas's mode, shown bottom-left.</param>
/// <param name="SelectedFeatureIndex">The selected feature's index in the map, or null when nothing is selected.</param>
/// <param name="SelectedVertexIndex">The selected vertex of the selected line, or null when none is.</param>
/// <param name="Hover">What the pointer is over, or null when nothing is.</param>
public sealed record AirportEditorRenderSnapshot(
    AirportEditorScene Scene,
    string ModeText,
    int? SelectedFeatureIndex,
    int? SelectedVertexIndex,
    AirportEditorHit? Hover
)
{
    /// <summary>Gets the open airport's tower-cab image, or null for none. Never disposed while a frame may draw it.</summary>
    public required TowerCabImage? Image { get; init; }

    /// <summary>Gets a value indicating whether the image is drawn.</summary>
    public required bool ShowImagery { get; init; }

    /// <summary>Gets the image's opacity, 0 to 100, as the Ground View applies its satellite image's.</summary>
    public required int ImageryBrightness { get; init; }

    /// <summary>Gets the USGS tiles drawn when the frame has no tower-cab image; never disposed while a frame may draw one.</summary>
    public ImmutableArray<ImageryTile> Tiles { get; init; } = [];

    /// <summary>Gets a value indicating whether the USGS credit line is drawn (a tile is drawn).</summary>
    public bool ShowCredit { get; init; }

    /// <summary>Gets the imagery status line drawn above the mode label, or null for none.</summary>
    public string? StatusLine { get; init; }

    /// <summary>
    /// Gets the snap target a drag in progress, or the cursor in a draw or place mode, snaps to, drawn as a crosshair, or
    /// null when it snaps to none.
    /// </summary>
    public MapPosition? SnapAt { get; init; }

    /// <summary>Gets the drawing in progress, drawn with a dashed ghost line to the cursor, or null when there is none.</summary>
    public AirportEditorDrawGhost? Ghost { get; init; }
}

/// <summary>A taxiway or runway drawing in progress, as one frame draws it.</summary>
/// <param name="Vertices">The drawing's unsaved vertices, in click order; never empty.</param>
/// <param name="Cursor">The snapped cursor the ghost line runs to from the last vertex, or null once the pointer has left.</param>
public sealed record AirportEditorDrawGhost(ImmutableArray<MapPosition> Vertices, MapPosition? Cursor);

/// <summary>
/// Draws the Airport Editor's map on its dark background, over the tower-cab image when the frame has one or else its USGS
/// tiles (with the USGS credit bottom-right): runways,
/// taxiways, parking, spots and helipads, then the hovered feature's and the selection's highlights and the selected
/// line's vertex handles, the drawing in progress with its dashed ghost line, and the snap crosshair, then the taxiway and
/// runway labels, then the HUD.
/// Owns its paints and fonts; dispose it once the canvas is done with it.
/// </summary>
public sealed class AirportEditorRenderer : IDisposable
{
    private const float PointRadius = 2.6f;
    private const float HudInset = 8f;
    private const float HudLineGap = 4f;
    private const float SelectedLineExtraWidth = 3f;
    private const float SelectedRingExtraRadius = 4f;
    private const float HoverExtraWidth = 2f;
    private const float HandleRadius = 4f;
    private const float ActiveHandleRadius = 6f;

    private const float CrosshairArm = 10f;
    private const float CrosshairRadius = 5f;

    /// <summary>The snap crosshair's colour, magenta.</summary>
    internal static readonly SKColor SnapColour = SKColor.Parse("#FF00FF");

    private static readonly SKColor Amber = SKColor.Parse("#FFC107");

    private readonly SKPaint _selectedLinePaint = LinePaint(Amber, 0f);
    private readonly SKPaint _selectedRingPaint = LinePaint(Amber, 2f);
    private readonly SKPaint _hoverLinePaint = LinePaint(SKColors.White.WithAlpha(102), 0f);
    private readonly SKPaint _hoverFillPaint = FillPaint(SKColors.White.WithAlpha(102));
    private readonly SKPaint _handlePaint = FillPaint(SKColors.White);
    private readonly SKPaint _selectedHandlePaint = FillPaint(Amber);
    private readonly SKPaint _handleOutlinePaint = LinePaint(SKColor.Parse("#1F1F1F"), 1f);
    private readonly SKPaint _snapPaint = LinePaint(SnapColour, 2f);
    private readonly SKPaint _ghostLinePaint = LinePaint(SKColors.White, 1.4f);
    private readonly SKPaint _ghostDashPaint = DashedPaint(SKColors.White, 1.4f);

    private readonly SKColor _background = SKColor.Parse(GroundColorScheme.DefaultBackground);

    private readonly SKPaint _runwayPaint = LinePaint(SKColor.Parse("#5fd0ff"), 2f);
    private readonly SKPaint _taxiwayPaint = LinePaint(SKColor.Parse("#ffc83c"), 1.4f);
    private readonly SKPaint _parkingPaint = FillPaint(new SKColor(60, 180, 80));
    private readonly SKPaint _spotPaint = FillPaint(new SKColor(220, 160, 40));
    private readonly SKPaint _taxiwayLabelPaint = FillPaint(SKColors.White);
    private readonly SKPaint _runwayLabelPaint = FillPaint(SKColor.Parse("#9fe4ff"));
    private readonly SKPaint _hudPaint = FillPaint(new SKColor(220, 220, 220));

    private readonly SKPaint _labelHaloPaint = new()
    {
        Color = SKColors.Black,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 3f,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true,
    };

    private readonly SKFont _taxiwayLabelFont = PlatformHelper.MonospaceFontBold(10);
    private readonly SKFont _runwayLabelFont = PlatformHelper.MonospaceFontBold(11);
    private readonly SKFont _hudFont = PlatformHelper.MonospaceFontBold(11);
    private readonly SKFont _creditFont = PlatformHelper.MonospaceFont(9);

    /// <summary>Draws one frame.</summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="viewport">The view, copied for this frame.</param>
    /// <param name="snapshot">What to draw.</param>
    public void Render(SKCanvas canvas, MapViewport viewport, AirportEditorRenderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(snapshot);
        canvas.Clear(_background);
        DrawImagery(canvas, viewport, snapshot);
        ImmutableArray<AirportEditorShape> shapes = snapshot.Scene.Shapes;
        DrawLines(canvas, viewport, shapes, AirportEditorFeatureKind.Runway, _runwayPaint);
        DrawLines(canvas, viewport, shapes, AirportEditorFeatureKind.Taxiway, _taxiwayPaint);
        foreach (AirportEditorShape shape in shapes)
        {
            if (!AirportEditorHitTest.IsLine(shape.Kind))
            {
                (float x, float y) = viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
                canvas.DrawCircle(x, y, PointRadius, NormalPaint(shape.Kind));
            }
        }

        DrawHover(canvas, viewport, snapshot);
        DrawSelection(canvas, viewport, snapshot);
        DrawGhost(canvas, viewport, snapshot.Ghost);
        DrawSnapCrosshair(canvas, viewport, snapshot.SnapAt);
        DrawLineLabels(canvas, viewport, shapes);
        DrawHud(canvas, viewport, snapshot);
    }

    // The tower-cab image when the frame has one, else the USGS tiles, at the imagery brightness.
    private static void DrawImagery(SKCanvas canvas, MapViewport viewport, AirportEditorRenderSnapshot snapshot)
    {
        if (!snapshot.ShowImagery)
        {
            return;
        }

        if (snapshot.Image is { } image)
        {
            GeoImageDrawing.Draw(canvas, viewport, image, snapshot.ImageryBrightness);
            return;
        }

        foreach (ImageryTile tile in snapshot.Tiles)
        {
            GeoImageDrawing.Draw(
                canvas,
                viewport,
                tile.Image,
                new TileBounds(tile.South, tile.West, tile.North, tile.East),
                snapshot.ImageryBrightness
            );
        }
    }

    // Bottom-left the imagery status line over the mode label; bottom-right the USGS credit while tiles show.
    private void DrawHud(SKCanvas canvas, MapViewport viewport, AirportEditorRenderSnapshot snapshot)
    {
        float baseline = viewport.PixelHeight - HudInset;
        DrawHaloText(canvas, snapshot.ModeText, new SKPoint(HudInset, baseline), SKTextAlign.Left, _hudFont, _hudPaint);
        if (snapshot.StatusLine is { } status)
        {
            DrawHaloText(canvas, status, new SKPoint(HudInset, baseline - _hudFont.Size - HudLineGap), SKTextAlign.Left, _hudFont, _hudPaint);
        }

        if (snapshot.ShowCredit)
        {
            var at = new SKPoint(viewport.PixelWidth - HudInset, baseline);
            DrawHaloText(canvas, UsgsImageryTileCache.Credit, at, SKTextAlign.Right, _creditFont, _hudPaint);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runwayPaint.Dispose();
        _taxiwayPaint.Dispose();
        _parkingPaint.Dispose();
        _spotPaint.Dispose();
        _taxiwayLabelPaint.Dispose();
        _runwayLabelPaint.Dispose();
        _hudPaint.Dispose();
        _selectedLinePaint.Dispose();
        _selectedRingPaint.Dispose();
        _hoverLinePaint.Dispose();
        _hoverFillPaint.Dispose();
        _handlePaint.Dispose();
        _selectedHandlePaint.Dispose();
        _handleOutlinePaint.Dispose();
        _snapPaint.Dispose();
        _ghostLinePaint.Dispose();
        _ghostDashPaint.PathEffect?.Dispose();
        _ghostDashPaint.Dispose();
        _labelHaloPaint.Dispose();
        _taxiwayLabelFont.Dispose();
        _runwayLabelFont.Dispose();
        _hudFont.Dispose();
        _creditFont.Dispose();
    }

    private static SKPaint LinePaint(SKColor color, float width) =>
        new()
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

    private static SKPaint DashedPaint(SKColor color, float width)
    {
        SKPaint paint = LinePaint(color, width);
        paint.PathEffect = SKPathEffect.CreateDash([6f, 4f], 0f);
        return paint;
    }

    private static SKPaint FillPaint(SKColor color) =>
        new()
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

    private static void DrawLines(
        SKCanvas canvas,
        MapViewport viewport,
        ImmutableArray<AirportEditorShape> shapes,
        AirportEditorFeatureKind kind,
        SKPaint paint
    )
    {
        using var path = new SKPath();
        foreach (AirportEditorShape shape in shapes)
        {
            if (shape.Kind == kind)
            {
                AddLine(path, viewport, shape);
            }
        }

        canvas.DrawPath(path, paint);
    }

    private static void AddLine(SKPath path, MapViewport viewport, AirportEditorShape shape)
    {
        for (int i = 0; i < shape.Positions.Length; i++)
        {
            (float x, float y) = viewport.LatLonToScreen(shape.Positions[i].Lat, shape.Positions[i].Lon);
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }
    }

    private static void DrawLine(SKCanvas canvas, MapViewport viewport, AirportEditorShape shape, SKPaint paint)
    {
        using var path = new SKPath();
        AddLine(path, viewport, shape);
        canvas.DrawPath(path, paint);
    }

    private static AirportEditorShape? ShapeOf(AirportEditorScene scene, int? featureIndex) =>
        featureIndex is int index ? scene.Shapes.FirstOrDefault(s => s.FeatureIndex == index) : null;

    private SKPaint NormalPaint(AirportEditorFeatureKind kind) =>
        kind switch
        {
            AirportEditorFeatureKind.Runway => _runwayPaint,
            AirportEditorFeatureKind.Taxiway => _taxiwayPaint,
            AirportEditorFeatureKind.Spot => _spotPaint,
            _ => _parkingPaint,
        };

    // The feature under the pointer, when it is not the selected one: the same shape in translucent white, 2 px wider.
    private void DrawHover(SKCanvas canvas, MapViewport viewport, AirportEditorRenderSnapshot snapshot)
    {
        if (
            (snapshot.Hover is not { } hover)
            || (hover.FeatureIndex == snapshot.SelectedFeatureIndex)
            || (ShapeOf(snapshot.Scene, hover.FeatureIndex) is not { } shape)
        )
        {
            return;
        }

        if (AirportEditorHitTest.IsLine(shape.Kind))
        {
            _hoverLinePaint.StrokeWidth = NormalPaint(shape.Kind).StrokeWidth + HoverExtraWidth;
            DrawLine(canvas, viewport, shape, _hoverLinePaint);
            return;
        }

        (float x, float y) = viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
        canvas.DrawCircle(x, y, PointRadius + (HoverExtraWidth / 2), _hoverFillPaint);
    }

    // The selected line: an amber stroke 3 px wider under its own stroke, then its vertex handles. The selected point:
    // an amber ring 4 px outside it.
    private void DrawSelection(SKCanvas canvas, MapViewport viewport, AirportEditorRenderSnapshot snapshot)
    {
        if (ShapeOf(snapshot.Scene, snapshot.SelectedFeatureIndex) is not { } shape)
        {
            return;
        }

        if (!AirportEditorHitTest.IsLine(shape.Kind))
        {
            (float x, float y) = viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
            canvas.DrawCircle(x, y, PointRadius + SelectedRingExtraRadius, _selectedRingPaint);
            return;
        }

        SKPaint normal = NormalPaint(shape.Kind);
        _selectedLinePaint.StrokeWidth = normal.StrokeWidth + SelectedLineExtraWidth;
        DrawLine(canvas, viewport, shape, _selectedLinePaint);
        DrawLine(canvas, viewport, shape, normal);
        int? hovered = (snapshot.Hover is { } hover) && (hover.FeatureIndex == shape.FeatureIndex) ? hover.VertexIndex : null;
        DrawHandles(canvas, viewport, shape, snapshot.SelectedVertexIndex, hovered);
    }

    // White handles on every vertex; the selected one larger and amber, a hovered one larger.
    private void DrawHandles(SKCanvas canvas, MapViewport viewport, AirportEditorShape shape, int? selected, int? hovered)
    {
        for (int i = 0; i < shape.Positions.Length; i++)
        {
            (float x, float y) = viewport.LatLonToScreen(shape.Positions[i].Lat, shape.Positions[i].Lon);
            bool isSelected = i == selected;
            float radius = (isSelected || (i == hovered)) ? ActiveHandleRadius : HandleRadius;
            canvas.DrawCircle(x, y, radius, isSelected ? _selectedHandlePaint : _handlePaint);
            canvas.DrawCircle(x, y, radius, _handleOutlinePaint);
        }
    }

    // The drawing's unsaved vertices joined in white with their handles, and a dashed white line from the last one to the
    // cursor.
    private void DrawGhost(SKCanvas canvas, MapViewport viewport, AirportEditorDrawGhost? ghost)
    {
        if ((ghost is null) || (ghost.Vertices.IsEmpty))
        {
            return;
        }

        using var path = new SKPath();
        foreach (MapPosition vertex in ghost.Vertices)
        {
            (float x, float y) = viewport.LatLonToScreen(vertex.Lat, vertex.Lon);
            if (path.IsEmpty)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        canvas.DrawPath(path, _ghostLinePaint);
        if (ghost.Cursor is { } cursor)
        {
            (float fromX, float fromY) = viewport.LatLonToScreen(ghost.Vertices[^1].Lat, ghost.Vertices[^1].Lon);
            (float toX, float toY) = viewport.LatLonToScreen(cursor.Lat, cursor.Lon);
            canvas.DrawLine(fromX, fromY, toX, toY, _ghostDashPaint);
        }

        foreach (MapPosition vertex in ghost.Vertices)
        {
            (float x, float y) = viewport.LatLonToScreen(vertex.Lat, vertex.Lon);
            canvas.DrawCircle(x, y, HandleRadius, _handlePaint);
            canvas.DrawCircle(x, y, HandleRadius, _handleOutlinePaint);
        }
    }

    // A magenta cross and ring on the snap target.
    private void DrawSnapCrosshair(SKCanvas canvas, MapViewport viewport, MapPosition? snapAt)
    {
        if (snapAt is not { } at)
        {
            return;
        }

        (float x, float y) = viewport.LatLonToScreen(at.Lat, at.Lon);
        canvas.DrawLine(x - CrosshairArm, y, x + CrosshairArm, y, _snapPaint);
        canvas.DrawLine(x, y - CrosshairArm, x, y + CrosshairArm, _snapPaint);
        canvas.DrawCircle(x, y, CrosshairRadius, _snapPaint);
    }

    // Each taxiway's and runway's name at the middle of its middle segment.
    private void DrawLineLabels(SKCanvas canvas, MapViewport viewport, ImmutableArray<AirportEditorShape> shapes)
    {
        foreach (AirportEditorShape shape in shapes)
        {
            (SKFont font, SKPaint paint)? style = shape.Kind switch
            {
                AirportEditorFeatureKind.Taxiway => (_taxiwayLabelFont, _taxiwayLabelPaint),
                AirportEditorFeatureKind.Runway => (_runwayLabelFont, _runwayLabelPaint),
                _ => null,
            };
            if ((style is not { } s) || (shape.Label is null))
            {
                continue;
            }

            int after = shape.Positions.Length / 2;
            MapPosition a = shape.Positions[after - 1];
            MapPosition b = shape.Positions[after];
            (float x, float y) = viewport.LatLonToScreen((a.Lat + b.Lat) / 2, (a.Lon + b.Lon) / 2);
            DrawHaloText(canvas, shape.Label, new SKPoint(x, y + (s.font.Size / 2) - 1), SKTextAlign.Center, s.font, s.paint);
        }
    }

    private void DrawHaloText(SKCanvas canvas, string text, SKPoint at, SKTextAlign align, SKFont font, SKPaint paint)
    {
        canvas.DrawText(text, at.X, at.Y, align, font, _labelHaloPaint);
        canvas.DrawText(text, at.X, at.Y, align, font, paint);
    }
}
