using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// Draws the elastic heading vector as the Radar's heading preview does: the standard-rate turn arc from the aircraft's present
/// heading into the one its cursor names (left out for a course change under three degrees or a taxiing aircraft), the straight
/// line from the arc's end to the snapped endpoint, and the label "{heading}M {nm}nm {m:ss}" beside it in amber.
/// </summary>
public sealed class ScopeHeadingVectorRenderer : IDisposable
{
    private const double StdTurnRateDegPerSec = 3.0;
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
        double change = GeoMath.SignedBearingDifference(overlay.HeadingTrue, headingTrue);
        double distNm = GeoMath.DistanceNm(overlay.Aircraft, overlay.Cursor);
        // Projecting along the snapped bearing, so the arc, the line and the label agree with the heading that will be sent.
        LatLon end = GeoMath.ProjectPoint(overlay.Aircraft, new TrueHeading(headingTrue), distNm);
        (float endX, float endY) = vp.LatLonToScreen(end.Lat, end.Lon);

        (float acX, float acY) = vp.LatLonToScreen(overlay.Aircraft.Lat, overlay.Aircraft.Lon);
        SKPoint arcEnd = new(acX, acY);
        if ((Math.Abs(change) >= 3.0) && (overlay.GroundSpeed > 30))
        {
            arcEnd = DrawArc(canvas, vp, overlay, change, headingTrue);
        }

        canvas.DrawLine(arcEnd.X, arcEnd.Y, endX, endY, _linePaint);
        DrawLabel(canvas, Label(overlay, headingMag, distNm), endX + 12, endY - 8);
    }

    // The standard-rate arc from the present heading into the new one, around the centre on the turn's side; returns where it
    // ends, which is where the straight line starts.
    private SKPoint DrawArc(SKCanvas canvas, MapViewport vp, ScopeHeadingVectorOverlay overlay, double change, double newHeadingTrue)
    {
        double radiusNm = ScopeHeadingVectorState.TurnRadiusNm(overlay.GroundSpeed, StdTurnRateDegPerSec);
        double side = change > 0 ? 1 : -1;
        LatLon center = GeoMath.ProjectPoint(overlay.Aircraft, new TrueHeading(overlay.HeadingTrue + (90 * side)), radiusNm);
        double startBearing = overlay.HeadingTrue - (90 * side);
        double endBearing = newHeadingTrue - (90 * side);

        var end = new SKPoint();
        using var path = new SKPath();
        for (int i = 0; i <= ArcSegments; i++)
        {
            double bearing = GeoMath.BlendBearings(startBearing, endBearing, (double)i / ArcSegments);
            LatLon point = GeoMath.ProjectPoint(center, new TrueHeading(bearing), radiusNm);
            (float x, float y) = vp.LatLonToScreen(point.Lat, point.Lon);
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }

            if (i == ArcSegments)
            {
                end = new SKPoint(x, y);
            }
        }

        canvas.DrawPath(path, _linePaint);
        return end;
    }

    // "{heading}M  {nm}nm  {m:ss}", without the time for an aircraft barely moving; the Radar's label verbatim.
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
