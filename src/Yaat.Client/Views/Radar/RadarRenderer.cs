using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
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
    private static readonly SKColor FixColor = new(0, 140, 0);
    private readonly VideoMapRenderer _videoMapRenderer = new();
    private readonly TargetRenderer _targetRenderer = new();

    private float _rangeRingBrightness = 0.6f;

    private readonly SKPaint _rangeRingPaint = new()
    {
        Color = new SKColor(0, 100, 0, 153),
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
        Color = SKColors.White,
        TextSize = 14,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    private static readonly SKColor ProgrammedFixColor = new(0, 220, 200);

    private readonly SKPaint _programmedFixPaint = new()
    {
        Color = ProgrammedFixColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _programmedFixLabelPaint = new()
    {
        Color = ProgrammedFixColor,
        TextSize = 14,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    private static readonly SKColor RouteDrawColor = new(255, 200, 0);

    private readonly SKPaint _routeLinePaint = new()
    {
        Color = RouteDrawColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _rubberBandPaint = new()
    {
        Color = RouteDrawColor.WithAlpha(150),
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([6, 4], 0),
    };

    private readonly SKPaint _routeWaypointPaint = new()
    {
        Color = RouteDrawColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _routeLabelPaint = new()
    {
        Color = RouteDrawColor,
        TextSize = 13,
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

    public float RangeRingBrightness
    {
        get => _rangeRingBrightness;
        set
        {
            _rangeRingBrightness = value;
            _rangeRingPaint.Color = new SKColor(0, 100, 0, (byte)(value * 255));
        }
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
        double rangeRingSizeNm = 5,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets = null,
        string? hoveredFixName = null,
        double ptlLengthMinutes = 0,
        bool ptlOwn = false,
        bool ptlAll = false,
        IReadOnlySet<string>? programmedFixNames = null,
        IReadOnlyList<DrawnWaypoint>? drawnWaypoints = null,
        (double Lat, double Lon)? rubberBandTarget = null,
        string? rubberBandLabel = null
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
            DrawFixes(canvas, vp, fixes, hoveredFixName, programmedFixNames);
        }

        // Aircraft targets
        _targetRenderer.Render(canvas, vp, aircraft, selectedAircraft, dataBlockOffsets, ptlLengthMinutes, ptlOwn, ptlAll);

        // Drawn route overlay
        if (drawnWaypoints is { Count: > 0 })
        {
            DrawRouteOverlay(canvas, vp, drawnWaypoints, rubberBandTarget, rubberBandLabel);
        }
    }

    private void DrawRangeRing(SKCanvas canvas, MapViewport vp, double centerLat, double centerLon, double rangeRingSizeNm)
    {
        if (rangeRingSizeNm <= 0)
        {
            return;
        }

        var (cx, cy) = vp.LatLonToScreen(centerLat, centerLon);

        // Pixels-per-nm: convert one step to screen pixels
        var stepDeg = rangeRingSizeNm / 60.0;
        var (_, edgeY) = vp.LatLonToScreen(centerLat + stepDeg, centerLon);
        float stepPx = MathF.Abs(cy - edgeY);

        if (stepPx < 1)
        {
            return;
        }

        // Draw concentric circles until they exceed the visible viewport diagonal
        float maxExtent = MathF.Sqrt(vp.PixelWidth * vp.PixelWidth + vp.PixelHeight * vp.PixelHeight);
        float radiusPx = stepPx;

        while (radiusPx <= maxExtent)
        {
            canvas.DrawCircle(cx, cy, radiusPx, _rangeRingPaint);
            radiusPx += stepPx;
        }
    }

    private void DrawFixes(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<(string Name, double Lat, double Lon)> fixes,
        string? hoveredFixName,
        IReadOnlySet<string>? programmedFixNames
    )
    {
        const float crossSize = 4f;
        const float programmedCrossSize = 6f;
        const float margin = 50f;

        foreach (var fix in fixes)
        {
            var (sx, sy) = vp.LatLonToScreen(fix.Lat, fix.Lon);

            if (sx < -margin || sx > vp.PixelWidth + margin || sy < -margin || sy > vp.PixelHeight + margin)
            {
                continue;
            }

            bool isProgrammed = programmedFixNames is not null && programmedFixNames.Contains(fix.Name);
            var paint = isProgrammed ? _programmedFixPaint : _fixPaint;
            float size = isProgrammed ? programmedCrossSize : crossSize;

            canvas.DrawLine(sx - size, sy, sx + size, sy, paint);
            canvas.DrawLine(sx, sy - size, sx, sy + size, paint);

            if (isProgrammed || fix.Name == hoveredFixName)
            {
                var labelPaint = isProgrammed ? _programmedFixLabelPaint : _fixLabelPaint;
                canvas.DrawText(fix.Name, sx + 6, sy - 2, labelPaint);
            }
        }
    }

    private void DrawRouteOverlay(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<DrawnWaypoint> waypoints,
        (double Lat, double Lon)? rubberBandTarget,
        string? rubberBandLabel
    )
    {
        const float waypointSize = 5f;

        // Solid lines connecting consecutive waypoints
        for (int i = 1; i < waypoints.Count; i++)
        {
            var (x1, y1) = vp.LatLonToScreen(waypoints[i - 1].Lat, waypoints[i - 1].Lon);
            var (x2, y2) = vp.LatLonToScreen(waypoints[i].Lat, waypoints[i].Lon);
            canvas.DrawLine(x1, y1, x2, y2, _routeLinePaint);
        }

        // Rubber-band dashed line from last waypoint to cursor
        if (rubberBandTarget is { } target)
        {
            var last = waypoints[^1];
            var (lx, ly) = vp.LatLonToScreen(last.Lat, last.Lon);
            var (cx, cy) = vp.LatLonToScreen(target.Lat, target.Lon);
            canvas.DrawLine(lx, ly, cx, cy, _rubberBandPaint);

            if (rubberBandLabel is not null)
            {
                canvas.DrawText(rubberBandLabel, cx + 8, cy - 4, _routeLabelPaint);
            }
        }

        // Diamond markers + labels at each waypoint
        foreach (var wp in waypoints)
        {
            var (sx, sy) = vp.LatLonToScreen(wp.Lat, wp.Lon);

            using var path = new SKPath();
            path.MoveTo(sx, sy - waypointSize);
            path.LineTo(sx + waypointSize, sy);
            path.LineTo(sx, sy + waypointSize);
            path.LineTo(sx - waypointSize, sy);
            path.Close();
            canvas.DrawPath(path, _routeWaypointPaint);

            canvas.DrawText(wp.ResolvedName, sx + 8, sy - 2, _routeLabelPaint);
        }
    }

    public void Dispose()
    {
        _videoMapRenderer.Dispose();
        _targetRenderer.Dispose();
        _rangeRingPaint.Dispose();
        _fixPaint.Dispose();
        _fixLabelPaint.Dispose();
        _programmedFixPaint.Dispose();
        _programmedFixLabelPaint.Dispose();
        _routeLinePaint.Dispose();
        _rubberBandPaint.Dispose();
        _routeWaypointPaint.Dispose();
        _routeLabelPaint.Dispose();
    }
}
