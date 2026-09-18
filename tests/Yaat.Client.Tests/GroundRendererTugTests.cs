using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Faa;

namespace Yaat.Client.Tests;

/// <summary>
/// Renders the ground-view aircraft pass to an offscreen bitmap and probes the pixel where the tug body
/// must land: ahead of the nose gear, out along <see cref="AircraftModel.TowbarHeading"/>. Covers the
/// three contracts — drawn only when a towbar heading is present, placed on the towbar side of the nose
/// axis, and skipped entirely once the tug would be smaller than <see cref="GroundRenderer.MinTugPx"/>.
/// </summary>
public class GroundRendererTugTests
{
    private const double CenterLat = 37.62;
    private const double CenterLon = -122.38;
    private const int SurfacePx = 400;
    private const double FeetPerDegLat = 364_567.2;

    private static readonly SKColor BackgroundColor = SKColors.Black;

    [Fact]
    public void TowbarHeadingSet_PaintsTugBody()
    {
        MapViewport vp = Viewport(pxPerFt: 1.5f);
        AircraftModel withTug = Aircraft(headingDeg: 0, towbarHeadingDeg: 0);
        (float tx, float ty) = TugCenter(vp, withTug, towbarHeadingDeg: 0);

        using SKBitmap withTugBitmap = Render(vp, withTug);
        using SKBitmap withoutTugBitmap = Render(vp, Aircraft(headingDeg: 0, towbarHeadingDeg: null));

        Assert.NotEqual(BackgroundColor, PixelAt(withTugBitmap, tx, ty));
        Assert.Equal(BackgroundColor, PixelAt(withoutTugBitmap, tx, ty));
    }

    [Fact]
    public void TowbarHeadingLeftOfNose_PutsTugLeftOfTheNoseAxis()
    {
        MapViewport vp = Viewport(pxPerFt: 1.5f);
        AircraftModel ac = Aircraft(headingDeg: 0, towbarHeadingDeg: 315);
        (float tx, float ty) = TugCenter(vp, ac, towbarHeadingDeg: 315);
        (float noseX, float _) = vp.LatLonToScreen(CenterLat, CenterLon);

        using SKBitmap bitmap = Render(vp, ac);

        // Aircraft heading is north and the view is unrotated, so "left of the nose axis" is screen-left.
        Assert.True(tx < noseX, $"tug centre {tx:F1} should be left of the nose axis {noseX:F1}");
        Assert.NotEqual(BackgroundColor, PixelAt(bitmap, tx, ty));
        Assert.Equal(BackgroundColor, PixelAt(bitmap, noseX + (noseX - tx), ty));
    }

    [Fact]
    public void ZoomedOutBelowMinimumSize_SkipsTheTug()
    {
        const float pxPerFt = 0.2f;
        Assert.True(GroundRenderer.TugLengthFt * pxPerFt < GroundRenderer.MinTugPx, "test zoom must put the tug under the minimum size");

        MapViewport vp = Viewport(pxPerFt);
        AircraftModel ac = Aircraft(headingDeg: 0, towbarHeadingDeg: 90);
        (float tx, float ty) = TugCenter(vp, ac, towbarHeadingDeg: 90);

        using SKBitmap bitmap = Render(vp, ac);

        Assert.Equal(BackgroundColor, PixelAt(bitmap, tx, ty));
    }

    private static MapViewport Viewport(float pxPerFt) =>
        new()
        {
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            Zoom = pxPerFt * FeetPerDegLat / 5000.0,
            PixelWidth = SurfacePx,
            PixelHeight = SurfacePx,
            RotationDeg = 0,
        };

    private static AircraftModel Aircraft(double headingDeg, double? towbarHeadingDeg) =>
        new()
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(CenterLat, CenterLon),
            Heading = new TrueHeading(headingDeg),
            IsOnGround = true,
            TowbarHeading = towbarHeadingDeg.HasValue ? new TrueHeading(towbarHeadingDeg.Value) : null,
        };

    private static SKBitmap Render(MapViewport vp, AircraftModel ac)
    {
        var bitmap = new SKBitmap(SurfacePx, SurfacePx);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(BackgroundColor);
        using var renderer = new GroundRenderer();
        renderer.DrawAircraft(canvas, vp, [ac], selectedAircraft: null);
        return bitmap;
    }

    /// <summary>
    /// The screen point the tug body is centred on: the nose gear, then half a tug's length past the
    /// far end of the towbar. Built from the viewport geometry independently of the renderer's own path.
    /// </summary>
    private static (float X, float Y) TugCenter(MapViewport vp, AircraftModel ac, double towbarHeadingDeg)
    {
        float pxPerFt = (float)(vp.Zoom * 5000.0 / FeetPerDegLat);
        float lengthFt = GroundRenderer.AircraftLengthFt(FaaAircraftDatabase.Get(ac.AircraftType));
        float noseGearFt = (lengthFt * 0.5f) - (lengthFt * GroundRenderer.NoseGearSetbackFraction);
        float tugCenterFt = GroundRenderer.TowbarLengthFt + (GroundRenderer.TugLengthFt / 2f);

        (float sx, float sy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        (float nx, float ny) = Offset(sx, sy, (float)(ac.Heading.Degrees - vp.RotationDeg), noseGearFt * pxPerFt);
        return Offset(nx, ny, (float)(towbarHeadingDeg - vp.RotationDeg), tugCenterFt * pxPerFt);
    }

    private static (float X, float Y) Offset(float x, float y, float headingDeg, float distancePx)
    {
        float rad = (headingDeg - 90f) * MathF.PI / 180f;
        return (x + (MathF.Cos(rad) * distancePx), y + (MathF.Sin(rad) * distancePx));
    }

    private static SKColor PixelAt(SKBitmap bitmap, float x, float y) => bitmap.GetPixel((int)MathF.Round(x), (int)MathF.Round(y));
}
