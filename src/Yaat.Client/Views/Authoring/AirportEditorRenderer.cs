using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;

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

/// <summary>One feature as the Airport Editor draws it: its kind, its positions and its label.</summary>
/// <param name="Kind">What the feature is.</param>
/// <param name="Positions">The feature's positions in source order: one for a point, two or more for a line.</param>
/// <param name="Label">The feature's <c>name</c>, or null when it has none.</param>
public sealed record AirportEditorShape(AirportEditorFeatureKind Kind, ImmutableArray<MapPosition> Positions, string? Label);

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
        foreach (MapFeature feature in document.Features)
        {
            if (KindOf(feature) is { } kind)
            {
                shapes.Add(new AirportEditorShape(kind, feature.Coordinates, NameOf(feature)));
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

    // The name as GeoJsonParser.TryReadNameText reads it (private to Yaat.Sim, so mirrored): the key matched exactly, else
    // case-insensitively; a string as written, a number as its JSON text, anything else none. A blank name labels nothing.
    private static string? NameOf(MapFeature feature)
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

/// <summary>What the render thread draws one Airport Editor frame from: the scene and the mode shown in the HUD.</summary>
/// <param name="Scene">The open map's scene.</param>
/// <param name="ModeText">The canvas's mode, shown bottom-left.</param>
public sealed record AirportEditorRenderSnapshot(AirportEditorScene Scene, string ModeText);

/// <summary>
/// Draws the Airport Editor's map on its dark background: runways, taxiways, parking, spots and helipads, then the
/// taxiway and runway labels, then the HUD. Owns its paints and fonts; dispose it once the canvas is done with it.
/// </summary>
public sealed class AirportEditorRenderer : IDisposable
{
    private const float PointRadius = 2.6f;
    private const float HudInset = 8f;

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
        ImmutableArray<AirportEditorShape> shapes = snapshot.Scene.Shapes;
        DrawLines(canvas, viewport, shapes, AirportEditorFeatureKind.Runway, _runwayPaint);
        DrawLines(canvas, viewport, shapes, AirportEditorFeatureKind.Taxiway, _taxiwayPaint);
        foreach (AirportEditorShape shape in shapes)
        {
            SKPaint? paint = shape.Kind switch
            {
                AirportEditorFeatureKind.Parking or AirportEditorFeatureKind.Helipad => _parkingPaint,
                AirportEditorFeatureKind.Spot => _spotPaint,
                _ => null,
            };
            if (paint is not null)
            {
                (float x, float y) = viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
                canvas.DrawCircle(x, y, PointRadius, paint);
            }
        }

        DrawLineLabels(canvas, viewport, shapes);
        DrawHaloText(canvas, snapshot.ModeText, new SKPoint(HudInset, viewport.PixelHeight - HudInset), SKTextAlign.Left, _hudFont, _hudPaint);
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
        _labelHaloPaint.Dispose();
        _taxiwayLabelFont.Dispose();
        _runwayLabelFont.Dispose();
        _hudFont.Dispose();
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
            if (shape.Kind != kind)
            {
                continue;
            }

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

        canvas.DrawPath(path, paint);
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
