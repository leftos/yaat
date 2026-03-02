using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Stateless STARS-style radar renderer. Coordinates sub-renderers
/// for video maps, targets, and overlays.
/// </summary>
public sealed class RadarRenderer : IDisposable
{
    private static readonly SKColor BackgroundColor = SKColors.Black;
    private static readonly SKColor RangeRingColor = new(0, 100, 0);
    private static readonly SKColor FixColor = new(0, 140, 0);
    private readonly VideoMapRenderer _videoMapRenderer = new();
    private readonly TargetRenderer _targetRenderer = new();

    private readonly SKPaint _rangeRingPaint = new()
    {
        Color = RangeRingColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _fixPaint = new()
    {
        Color = FixColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _fixLabelPaint = new()
    {
        Color = FixColor,
        TextSize = 10,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    public float BrightnessA
    {
        get => _videoMapRenderer.BrightnessA;
        set => _videoMapRenderer.BrightnessA = value;
    }

    public float BrightnessB
    {
        get => _videoMapRenderer.BrightnessB;
        set => _videoMapRenderer.BrightnessB = value;
    }

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<VideoMapData> videoMaps,
        IReadOnlyDictionary<string, string> brightnessLookup,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        bool showRangeRings,
        double rangeNm,
        double centerLat,
        double centerLon,
        bool showFixes,
        IReadOnlyList<(string Name, double Lat, double Lon)>? fixes,
        double rangeRingCenterLat = 0,
        double rangeRingCenterLon = 0,
        double rangeRingSizeNm = 5
    )
    {
        canvas.Clear(BackgroundColor);

        // Video maps
        _videoMapRenderer.Render(canvas, vp, videoMaps, brightnessLookup);

        // Range rings
        if (showRangeRings)
        {
            // Range ring at dedicated center + size
            var rrLat = rangeRingCenterLat != 0 ? rangeRingCenterLat : centerLat;
            var rrLon = rangeRingCenterLon != 0 ? rangeRingCenterLon : centerLon;
            DrawRangeRing(canvas, vp, rrLat, rrLon, rangeRingSizeNm);
        }

        // Fix overlay
        if (showFixes && fixes is not null)
        {
            DrawFixes(canvas, vp, fixes);
        }

        // Aircraft targets
        _targetRenderer.Render(canvas, vp, aircraft, selectedAircraft);
    }

    private void DrawRangeRing(SKCanvas canvas, MapViewport vp, double centerLat, double centerLon, double rangeRingSizeNm)
    {
        if (rangeRingSizeNm <= 0)
        {
            return;
        }

        var (cx, cy) = vp.LatLonToScreen(centerLat, centerLon);

        // Convert nautical miles to approximate screen pixels
        // 1 nm = 1/60 degree latitude
        var degreesLat = rangeRingSizeNm / 60.0;
        var (_, edgeY) = vp.LatLonToScreen(centerLat + degreesLat, centerLon);
        float radiusPx = MathF.Abs(cy - edgeY);

        canvas.DrawCircle(cx, cy, radiusPx, _rangeRingPaint);
    }

    private void DrawFixes(SKCanvas canvas, MapViewport vp, IReadOnlyList<(string Name, double Lat, double Lon)> fixes)
    {
        const float crossSize = 4f;

        foreach (var fix in fixes)
        {
            var (sx, sy) = vp.LatLonToScreen(fix.Lat, fix.Lon);

            // Draw small + symbol
            canvas.DrawLine(sx - crossSize, sy, sx + crossSize, sy, _fixPaint);
            canvas.DrawLine(sx, sy - crossSize, sx, sy + crossSize, _fixPaint);

            // Label
            canvas.DrawText(fix.Name, sx + 6, sy - 2, _fixLabelPaint);
        }
    }

    public void Dispose()
    {
        _videoMapRenderer.Dispose();
        _targetRenderer.Dispose();
        _rangeRingPaint.Dispose();
        _fixPaint.Dispose();
        _fixLabelPaint.Dispose();
    }
}
