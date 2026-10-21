using SkiaSharp;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// Draws a menu row's radar cue (<see cref="RadarRenderer.DrawMenuCue"/>) on a black offscreen bitmap and probes it: the
/// solid ring in the highlight blue on the target, the dashed ring in teal on another aircraft the call resolves to, the
/// dashed bearing line from the commanded aircraft to the target, and the clock and miles label at the line's midpoint,
/// which the cue leaves out when told to; with no cue, nothing is drawn.
/// </summary>
public class RadarCueRenderTests
{
    private const double CenterLat = 37.72;
    private const double CenterLon = -122.22;
    private const int SurfacePx = 400;

    /// <summary>The commanded aircraft, 2 nm south of the centre, heading north.</summary>
    private static readonly LatLon FromPosition = new(CenterLat - (2.0 / 60), CenterLon);

    /// <summary>The pointed target, 2 nm north of the centre: at the commanded aircraft's 12 o'clock, 4 nm.</summary>
    private static readonly LatLon TargetPosition = new(CenterLat + (2.0 / 60), CenterLon);

    [Fact]
    public void Ring_DrawnAtTarget()
    {
        MapViewport vp = Viewport();
        (float tx, float ty) = vp.LatLonToScreen(TargetPosition.Lat, TargetPosition.Lon);

        using SKBitmap bitmap = Render(vp, new ResolvedRadarCue(TargetPosition, null, null, null));

        AssertSolidRing(bitmap, tx, ty);
        Assert.True(IsBackground(PixelAt(bitmap, new SKPoint(tx, ty))), "the ring's centre should stay clear");
    }

    [Fact]
    public void ResolvedOther_GetsDashedRing_PointedKeepsSolid()
    {
        MapViewport vp = Viewport();
        (float tx, float ty) = vp.LatLonToScreen(TargetPosition.Lat, TargetPosition.Lon);
        (double otherLat, double otherLon) = vp.ScreenToLatLon(tx + 100, ty);

        using SKBitmap bitmap = Render(vp, new ResolvedRadarCue(TargetPosition, new LatLon(otherLat, otherLon), null, null));

        AssertSolidRing(bitmap, tx, ty);
        List<SKColor> other = [.. RingSamples(tx + 100, ty).Select(point => PixelAt(bitmap, point))];
        Assert.Contains(other, IsTeal);
        Assert.Contains(other, IsBackground);
        Assert.DoesNotContain(other, IsHighlightBlue);
    }

    [Fact]
    public void BearingLine_ConnectsSelectedToTarget()
    {
        MapViewport vp = Viewport();
        (float fx, float fy) = vp.LatLonToScreen(FromPosition.Lat, FromPosition.Lon);
        (float tx, float ty) = vp.LatLonToScreen(TargetPosition.Lat, TargetPosition.Lon);
        Assert.True(fy - ty > 100, $"the test zoom must put the pair well apart, was {fy - ty} px");

        using SKBitmap bitmap = Render(vp, new ResolvedRadarCue(TargetPosition, null, FromPosition, null));

        // The line runs straight up from the commanded aircraft; past the target ring it is dashed.
        List<SKColor> line = [.. Enumerable.Range((int)(ty + 20), (int)(fy - ty - 20)).Select(y => PixelAt(bitmap, new SKPoint(fx, y)))];
        Assert.Contains(line, IsHighlightBlue);
        Assert.Contains(line, IsBackground);
        Assert.True(IsBackground(PixelAt(bitmap, new SKPoint(fx, fy + 20))), "the line should stop at the commanded aircraft");
        Assert.True(IsBackground(PixelAt(bitmap, new SKPoint(fx + 20, (fy + ty) / 2))), "the line should run straight to the target");
    }

    [Fact]
    public void BearingLabel_ReadsClockAndMiles()
    {
        AircraftModel from = Airborne("N52TW", FromPosition);
        AircraftModel target = Airborne("SWA1", TargetPosition);
        MapViewport vp = Viewport();

        ResolvedRadarCue? cue = new RadarCue(from.Callsign, target.Callsign, null, ShowBearingLabel: true).ResolveAgainst([from, target]);

        Assert.NotNull(cue);
        MenuTrafficRow row = RelativeGeometry.TrafficRow(from, target);
        Assert.Equal((12, 4), (row.ClockPosition, RelativeGeometry.WholeNm(row.DistanceNm)));
        Assert.Equal("12 o'clock · 4 nm", cue.BearingLabel);
        using SKBitmap bitmap = Render(vp, cue);
        Assert.Contains(LabelBox(vp, bitmap), IsHighlightBlue);
    }

