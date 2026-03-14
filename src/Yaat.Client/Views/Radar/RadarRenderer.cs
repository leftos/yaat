using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
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
    private static readonly SKColor FixColor = new(100, 100, 100);
    private readonly VideoMapRenderer _videoMapRenderer = new();
    private readonly TargetRenderer _targetRenderer = new();

    private float _rangeRingBrightness = 0.6f;

    private readonly SKPaint _rangeRingPaint = new()
    {
        Color = new SKColor(100, 100, 100, 153),
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
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
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
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
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
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _routeConditionLabelPaint = new()
    {
        Color = new SKColor(255, 200, 0, 180),
        TextSize = 11,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
    };

    // Pre-allocated paints for shown flight paths (one set per palette color)
    private readonly SKPaint[] _pathLinePaints;
    private readonly SKPaint[] _pathWaypointPaints;
    private readonly SKPaint[] _pathLabelPaints;
    private readonly SKPaint[] _pathLeaderPaints;

    public RadarRenderer()
    {
        var colors = new[]
        {
            SKColor.Parse("#FF6B6B"),
            SKColor.Parse("#4ECDC4"),
            SKColor.Parse("#FFE66D"),
            SKColor.Parse("#A8E6CF"),
            SKColor.Parse("#FF8B94"),
            SKColor.Parse("#B088F9"),
            SKColor.Parse("#F8B500"),
            SKColor.Parse("#45B7D1"),
        };

        _pathLinePaints = new SKPaint[colors.Length];
        _pathWaypointPaints = new SKPaint[colors.Length];
        _pathLabelPaints = new SKPaint[colors.Length];
        _pathLeaderPaints = new SKPaint[colors.Length];

        for (int i = 0; i < colors.Length; i++)
        {
            _pathLinePaints[i] = new SKPaint
            {
                Color = colors[i],
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            _pathWaypointPaints[i] = new SKPaint
            {
                Color = colors[i],
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            _pathLabelPaints[i] = new SKPaint
            {
                Color = colors[i],
                TextSize = 12,
                IsAntialias = true,
                SubpixelText = true,
                Typeface = PlatformHelper.MonospaceTypeface,
            };
            _pathLeaderPaints[i] = new SKPaint
            {
                Color = colors[i].WithAlpha(150),
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash([6, 4], 0),
            };
        }
    }

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

    public string? LocalUserInitials
    {
        get => _targetRenderer.LocalUserInitials;
        set => _targetRenderer.LocalUserInitials = value;
    }

    public SKColor? AssignmentTintColor
    {
        get => _targetRenderer.AssignmentTintColor;
        set => _targetRenderer.AssignmentTintColor = value;
    }

    public SKColor? UnassignedTintColor
    {
        get => _targetRenderer.UnassignedTintColor;
        set => _targetRenderer.UnassignedTintColor = value;
    }

    public SKColor? SelectedOverrideColor
    {
        get => _targetRenderer.SelectedOverrideColor;
        set => _targetRenderer.SelectedOverrideColor = value;
    }

    public float RangeRingBrightness
    {
        get => _rangeRingBrightness;
        set
        {
            _rangeRingBrightness = value;
            _rangeRingPaint.Color = new SKColor(100, 100, 100, (byte)(value * 255));
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
        (double Lat, double Lon)? drawRouteOrigin = null,
        (double Lat, double Lon)? rubberBandTarget = null,
        string? rubberBandLabel = null,
        IReadOnlyDictionary<int, WaypointCondition>? waypointConditions = null,
        IReadOnlySet<string>? minifiedCallsigns = null,
        bool showTopDown = false,
        IReadOnlyList<WeatherDisplayInfo>? weatherInfo = null,
        IReadOnlyList<ShownPathEntry>? shownPaths = null
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

        // Shown flight paths (behind aircraft)
        if (shownPaths is { Count: > 0 })
        {
            DrawShownPaths(canvas, vp, shownPaths);
        }

        // Aircraft targets
        _targetRenderer.Render(
            canvas,
            vp,
            aircraft,
            selectedAircraft,
            dataBlockOffsets,
            ptlLengthMinutes,
            ptlOwn,
            ptlAll,
            minifiedCallsigns,
            showTopDown
        );

        // Drawn route overlay
        if (drawnWaypoints is { Count: > 0 })
        {
            DrawRouteOverlay(canvas, vp, drawnWaypoints, drawRouteOrigin, rubberBandTarget, rubberBandLabel, waypointConditions);
        }
        else if (drawRouteOrigin is { } origin && rubberBandTarget is { } target)
        {
            DrawRubberBandFromOrigin(canvas, vp, origin, target, rubberBandLabel);
        }

        // Weather overlay (top-left)
        if (weatherInfo is { Count: > 0 })
        {
            DrawWeatherOverlay(canvas, weatherInfo);
        }
    }

    private void DrawShownPaths(SKCanvas canvas, MapViewport vp, IReadOnlyList<ShownPathEntry> entries)
    {
        const float diamondSize = 5f;

        foreach (var entry in entries)
        {
            if (entry.Waypoints.Count == 0)
            {
                continue;
            }

            // Find paint set index by matching color
            int paintIdx = 0;
            for (int i = 0; i < _pathLinePaints.Length; i++)
            {
                if (_pathLinePaints[i].Color == entry.Color)
                {
                    paintIdx = i;
                    break;
                }
            }

            var linePaint = _pathLinePaints[paintIdx];
            var wpPaint = _pathWaypointPaints[paintIdx];
            var labelPaint = _pathLabelPaints[paintIdx];
            var leaderPaint = _pathLeaderPaints[paintIdx];

            // Dashed leader from aircraft to first waypoint
            var (ax, ay) = vp.LatLonToScreen(entry.AircraftLat, entry.AircraftLon);
            var (fx, fy) = vp.LatLonToScreen(entry.Waypoints[0].Lat, entry.Waypoints[0].Lon);
            canvas.DrawLine(ax, ay, fx, fy, leaderPaint);

            // Solid polyline connecting waypoints
            for (int i = 1; i < entry.Waypoints.Count; i++)
            {
                var (x1, y1) = vp.LatLonToScreen(entry.Waypoints[i - 1].Lat, entry.Waypoints[i - 1].Lon);
                var (x2, y2) = vp.LatLonToScreen(entry.Waypoints[i].Lat, entry.Waypoints[i].Lon);
                canvas.DrawLine(x1, y1, x2, y2, linePaint);
            }

            // Diamond markers + fix name labels
            foreach (var wp in entry.Waypoints)
            {
                var (wx, wy) = vp.LatLonToScreen(wp.Lat, wp.Lon);
                canvas.DrawLine(wx, wy - diamondSize, wx + diamondSize, wy, wpPaint);
                canvas.DrawLine(wx + diamondSize, wy, wx, wy + diamondSize, wpPaint);
                canvas.DrawLine(wx, wy + diamondSize, wx - diamondSize, wy, wpPaint);
                canvas.DrawLine(wx - diamondSize, wy, wx, wy - diamondSize, wpPaint);
                canvas.DrawText(wp.ResolvedName, wx + 8, wy - 4, labelPaint);
            }
        }
    }

    private static void DrawWeatherOverlay(SKCanvas canvas, IReadOnlyList<WeatherDisplayInfo> stations)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0x00, 0xC8, 0x00), // STARS green
            TextSize = 14,
            Typeface = Services.PlatformHelper.MonospaceTypeface,
            IsAntialias = true,
        };

        float y = 20;
        const float lineHeight = 18;
        foreach (var station in stations)
        {
            canvas.DrawText(station.ToDisplayString(), 10, y, paint);
            y += lineHeight;
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
        (double Lat, double Lon)? originLatLon,
        (double Lat, double Lon)? rubberBandTarget,
        string? rubberBandLabel,
        IReadOnlyDictionary<int, WaypointCondition>? waypointConditions = null
    )
    {
        const float waypointSize = 5f;

        // Dashed line from aircraft position to first waypoint
        if (originLatLon is { } origin)
        {
            var (ox, oy) = vp.LatLonToScreen(origin.Lat, origin.Lon);
            var (fx, fy) = vp.LatLonToScreen(waypoints[0].Lat, waypoints[0].Lon);
            canvas.DrawLine(ox, oy, fx, fy, _rubberBandPaint);
        }

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
        for (int i = 0; i < waypoints.Count; i++)
        {
            var wp = waypoints[i];
            var (sx, sy) = vp.LatLonToScreen(wp.Lat, wp.Lon);

            using var path = new SKPath();
            path.MoveTo(sx, sy - waypointSize);
            path.LineTo(sx + waypointSize, sy);
            path.LineTo(sx, sy + waypointSize);
            path.LineTo(sx - waypointSize, sy);
            path.Close();
            canvas.DrawPath(path, _routeWaypointPaint);

            canvas.DrawText(wp.ResolvedName, sx + 8, sy - 2, _routeLabelPaint);

            // Show condition summary below the fix name
            if (waypointConditions is not null && waypointConditions.TryGetValue(i, out var cond))
            {
                var summary = cond.ToSummary();
                if (summary.Length > 0)
                {
                    canvas.DrawText(summary, sx + 8, sy + 11, _routeConditionLabelPaint);
                }
            }
        }
    }

    private void DrawRubberBandFromOrigin(
        SKCanvas canvas,
        MapViewport vp,
        (double Lat, double Lon) origin,
        (double Lat, double Lon) target,
        string? label
    )
    {
        var (ox, oy) = vp.LatLonToScreen(origin.Lat, origin.Lon);
        var (cx, cy) = vp.LatLonToScreen(target.Lat, target.Lon);
        canvas.DrawLine(ox, oy, cx, cy, _rubberBandPaint);

        if (label is not null)
        {
            canvas.DrawText(label, cx + 8, cy - 4, _routeLabelPaint);
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
        _routeConditionLabelPaint.Dispose();

        for (int i = 0; i < _pathLinePaints.Length; i++)
        {
            _pathLinePaints[i].Dispose();
            _pathWaypointPaints[i].Dispose();
            _pathLabelPaints[i].Dispose();
            _pathLeaderPaints[i].Dispose();
        }
    }
}
