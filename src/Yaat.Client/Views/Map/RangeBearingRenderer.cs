using SkiaSharp;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Draws the distance measuring tool's range/bearing lines. Shared by the Radar and Ground renderers so
/// a measurement looks the same wherever it is read.
/// </summary>
/// <remarks>
/// White, like CRC STARS' RBLs, which keeps it distinct from YAAT's green route and taxi overlays. The
/// half-placed line following the cursor is dashed; committed measurements are solid.
/// </remarks>
public sealed class RangeBearingRenderer : IDisposable
{
    private static readonly SKColor LineColor = new(0xFF, 0xFF, 0xFF);

    private readonly SKPaint _linePaint = new()
    {
        Color = LineColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _pendingPaint = new()
    {
        Color = LineColor.WithAlpha(170),
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([6, 4], 0),
    };

    private readonly SKPaint _endpointPaint = new()
    {
        Color = LineColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _labelPaint = new() { Color = LineColor, IsAntialias = true };

    private readonly SKPaint _labelShadowPaint = new() { Color = new SKColor(0, 0, 0, 180), IsAntialias = true };

    /// <summary>Size of the readout font; a canvas measures readouts with a font of this size to place them.</summary>
    public const float LabelFontSize = 12f;

    private readonly SKFont _labelFont = PlatformHelper.MonospaceFont(LabelFontSize);

    /// <summary>Half-length of the cross drawn at an endpoint that is not latched to an aircraft.</summary>
    private const float EndpointMarkerPx = 4f;

    /// <summary>
    /// Builds the readouts to place this frame, one per line with any part on-screen: the ring anchor (the far end, or
    /// where the line leaves the screen), the text box measured with <paramref name="font"/>, and the latched far end's
    /// symbol. The half-placed line (slot 0) never nudges datablocks.
    /// </summary>
    public static List<RblReadout> BuildReadouts(IReadOnlyList<ResolvedRbl>? lines, ResolvedRbl? pending, MapViewport viewport, SKFont font)
    {
        var readouts = new List<RblReadout>((lines?.Count ?? 0) + 1);
        foreach (ResolvedRbl line in lines ?? [])
        {
            AddReadout(readouts, line, viewport, font);
        }

        if (pending is not null)
        {
            AddReadout(readouts, pending, viewport, font);
        }

        return readouts;
    }

    private static void AddReadout(List<RblReadout> readouts, ResolvedRbl line, MapViewport viewport, SKFont font)
    {
        (float ax, float ay) = viewport.LatLonToScreen(line.A.Lat, line.A.Lon);
        (float bx, float by) = viewport.LatLonToScreen(line.B.Lat, line.B.Lon);
        if (RblLabelPlacement.Anchor(ax, ay, bx, by, viewport.PixelWidth, viewport.PixelHeight) is not { } anchor)
        {
            return;
        }

        var size = new SKSize(font.MeasureText(line.Label), font.Size);
        SKPoint? ownSymbol = line.BLatched ? new SKPoint(bx, by) : null;
        readouts.Add(new RblReadout(line.Slot, new SKPoint(anchor.X, anchor.Y), size, ownSymbol, line.Slot != 0));
    }

    /// <summary>
    /// Draws every placed measurement plus the half-placed one, if any, each readout in the rect
    /// <paramref name="readoutRects"/> placed for its slot (a line without one gets no readout). Call last in a view's
    /// render pass so measurements stay legible over targets and datablocks.
    /// </summary>
    public void Draw(
        SKCanvas canvas,
        MapViewport viewport,
        IReadOnlyList<ResolvedRbl>? lines,
        ResolvedRbl? pending,
        IReadOnlyDictionary<int, SKRect> readoutRects
    )
    {
        if (lines is not null)
        {
            foreach (ResolvedRbl line in lines)
            {
                Draw(canvas, viewport, line, _linePaint, readoutRects);
            }
        }

        if (pending is not null)
        {
            Draw(canvas, viewport, pending, _pendingPaint, readoutRects);
        }
    }

    private void Draw(SKCanvas canvas, MapViewport viewport, ResolvedRbl line, SKPaint linePaint, IReadOnlyDictionary<int, SKRect> readoutRects)
    {
        (float ax, float ay) = viewport.LatLonToScreen(line.A.Lat, line.A.Lon);
        (float bx, float by) = viewport.LatLonToScreen(line.B.Lat, line.B.Lon);

        canvas.DrawLine(ax, ay, bx, by, linePaint);

        // A latched end sits on an aircraft symbol that already marks the spot; a fixed end needs one.
        if (!line.ALatched)
        {
            DrawEndpointMarker(canvas, ax, ay);
        }

        if (!line.BLatched)
        {
            DrawEndpointMarker(canvas, bx, by);
        }

        // The readout sits where the canvas placed it around the far end (CRC's spot unless a datablock is
        // there), clamped into the viewport so a partially visible line still shows its reading. The shadow
        // keeps it readable over video maps.
        if (readoutRects.TryGetValue(line.Slot, out SKRect placed))
        {
            SKRect label = RblLabelPlacement.ClampIntoView(placed, viewport.PixelWidth, viewport.PixelHeight);
            canvas.DrawText(line.Label, label.Left + 1, label.Bottom + 1, SKTextAlign.Left, _labelFont, _labelShadowPaint);
            canvas.DrawText(line.Label, label.Left, label.Bottom, SKTextAlign.Left, _labelFont, _labelPaint);
        }
    }

    private void DrawEndpointMarker(SKCanvas canvas, float x, float y)
    {
        canvas.DrawLine(x - EndpointMarkerPx, y, x + EndpointMarkerPx, y, _endpointPaint);
        canvas.DrawLine(x, y - EndpointMarkerPx, x, y + EndpointMarkerPx, _endpointPaint);
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _pendingPaint.Dispose();
        _endpointPaint.Dispose();
        _labelPaint.Dispose();
        _labelShadowPaint.Dispose();
        _labelFont.Dispose();
    }
}
