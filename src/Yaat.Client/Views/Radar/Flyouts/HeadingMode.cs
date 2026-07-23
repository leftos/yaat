using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Current state of an active heading-vector interaction. Set by RadarCanvas when the user
/// clicks the AHDG field of a EuroScope tag. The cursor position is updated on every
/// PointerMoved while the mode is active. RadarRenderer reads this on every frame to paint
/// the live preview (turn arc + line + label).
///
/// Two confirm paths coexist:
///   - Drag-style: mouse-down on AHDG, drag past <see cref="DragThresholdPxSq"/> while held,
///     release confirms. <see cref="DraggedPastThreshold"/> tracks the threshold crossing.
///   - Click-style: mouse-down on AHDG, release without dragging (sets <see cref="ButtonHeld"/>
///     to false). Subsequent left-click on the map confirms.
/// </summary>
public sealed class HeadingModeState
{
    /// <summary>Pixel² movement before a button-held interaction becomes a drag.</summary>
    public const double DragThresholdPxSq = 16;

    public required string Callsign { get; init; }

    /// <summary>Current cursor position in lat/lon (updated as the mouse moves).</summary>
    public LatLon CursorPos { get; set; }

    /// <summary>Screen position where AHDG was clicked. Reference for drag-threshold detection.</summary>
    public required Avalonia.Point DragOrigin { get; init; }

    /// <summary>True while the mouse button has been held since heading-mode entry.</summary>
    public bool ButtonHeld { get; set; } = true;

    /// <summary>True once the cursor has moved past <see cref="DragThresholdPxSq"/> while held.</summary>
    public bool DraggedPastThreshold { get; set; }
}

/// <summary>
/// Draws the live heading-vector preview when <see cref="HeadingModeState"/> is active.
///
/// Visual elements (mirroring EuroScope's elastic vector + anticipation arc):
///   1. A turn arc from the aircraft's current position, curving from the current heading
///      into the new heading (bearing aircraft -&gt; cursor). Radius = standard-rate turn radius
///      computed from current ground speed.
///   2. A straight line from the arc end-point to the cursor position.
///   3. A label near the cursor: "275M  3.2nm  0:48" (heading magnetic / distance / ETA).
///
/// For very small course changes (&lt;3 deg) the arc is omitted and only the straight line is drawn.
/// </summary>
public static class HeadingPreviewRenderer
{
    private const double StdTurnRateDegPerSec = 3.0;
    private const int ArcSegments = 24;

    private static readonly SKColor PreviewColor = new(255, 220, 80);
    private static readonly SKColor LabelBgColor = new(0, 0, 0, 180);

