using System.Globalization;
using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope's min-sep pair: its two tracks, where they are now, and their projection (converging or diverging).</summary>
/// <param name="CallsignA">The first track's callsign.</param>
/// <param name="CallsignB">The second track's callsign.</param>
/// <param name="PositionA">The first track's position the projection started from.</param>
/// <param name="PositionB">The second track's position the projection started from.</param>
/// <param name="Result">The projection: <see cref="MinSepConverging"/> or <see cref="MinSepDiverging"/>.</param>
public sealed record ScopeMinSep(string CallsignA, string CallsignB, LatLon PositionA, LatLon PositionB, MinSepResult Result);

/// <summary>
/// Draws the Scope's min-sep pair as CRC STARS draws it. Converging: a line from track A to its predicted point, on to B's
/// predicted point and back to track B, a small filled marker at each predicted point (the Scope's monospace font maps no
/// glyph to CRC's 0x10 marker), and the closest distance boxed at the middle of the predicted points. Diverging: a line
/// between the two tracks and a two-line <c>NO XING</c> box over the current distance. White and unscaled by BRITE, at the
/// measuring tool's readout size, as the Scope draws its range/bearing lines; CRC takes the brightness from its TLS control,
/// which the Scope has no group for.
/// </summary>
public sealed class MinSepRenderer : IDisposable
{
    /// <summary>The first line of a diverging pair's box.</summary>
    public const string NoCrossing = "NO XING";

    private const float MarkerRadiusPx = 2.5f;

    private const float BoxPaddingPx = 3f;

    /// <summary>How far each text line's baseline sits above the bottom of its line in the box, so descenders stay inside.</summary>
    private const float BaselineLiftPx = 2f;

    private static readonly SKColor LineColor = new(0xFF, 0xFF, 0xFF);

    private readonly SKPaint _linePaint = new()
    {
        Color = LineColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _markerPaint = new()
    {
        Color = LineColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _boxFillPaint = new() { Color = SKColors.Black, Style = SKPaintStyle.Fill };

    private readonly SKPaint _textPaint = new() { Color = LineColor, IsAntialias = true };

    private readonly SKFont _font = PlatformHelper.MonospaceFont(RangeBearingRenderer.LabelFontSize);

    /// <summary>A distance as the readout shows it: NM to hundredths, <c>0.00 NM</c>.</summary>
    public static string FormatNm(double nm) => $"{nm.ToString("0.00", CultureInfo.InvariantCulture)} NM";

    /// <summary>The readout's lines: the closest distance for a converging pair; <c>NO XING</c> over the current distance otherwise.</summary>
    public static IReadOnlyList<string> LabelLines(MinSepResult result) =>
        result switch
        {
            MinSepConverging converging => [FormatNm(converging.ClosestNm)],
            MinSepDiverging diverging => [NoCrossing, FormatNm(diverging.DistanceNm)],
            _ => [],
        };

    /// <summary>Draws <paramref name="pair"/>; nothing when it is null. Call last, with the measure lines.</summary>
    public void Draw(SKCanvas canvas, MapViewport viewport, ScopeMinSep? pair)
    {
        if (pair is null)
        {
            return;
        }

        SKPoint a = ToScreen(viewport, pair.PositionA);
        SKPoint b = ToScreen(viewport, pair.PositionB);
        if (pair.Result is MinSepConverging converging)
        {
            SKPoint pa = ToScreen(viewport, converging.PointA);
            SKPoint pb = ToScreen(viewport, converging.PointB);
            canvas.DrawLine(a, pa, _linePaint);
            canvas.DrawLine(pa, pb, _linePaint);
            canvas.DrawLine(pb, b, _linePaint);
            canvas.DrawCircle(pa, MarkerRadiusPx, _markerPaint);
            canvas.DrawCircle(pb, MarkerRadiusPx, _markerPaint);
            DrawBox(canvas, viewport, Midpoint(pa, pb), LabelLines(converging));
        }
        else
        {
            canvas.DrawLine(a, b, _linePaint);
            DrawBox(canvas, viewport, Midpoint(a, b), LabelLines(pair.Result));
        }
    }

    // The lines boxed and centred on the anchor, clamped into the viewport so a pair partly off-screen still reads.
    private void DrawBox(SKCanvas canvas, MapViewport viewport, SKPoint anchor, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        float lineHeight = _font.Size;
        float width = lines.Max(line => _font.MeasureText(line)) + (2 * BoxPaddingPx);
        float height = (lines.Count * lineHeight) + (2 * BoxPaddingPx);
        var placed = SKRect.Create(anchor.X - (width / 2), anchor.Y - (height / 2), width, height);
        SKRect box = RblLabelPlacement.ClampIntoView(placed, viewport.PixelWidth, viewport.PixelHeight);
        canvas.DrawRect(box, _boxFillPaint);
        canvas.DrawRect(box, _linePaint);
        for (int i = 0; i < lines.Count; i++)
        {
            float baseline = box.Top + BoxPaddingPx + ((i + 1) * lineHeight) - BaselineLiftPx;
            canvas.DrawText(lines[i], box.MidX, baseline, SKTextAlign.Center, _font, _textPaint);
        }
    }

    private static SKPoint ToScreen(MapViewport viewport, LatLon point)
    {
        (float x, float y) = viewport.LatLonToScreen(point.Lat, point.Lon);
        return new SKPoint(x, y);
    }

    private static SKPoint Midpoint(SKPoint p, SKPoint q) => new((p.X + q.X) / 2, (p.Y + q.Y) / 2);

    public void Dispose()
    {
        _linePaint.Dispose();
        _markerPaint.Dispose();
        _boxFillPaint.Dispose();
        _textPaint.Dispose();
        _font.Dispose();
    }
}
