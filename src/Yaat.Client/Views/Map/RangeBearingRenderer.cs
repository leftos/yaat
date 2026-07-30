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

    private readonly SKFont _labelFont = PlatformHelper.MonospaceFont(12);

    /// <summary>Half-length of the cross drawn at an endpoint that is not latched to an aircraft.</summary>
    private const float EndpointMarkerPx = 4f;

    /// <summary>
    /// Draws every placed measurement plus the half-placed one, if any. Call last in a view's render pass
    /// so measurements stay legible over targets and datablocks.
    /// </summary>
    public void Draw(SKCanvas canvas, MapViewport viewport, IReadOnlyList<ResolvedRbl>? lines, ResolvedRbl? pending)
    {
        if (lines is not null)
        {
            foreach (var line in lines)
            {
                Draw(canvas, viewport, line, _linePaint);
            }
        }

        if (pending is not null)
        {
            Draw(canvas, viewport, pending, _pendingPaint);
        }
    }

    private void Draw(SKCanvas canvas, MapViewport viewport, ResolvedRbl line, SKPaint linePaint)
    {
        var (ax, ay) = viewport.LatLonToScreen(line.A.Lat, line.A.Lon);
        var (bx, by) = viewport.LatLonToScreen(line.B.Lat, line.B.Lon);

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

        // Label at the far end, matching CRC's placement. The shadow keeps it readable over video maps.
        var labelX = bx + 9;
        var labelY = by + 4;
        canvas.DrawText(line.Label, labelX + 1, labelY + 1, SKTextAlign.Left, _labelFont, _labelShadowPaint);
        canvas.DrawText(line.Label, labelX, labelY, SKTextAlign.Left, _labelFont, _labelPaint);
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
