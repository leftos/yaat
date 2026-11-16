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
///   - Drag-style: mouse-down on AHDG, drag past <see cref="DragThresholdPxSq"/> while held, and release
///     more than that distance from <see cref="DragOrigin"/>. <see cref="DraggedPastThreshold"/> tracks
///     the threshold crossing. A click that slips a few pixels never confirms, and neither does a drag
///     that wanders back onto the press point.
///   - Click-style: mouse-down on AHDG, release without a deliberate drag (sets <see cref="ButtonHeld"/>
///     to false). Subsequent left-click on the map confirms.
/// </summary>
public sealed class HeadingModeState
{
    /// <summary>Pixel² movement (10 px) that separates a deliberate drag from a click that slipped.</summary>
    public const double DragThresholdPxSq = 100;

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
///   1. A roll-in lead drawn straight along the aircraft's current heading, then a turn arc curving
///      into the new heading (bearing aircraft -&gt; cursor). Radius = the arc flown at the aircraft's
///      type turn rate and current ground speed.
///   2. A straight line from the arc end-point to the cursor position.
///   3. A label near the cursor: "275M  3.2nm  0:48" (heading magnetic / distance / ETA).
///
/// For very small course changes (&lt;3 deg) the arc is omitted and only the straight line is drawn.
/// </summary>
public static class HeadingPreviewRenderer
{
    private const int ArcSegments = 24;

    private static readonly SKColor PreviewColor = new(255, 220, 80);
    private static readonly SKColor LabelBgColor = new(0, 0, 0, 180);

    public static void Render(SKCanvas canvas, MapViewport vp, AircraftModel ac, HeadingModeState state)
    {
        using var textPaint = new SKPaint { Color = PreviewColor, IsAntialias = true };
        using SKFont textFont = Services.PlatformHelper.MonospaceFontBold(12);
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

        (float acSx, float acSy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

        // Project the cursor's distance along the SNAPPED bearing so the line, arc, and label
        // all visually agree with the heading that will actually be dispatched.
        double cursorDistNm = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, state.CursorPos.Lat, state.CursorPos.Lon);
        (double endLat, double endLon) = GeoMath.ProjectPoint(ac.Position.Lat, ac.Position.Lon, new TrueHeading(newHeadingTrue), cursorDistNm);
        (float curSx, float curSy) = vp.LatLonToScreen(endLat, endLon);

        SKPoint arcEndScreen = new(acSx, acSy);

        // Turn drawing: only meaningful if there's a course change AND the aircraft has appreciable speed.
        if ((absCourseChange >= 3.0) && (ac.GroundSpeed > 30))
        {
            var anchor = new TurnAnchor(ac.Position, curHeadingTrue, newHeadingTrue, courseChange);
            arcEndScreen = DrawTurn(canvas, vp, anchor, TurnFor(ac, courseChange), linePaint);
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
    /// The turn the preview draws for <paramref name="ac"/>: the full turn rate of its type (the lookup
    /// the sim's turns use), the arc radius at the aircraft's ground speed, and the roll-in lead a ramped
    /// turn adds before the arc. The client has no session turn model on the wire, so the preview draws
    /// <see cref="TurnModel.Ramp"/>, which every scenario load flies; a recording made before the ramp
    /// flies <see cref="TurnModel.Instant"/> and has no lead. The lead is from wings level: the aircraft's
    /// applied turn rate is not on the wire.
    /// </summary>
    internal static PreviewTurn TurnFor(AircraftModel ac, double courseChangeDeg)
    {
        double turnRateDegPerSec = AircraftPerformance.TurnRate(ac.AircraftType, ac.Category);
        double radiusNm = TurnShape.RadiusNm(ac.GroundSpeed, turnRateDegPerSec);
        double leadNm = TurnShape.LeadNm(new TurnLeadInput(ac.GroundSpeed, courseChangeDeg, turnRateDegPerSec, ac.Category, TurnModel.Ramp));
        return new PreviewTurn(turnRateDegPerSec, radiusNm, leadNm);
    }

    /// <summary>
    /// The lead's end and the arc's centre of <paramref name="turn"/> from <paramref name="anchor"/>: the lead
    /// runs straight along the current heading, and the centre sits a radius abeam the lead's end, on the side
    /// of the turn.
    /// </summary>
    internal static (LatLon LeadEnd, LatLon Center) TurnGeometry(TurnAnchor anchor, PreviewTurn turn)
    {
        (double leadLat, double leadLon) = GeoMath.ProjectPoint(
            anchor.Position.Lat,
            anchor.Position.Lon,
            new TrueHeading(anchor.CurrentHeadingTrue),
            turn.LeadNm
        );
        (double centerLat, double centerLon) = GeoMath.ProjectPoint(
            leadLat,
            leadLon,
            new TrueHeading(anchor.CurrentHeadingTrue + TurnSide(anchor.CourseChange)),
            turn.RadiusNm
        );
        return (new LatLon(leadLat, leadLon), new LatLon(centerLat, centerLon));
    }

    /// <summary>The bearing offset of the arc's centre from the lead's end: +90 for a right turn, -90 for a left.</summary>
    private static double TurnSide(double courseChangeDeg) => courseChangeDeg > 0 ? 90 : -90;

    /// <summary>
    /// Draws the lead and the arc of one preview turn and returns the arc's end point on screen.
    /// </summary>
    private static SKPoint DrawTurn(SKCanvas canvas, MapViewport vp, TurnAnchor anchor, PreviewTurn turn, SKPaint paint)
    {
        (LatLon leadEnd, LatLon center) = TurnGeometry(anchor, turn);
        (float acSx, float acSy) = vp.LatLonToScreen(anchor.Position.Lat, anchor.Position.Lon);
        (float startSx, float startSy) = vp.LatLonToScreen(leadEnd.Lat, leadEnd.Lon);
        if (turn.LeadNm > 0)
        {
            canvas.DrawLine(acSx, acSy, startSx, startSy, paint);
        }

        double side = TurnSide(anchor.CourseChange);
        using var arcPath = new SKPath();
        var arcEndScreen = new SKPoint(startSx, startSy);
        for (int i = 0; i <= ArcSegments; i++)
        {
            double t = (double)i / ArcSegments;
            double b = GeoMath.BlendBearings(anchor.CurrentHeadingTrue - side, anchor.TargetHeadingTrue - side, t);
            (double lat, double lon) = GeoMath.ProjectPoint(center.Lat, center.Lon, new TrueHeading(b), turn.RadiusNm);
            (float sx, float sy) = vp.LatLonToScreen(lat, lon);
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
        canvas.DrawPath(arcPath, paint);
        return arcEndScreen;
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

/// <summary>
/// The turn a heading-select preview draws: the full turn rate of the aircraft's type (deg/s), the arc
/// radius at its ground speed, and the roll-in lead flown straight from wings level before the arc starts.
/// </summary>
internal readonly record struct PreviewTurn(double TurnRateDegPerSec, double RadiusNm, double LeadNm);

/// <summary>
/// Where a preview turn starts and where it heads: the aircraft position, its current and target true
/// headings, and the signed course change between them (positive right).
/// </summary>
internal readonly record struct TurnAnchor(LatLon Position, double CurrentHeadingTrue, double TargetHeadingTrue, double CourseChange);
