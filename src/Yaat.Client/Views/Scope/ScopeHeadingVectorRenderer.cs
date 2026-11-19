using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// Draws the elastic heading vector: the turn arc from the aircraft's present heading into the one its cursor names at the
/// type's turn rate (left out for a course change under three degrees or a taxiing aircraft), the straight line from the
/// arc's end along that heading, and the label "{heading}M {nm}nm {m:ss}" beside it in amber.
/// </summary>
public sealed class ScopeHeadingVectorRenderer : IDisposable
{
    private const int ArcSegments = 24;

    private static readonly SKColor PreviewColor = new(255, 220, 80);
    private static readonly SKColor LabelBgColor = new(0, 0, 0, 180);

    private readonly SKPaint _linePaint = new()
    {
        Color = PreviewColor,
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _textPaint = new() { Color = PreviewColor, IsAntialias = true };

    private readonly SKFont _textFont = PlatformHelper.MonospaceFontBold(12);

    private readonly SKPaint _labelBgPaint = new()
    {
        Color = LabelBgColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    /// <summary>Draws <paramref name="overlay"/> over <paramref name="vp"/>.</summary>
    public void Draw(SKCanvas canvas, MapViewport vp, ScopeHeadingVectorOverlay overlay)
    {
        int headingMag = ScopeHeadingVectorState.MagneticHeading(overlay.Aircraft, overlay.Cursor);
        double headingTrue = MagneticDeclination.MagneticToTrue(headingMag, overlay.Aircraft);
        double distNm = GeoMath.DistanceNm(overlay.Aircraft, overlay.Cursor);
        ScopeHeadingVectorGeometry geometry = ScopeHeadingVectorState.Geometry(overlay, headingTrue);
        if (geometry.HasArc)
        {
            DrawArc(canvas, vp, overlay, headingTrue);
        }

        (float startX, float startY) = vp.LatLonToScreen(geometry.ArcEnd.Lat, geometry.ArcEnd.Lon);
        (float endX, float endY) = vp.LatLonToScreen(geometry.LegEnd.Lat, geometry.LegEnd.Lon);
        canvas.DrawLine(startX, startY, endX, endY, _linePaint);
        DrawLabel(canvas, Label(overlay, headingMag, distNm), endX + 12, endY - 8);
    }

    // The turn arc from the present heading into the new one at the type's turn rate, around the centre on the turn's side.
    private void DrawArc(SKCanvas canvas, MapViewport vp, ScopeHeadingVectorOverlay overlay, double snappedHeadingTrue)
    {
        using var path = new SKPath();
        for (int i = 0; i <= ArcSegments; i++)
        {
            LatLon point = ScopeHeadingVectorState.ArcPoint(overlay, snappedHeadingTrue, (double)i / ArcSegments);
            (float x, float y) = vp.LatLonToScreen(point.Lat, point.Lon);
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        canvas.DrawPath(path, _linePaint);
    }

    // "{heading}M  {nm}nm  {m:ss}", without the time for an aircraft barely moving.
    private static string Label(ScopeHeadingVectorOverlay overlay, int headingMag, double distNm)
    {
        string heading = new MagneticHeading(headingMag).ToDisplayString();
        return overlay.GroundSpeed > 1
            ? $"{heading}M  {distNm:0.0}nm  {ScopeHeadingVectorState.FormatEta(distNm, overlay.GroundSpeed)}"
            : $"{heading}M  {distNm:0.0}nm";
    }

    private void DrawLabel(SKCanvas canvas, string text, float x, float y)
    {
        float width = _textFont.MeasureText(text);
        canvas.DrawRect(new SKRect(x - 3, y - _textFont.Size - 1, x + width + 3, y + 3), _labelBgPaint);
        canvas.DrawText(text, x, y, SKTextAlign.Left, _textFont, _textPaint);
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _textPaint.Dispose();
        _textFont.Dispose();
        _labelBgPaint.Dispose();
    }
}