    public static void Render(SKCanvas canvas, MapViewport vp, AircraftModel ac, HeadingModeState state)
    {
        using var textPaint = new SKPaint { Color = PreviewColor, IsAntialias = true };
        using var textFont = Services.PlatformHelper.MonospaceFontBold(12);
        var textStyle = new TextStyle(textFont, textPaint);
        double cursorBearingTrue = GeoMath.BearingTo(ac.Position.Lat, ac.Position.Lon, state.CursorPos.Lat, state.CursorPos.Lon);
        double cursorBearingMag = MagneticDeclination.TrueToMagnetic(cursorBearingTrue, ac.Position);
        int snappedHdgMag = SnapHeadingTo5(cursorBearingMag);
        double newHeadingMag = snappedHdgMag;
        double newHeadingTrue = MagneticDeclination.MagneticToTrue(newHeadingMag, ac.Position);
        double curHeadingTrue = ac.Heading.Degrees;
        double courseChange = GeoMath.SignedBearingDifference(curHeadingTrue, newHeadingTrue);
        double absCourseChange = Math.Abs(courseChange);

        using var linePaint = new SKPaint
        {
            Color = PreviewColor,
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };

        var (acSx, acSy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

        // Project the cursor's distance along the SNAPPED bearing so the line, arc, and label
        // all visually agree with the heading that will actually be dispatched.
        double cursorDistNm = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, state.CursorPos.Lat, state.CursorPos.Lon);
        var (endLat, endLon) = GeoMath.ProjectPoint(ac.Position.Lat, ac.Position.Lon, new TrueHeading(newHeadingTrue), cursorDistNm);
        var (curSx, curSy) = vp.LatLonToScreen(endLat, endLon);

        SKPoint arcEndScreen = new(acSx, acSy);

        // Arc: only meaningful if there's a course change AND the aircraft has appreciable speed.
        if (absCourseChange >= 3.0 && ac.GroundSpeed > 30)
        {
            double radiusNm = TurnRadiusNm(ac.GroundSpeed, StdTurnRateDegPerSec);
            // Arc center is perpendicular to current heading, on the side of the turn.
            double perpBearing = courseChange > 0 ? curHeadingTrue + 90 : curHeadingTrue - 90;
            var (centerLat, centerLon) = GeoMath.ProjectPoint(ac.Position.Lat, ac.Position.Lon, new TrueHeading(perpBearing), radiusNm);

            double startBearingFromCenter = courseChange > 0 ? curHeadingTrue - 90 : curHeadingTrue + 90;
            double endBearingFromCenter = courseChange > 0 ? newHeadingTrue - 90 : newHeadingTrue + 90;

            using var arcPath = new SKPath();
            for (int i = 0; i <= ArcSegments; i++)
            {
                double t = (double)i / ArcSegments;
                double b = GeoMath.BlendBearings(startBearingFromCenter, endBearingFromCenter, t);
                var (lat, lon) = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(b), radiusNm);
                var (sx, sy) = vp.LatLonToScreen(lat, lon);
                if (i == 0)
                {
                    arcPath.MoveTo(sx, sy);
                }
                else
                {
                    arcPath.LineTo(sx, sy);
                }
                if (i == ArcSegments)
                {
                    arcEndScreen = new SKPoint(sx, sy);
                }
            }
            canvas.DrawPath(arcPath, linePaint);
        }

        // Straight line from arc end (or aircraft position if no arc) to cursor.
        canvas.DrawLine(arcEndScreen.X, arcEndScreen.Y, curSx, curSy, linePaint);

        // Label near snapped end-point.
        string hdgStr = new MagneticHeading(snappedHdgMag).ToDisplayString();
        string label =
            ac.GroundSpeed > 1 ? $"{hdgStr}M  {cursorDistNm:0.0}nm  {FormatEta(cursorDistNm, ac.GroundSpeed)}" : $"{hdgStr}M  {cursorDistNm:0.0}nm";

        DrawLabel(canvas, label, curSx + 12, curSy - 8, textStyle);
    }

    /// <summary>
    /// Snap a magnetic heading in degrees to the nearest 5° bucket, mapped to [5, 360].
    /// Wraps so 0/360 collapse to 360 (matching the 001..360 convention used elsewhere).
    /// </summary>
    internal static int SnapHeadingTo5(double magneticDeg)
    {
        int snapped = (int)(Math.Round(magneticDeg / 5.0) * 5);
        snapped = ((snapped % 360) + 360) % 360;
        return snapped == 0 ? 360 : snapped;
    }

    private static double TurnRadiusNm(double groundSpeedKts, double turnRateDegPerSec)
    {
        if (groundSpeedKts <= 0)
        {
            return 0;
        }
        double turnRateRadPerSec = turnRateDegPerSec * Math.PI / 180.0;
        double gsNmPerSec = groundSpeedKts / 3600.0;
        return gsNmPerSec / turnRateRadPerSec;
    }

    private static string FormatEta(double distNm, double groundSpeedKts)
    {
        if (groundSpeedKts < 1)
        {
            return "--:--";
        }
        double seconds = distNm / groundSpeedKts * 3600;
        int totalSec = (int)Math.Round(seconds);
        return $"{totalSec / 60:D1}:{totalSec % 60:D2}";
    }

    private static void DrawLabel(SKCanvas canvas, string text, float x, float y, TextStyle style)
    {
        float w = style.Measure(text);
        float h = style.Size;
        var bgRect = new SKRect(x - 3, y - h - 1, x + w + 3, y + 3);
        using var bgPaint = new SKPaint
        {
            Color = LabelBgColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawRect(bgRect, bgPaint);
        canvas.DrawText(text, x, y, SKTextAlign.Left, style.Font, style.Paint);
    }
}