    [Fact]
    public void ResolvedWithNoTarget_DrawsTheDashedRingAlone()
    {
        AircraftModel from = Airborne("N52TW", FromPosition);
        AircraftModel resolved = Airborne("N22RX", TargetPosition);
        MapViewport vp = Viewport();
        (float rx, float ry) = vp.LatLonToScreen(TargetPosition.Lat, TargetPosition.Lon);

        ResolvedRadarCue? cue = new RadarCue(from.Callsign, null, resolved.Callsign, ShowBearingLabel: true).ResolveAgainst([from, resolved]);

        Assert.Equal(new ResolvedRadarCue(null, TargetPosition, null, null), cue);
        using SKBitmap bitmap = Render(vp, cue);
        List<SKColor> ring = [.. RingSamples(rx, ry).Select(point => PixelAt(bitmap, point))];
        Assert.Contains(ring, IsTeal);
        Assert.Contains(ring, IsBackground);
        Assert.DoesNotContain(bitmap.Pixels, IsHighlightBlue);
    }

    [Fact]
    public void BearingLabel_HiddenWhenShowBearingLabelFalse()
    {
        AircraftModel from = Airborne("N52TW", FromPosition);
        AircraftModel target = Airborne("SWA1", TargetPosition);
        MapViewport vp = Viewport();

        ResolvedRadarCue? cue = new RadarCue(from.Callsign, target.Callsign, null, ShowBearingLabel: false).ResolveAgainst([from, target]);

        Assert.NotNull(cue);
        Assert.Equal((FromPosition, (string?)null), (cue.From, cue.BearingLabel));
        using SKBitmap bitmap = Render(vp, cue);
        Assert.All(LabelBox(vp, bitmap), pixel => Assert.True(IsBackground(pixel), $"label pixel {pixel}"));
    }

    [Fact]
    public void NoCue_DrawsNothing()
    {
        using SKBitmap bitmap = Render(Viewport(), null);

        Assert.All(bitmap.Pixels, pixel => Assert.Equal(SKColors.Black, pixel));
    }

    private static MapViewport Viewport() =>
        new()
        {
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            Zoom = 0.6,
            PixelWidth = SurfacePx,
            PixelHeight = SurfacePx,
            RotationDeg = 0,
        };

    private static AircraftModel Airborne(string callsign, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            Heading = new TrueHeading(0),
            Altitude = 3000,
            IsOnGround = false,
        };

    private static SKBitmap Render(MapViewport vp, ResolvedRadarCue? cue)
    {
        var bitmap = new SKBitmap(SurfacePx, SurfacePx);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var renderer = new RadarRenderer();
        renderer.DrawMenuCue(canvas, vp, cue);
        return bitmap;
    }

    /// <summary>Points every 10° on a ring of <see cref="RadarRenderer.MenuCueRingRadiusPx"/> around the given centre.</summary>
    private static IEnumerable<SKPoint> RingSamples(float x, float y) =>
        Enumerable
            .Range(0, 36)
            .Select(i => i * 10 * MathF.PI / 180)
            .Select(a => new SKPoint(x + (RadarRenderer.MenuCueRingRadiusPx * MathF.Cos(a)), y + (RadarRenderer.MenuCueRingRadiusPx * MathF.Sin(a))));

    /// <summary>The pixels right of the bearing line's midpoint, where its label is written.</summary>
    private static List<SKColor> LabelBox(MapViewport vp, SKBitmap bitmap)
    {
        (float fx, float fy) = vp.LatLonToScreen(FromPosition.Lat, FromPosition.Lon);
        (float tx, float ty) = vp.LatLonToScreen(TargetPosition.Lat, TargetPosition.Lon);
        float mx = (fx + tx) / 2;
        float my = (fy + ty) / 2;
        return [.. Enumerable.Range((int)(mx + 6), 100).SelectMany(x => Enumerable.Range((int)(my - 8), 14).Select(y => bitmap.GetPixel(x, y)))];
    }

    /// <summary>
    /// Every point of the ring around the given centre is drawn in the highlight blue: the pixel at the point or one of its
    /// neighbours, since an antialiased 2 px stroke covers a pixel off its centre line only in part.
    /// </summary>
    private static void AssertSolidRing(SKBitmap bitmap, float x, float y) =>
        Assert.All(
            RingSamples(x, y),
            point =>
                Assert.True(
                    Enumerable
                        .Range(-1, 3)
                        .SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => new SKPoint(point.X + dx, point.Y + dy)))
                        .Any(near => IsHighlightBlue(PixelAt(bitmap, near))),
                    $"no highlight blue at or next to {point}"
                )
        );

    private static SKColor PixelAt(SKBitmap bitmap, SKPoint point) => bitmap.GetPixel((int)MathF.Round(point.X), (int)MathF.Round(point.Y));

    /// <summary>The cue's highlight blue (<c>#8fc1ff</c>), at full or partial coverage.</summary>
    private static bool IsHighlightBlue(SKColor pixel) => (pixel.Blue > 120) && (pixel.Blue > pixel.Green) && (pixel.Green > pixel.Red);

    /// <summary>The resolved aircraft's teal (<c>#7fd1b9</c>), at full or partial coverage.</summary>
    private static bool IsTeal(SKColor pixel) => (pixel.Green > 100) && (pixel.Green > pixel.Blue) && (pixel.Blue > pixel.Red);

    private static bool IsBackground(SKColor pixel) => (pixel.Red < 40) && (pixel.Green < 40) && (pixel.Blue < 40);
}
