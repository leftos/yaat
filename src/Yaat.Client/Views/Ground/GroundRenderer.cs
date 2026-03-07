using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Ground;

/// <summary>
/// Computed datablock geometry. Shared by renderer (draw) and canvas (hit-test).
/// </summary>
internal readonly struct DataBlockLayout
{
    private const float Pad = 3f;

    public readonly SKRect Rect;
    public readonly float TextX;
    public readonly float TextY;
    public readonly float LineHeight;
    public readonly string Line1;
    public readonly string Line2;
    public readonly string Line3;

    private DataBlockLayout(SKRect rect, float textX, float textY, float lineHeight, string line1, string line2, string line3)
    {
        Rect = rect;
        TextX = textX;
        TextY = textY;
        LineHeight = lineHeight;
        Line1 = line1;
        Line2 = line2;
        Line3 = line3;
    }

    public static DataBlockLayout Compute(AircraftModel ac, float screenX, float screenY, SKPoint offset, SKPaint textPaint, bool isAirborne)
    {
        float blockX = screenX + offset.X;
        float blockY = screenY + offset.Y;

        string line1 = ac.Callsign;
        string line2 = ac.AircraftType ?? "";
        string line3 = isAirborne ? $"{(int)(ac.Altitude / 100):D3}" : "";

        float w1 = textPaint.MeasureText(line1);
        float w2 = textPaint.MeasureText(line2);
        float w3 = line3.Length > 0 ? textPaint.MeasureText(line3) : 0;
        float textW = MathF.Max(w1, MathF.Max(w2, w3));
        float lineH = textPaint.TextSize + 2;
        int lineCount = line3.Length > 0 ? 3 : 2;

        var rect = new SKRect(blockX - Pad, blockY - textPaint.TextSize - Pad, blockX + textW + Pad, blockY + (lineCount - 1) * lineH + Pad);

        return new DataBlockLayout(rect, blockX, blockY, lineH, line1, line2, line3);
    }

    public static readonly SKPoint DefaultOffset = new(30, -25);
}

/// <summary>
/// Stateless SkiaSharp renderer for the airport ground layout.
/// All SKPaint objects are pre-allocated and reused.
/// Labels are collected during geometry passes and drawn last with overlap culling.
/// </summary>
public sealed class GroundRenderer : IDisposable
{
    private static readonly SKColor BackgroundColor = SKColor.Parse("#0e0e1a");
    private static readonly SKColor RunwayFillColor = new(60, 60, 60);
    private static readonly SKColor RunwayOutlineColor = new(100, 100, 100);
    private static readonly SKColor RunwayColor = new(120, 120, 120);
    private static readonly SKColor TaxiwayColor = new(200, 180, 60);
    private static readonly SKColor TaxiLabelColor = new(200, 180, 60, 160);
    private static readonly SKColor NodeIntersection = new(80, 120, 200);
    private static readonly SKColor NodeParking = new(60, 180, 80);
    private static readonly SKColor NodeHelipad = new(180, 60, 220);
    private static readonly SKColor NodeSpot = new(220, 160, 40);
    private static readonly SKColor NodeHoldShort = new(220, 60, 60);
    private static readonly SKColor ActiveRouteColor = new(60, 220, 60);
    private static readonly SKColor PreviewRouteColor = new(80, 180, 255, 180);
    private static readonly SKColor AircraftTaxiing = new(0, 200, 255);
    private static readonly SKColor AircraftHolding = new(255, 200, 0);
    private static readonly SKColor AircraftParked = new(100, 100, 100);
    private static readonly SKColor AircraftSelected = new(255, 255, 255);
    private static readonly SKColor AircraftDimmed = new(80, 80, 100);
    private static readonly SKColor AircraftAirborne = new(0, 200, 255);
    private static readonly SKColor HoverRingColor = new(255, 255, 255, 160);

    private const double AirborneMaxAglFt = 4000;
    private const double AirborneMaxRangeNm = 10;

    private enum LabelPriority
    {
        Runway,
        HoldShort,
        Taxiway,
        ParkingSpot,
    }

    private record struct LabelCandidate(string Text, float X, float Y, LabelPriority Priority, SKPaint Paint, SKColor? ColorOverride);

    private readonly SKPaint _runwayFillPaint = new()
    {
        Color = RunwayFillColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _runwayOutlinePaint = new()
    {
        Color = RunwayOutlineColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _runwayLabelPaint = new()
    {
        Color = new SKColor(180, 180, 180),
        TextSize = 15,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
        TextAlign = SKTextAlign.Center,
    };

    private readonly SKPaint _runwayPaint = new()
    {
        Color = RunwayColor,
        StrokeWidth = 6,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _taxiwayPaint = new()
    {
        Color = TaxiwayColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _taxiLabelPaint = new()
    {
        Color = TaxiLabelColor,
        TextSize = 13,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _activeRoutePaint = new()
    {
        Color = ActiveRouteColor,
        StrokeWidth = 4,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _previewRoutePaint = new()
    {
        Color = PreviewRouteColor,
        StrokeWidth = 5,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        PathEffect = SKPathEffect.CreateDash([10f, 6f], 0),
    };

    private readonly SKPaint _nodePaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private readonly SKPaint _nodeLabelPaint = new()
    {
        Color = new SKColor(200, 200, 200),
        TextSize = 12,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _aircraftPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private readonly SKPaint _dataBlockLeaderPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _dataBlockTextPaint = new()
    {
        TextSize = 12,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypefaceBold,
    };

    private readonly SKPaint _dataBlockBgPaint = new() { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Fill };

    private readonly SKPaint _hoverPaint = new()
    {
        Color = HoverRingColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _bgPaint = new() { Color = BackgroundColor, Style = SKPaintStyle.Fill };

    private readonly SKPaint _debugLabelPaint = new()
    {
        Color = new SKColor(255, 100, 255, 200),
        TextSize = 10,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _debugEdgeLabelPaint = new()
    {
        Color = new SKColor(100, 255, 100, 180),
        TextSize = 9,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypeface,
    };

    private readonly List<LabelCandidate> _labelCandidates = new(256);

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto? layout,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        int? hoveredNodeId,
        TaxiRoute? activeRoute,
        TaxiRoute? previewRoute,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double airportCenterLat = 0,
        double airportCenterLon = 0,
        double airportElevation = 0,
        bool showDebugInfo = false
    )
    {
        canvas.Clear(BackgroundColor);

        if (layout is null)
        {
            return;
        }

        _labelCandidates.Clear();

        DrawRunways(canvas, vp, layout);
        DrawEdges(canvas, vp, layout, showDebugInfo);
        DrawActiveRoute(canvas, vp, layout, activeRoute);
        DrawPreviewRoute(canvas, vp, layout, previewRoute);
        DrawNodes(canvas, vp, layout, hoveredNodeId, showDebugInfo);
        DrawLabels(canvas);
        DrawAircraft(canvas, vp, aircraft, selectedAircraft, airportCenterLat, airportCenterLon, airportElevation);
        DrawDataBlocks(canvas, vp, aircraft, selectedAircraft, dataBlockOffsets, airportCenterLat, airportCenterLon, airportElevation);
    }

    private void DrawRunways(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout)
    {
        if (layout.Runways is null)
        {
            return;
        }

        foreach (var rwy in layout.Runways)
        {
            if (rwy.Coordinates.Count < 2)
            {
                continue;
            }

            var first = rwy.Coordinates[0];
            var last = rwy.Coordinates[^1];
            if (first.Length < 2 || last.Length < 2)
            {
                continue;
            }

            double heading = GeoMath.BearingTo(first[0], first[1], last[0], last[1]);
            double halfWidthNm = (rwy.WidthFt / 2.0) / GeoMath.FeetPerNm;

            // Perpendicular angle (heading + 90)
            double perpRad = (heading + 90.0) * Math.PI / 180.0;
            double dLat = halfWidthNm / 60.0 * Math.Cos(perpRad);
            double dLon = halfWidthNm / 60.0 * Math.Sin(perpRad) / Math.Cos(first[0] * Math.PI / 180.0);

            // Build the 4 corners of the runway rectangle
            var (x1, y1) = vp.LatLonToScreen(first[0] + dLat, first[1] + dLon);
            var (x2, y2) = vp.LatLonToScreen(first[0] - dLat, first[1] - dLon);
            var (x3, y3) = vp.LatLonToScreen(last[0] - dLat, last[1] - dLon);
            var (x4, y4) = vp.LatLonToScreen(last[0] + dLat, last[1] + dLon);

            using var path = new SKPath();
            path.MoveTo(x1, y1);
            path.LineTo(x2, y2);
            path.LineTo(x3, y3);
            path.LineTo(x4, y4);
            path.Close();

            canvas.DrawPath(path, _runwayFillPaint);
            canvas.DrawPath(path, _runwayOutlinePaint);

            // Draw centerline dashed
            var (cx1, cy1) = vp.LatLonToScreen(first[0], first[1]);
            var (cx2, cy2) = vp.LatLonToScreen(last[0], last[1]);
            canvas.DrawLine(cx1, cy1, cx2, cy2, _runwayPaint);

            // Runway label at midpoint
            double midLat = (first[0] + last[0]) / 2.0;
            double midLon = (first[1] + last[1]) / 2.0;
            var (mx, my) = vp.LatLonToScreen(midLat, midLon);

            string label = rwy.Name.Replace(" - ", "/");
            _labelCandidates.Add(new LabelCandidate(label, mx, my + 4, LabelPriority.Runway, _runwayLabelPaint, null));
        }
    }

    private void DrawEdges(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, bool showDebugInfo)
    {
        var nodeScreenPos = new Dictionary<int, (float X, float Y)>(layout.Nodes.Count);
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        // Track placed taxiway label positions for deduplication
        var taxiLabelPositions = new Dictionary<string, List<(float X, float Y)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in layout.Edges)
        {
            if (!nodeScreenPos.TryGetValue(edge.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            bool isRunway = edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);
            var paint = isRunway ? _runwayPaint : _taxiwayPaint;

            if (edge.IntermediatePoints is { Count: > 0 })
            {
                using var path = new SKPath();
                path.MoveTo(from.X, from.Y);
                foreach (var pt in edge.IntermediatePoints)
                {
                    if (pt.Length >= 2)
                    {
                        var (sx, sy) = vp.LatLonToScreen(pt[0], pt[1]);
                        path.LineTo(sx, sy);
                    }
                }

                path.LineTo(to.X, to.Y);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
            }

            if (showDebugInfo)
            {
                var mx = (from.X + to.X) / 2f;
                var my = (from.Y + to.Y) / 2f;
                string debugLabel = $"{edge.TaxiwayName} {edge.FromNodeId}-{edge.ToNodeId}";
                canvas.DrawText(debugLabel, mx + 2, my + 4, _debugEdgeLabelPaint);
            }
            else if (!isRunway)
            {
                var mx = (from.X + to.X) / 2f;
                var my = (from.Y + to.Y) / 2f;

                // Skip if too close to an existing label for the same taxiway
                if (IsTaxiLabelTooClose(taxiLabelPositions, edge.TaxiwayName, mx, my))
                {
                    continue;
                }

                if (!taxiLabelPositions.TryGetValue(edge.TaxiwayName, out var positions))
                {
                    positions = [];
                    taxiLabelPositions[edge.TaxiwayName] = positions;
                }

                positions.Add((mx, my));
                _labelCandidates.Add(new LabelCandidate(edge.TaxiwayName, mx + 3, my - 3, LabelPriority.Taxiway, _taxiLabelPaint, null));
            }
        }
    }

    private static bool IsTaxiLabelTooClose(Dictionary<string, List<(float X, float Y)>> positions, string name, float x, float y)
    {
        const float minDistSq = 100f * 100f;

        if (!positions.TryGetValue(name, out var existing))
        {
            return false;
        }

        foreach (var (ex, ey) in existing)
        {
            float dx = x - ex;
            float dy = y - ey;
            if (dx * dx + dy * dy < minDistSq)
            {
                return true;
            }
        }

        return false;
    }

    private void DrawActiveRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? route)
    {
        DrawRoute(canvas, vp, layout, route, _activeRoutePaint);
    }

    private void DrawPreviewRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? route)
    {
        DrawRoute(canvas, vp, layout, route, _previewRoutePaint);
    }

    private static void DrawRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? route, SKPaint paint)
    {
        if (route is null)
        {
            return;
        }

        var nodeScreenPos = new Dictionary<int, (float X, float Y)>();
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        foreach (var seg in route.Segments)
        {
            if (!nodeScreenPos.TryGetValue(seg.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(seg.ToNodeId, out var to))
            {
                continue;
            }

            if (seg.Edge.IntermediatePoints.Count > 0)
            {
                using var path = new SKPath();
                path.MoveTo(from.X, from.Y);
                foreach (var (lat, lon) in seg.Edge.IntermediatePoints)
                {
                    var (sx, sy) = vp.LatLonToScreen(lat, lon);
                    path.LineTo(sx, sy);
                }

                path.LineTo(to.X, to.Y);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
            }
        }
    }

    private void DrawNodes(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, int? hoveredNodeId, bool showDebugInfo)
    {
        foreach (var node in layout.Nodes)
        {
            var (sx, sy) = vp.LatLonToScreen(node.Latitude, node.Longitude);

            _nodePaint.Color = node.Type switch
            {
                "Parking" => NodeParking,
                "Helipad" => NodeHelipad,
                "Spot" => NodeSpot,
                "RunwayHoldShort" => NodeHoldShort,
                _ => NodeIntersection,
            };

            float radius = node.Type switch
            {
                "Parking" => 4f,
                "Helipad" => 5f,
                "RunwayHoldShort" => 3.5f,
                _ => 2.5f,
            };

            canvas.DrawCircle(sx, sy, radius, _nodePaint);

            // Draw "H" marker on helipads
            if (node.Type == "Helipad")
            {
                _nodeLabelPaint.Color = NodeHelipad;
                canvas.DrawText("H", sx - 3, sy + 3, _nodeLabelPaint);
            }

            if (showDebugInfo)
            {
                string debugLabel = node.Name is not null ? $"{node.Id} {node.Name} ({node.Type})" : $"{node.Id} ({node.Type})";
                canvas.DrawText(debugLabel, sx + 5, sy - 3, _debugLabelPaint);
            }
            else if (node.Name is not null && node.Type is "Parking" or "Helipad" or "Spot")
            {
                _labelCandidates.Add(new LabelCandidate(node.Name, sx + 5, sy - 3, LabelPriority.ParkingSpot, _nodeLabelPaint, null));
            }
            else if (node.RunwayId is not null && node.Type == "RunwayHoldShort")
            {
                _labelCandidates.Add(new LabelCandidate($"HS {node.RunwayId}", sx + 5, sy - 3, LabelPriority.HoldShort, _nodeLabelPaint, null));
            }

            if (hoveredNodeId == node.Id)
            {
                canvas.DrawCircle(sx, sy, 8f, _hoverPaint);
            }
        }
    }

    private void DrawAircraft(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation
    )
    {
        foreach (var ac in aircraft)
        {
            if (!ac.IsOnGround && !IsAirborneVisible(ac, airportCenterLat, airportCenterLon, airportElevation))
            {
                continue;
            }

            var (sx, sy) = vp.LatLonToScreen(ac.Latitude, ac.Longitude);
            bool isSelected = ac == selectedAircraft;
            bool isAirborne = !ac.IsOnGround;

            _aircraftPaint.Color =
                isSelected ? AircraftSelected
                : isAirborne ? AircraftAirborne
                : GetAircraftColor(ac);

            DrawTriangle(canvas, sx, sy, (float)ac.Heading, isSelected ? 19f : 16f, _aircraftPaint);
        }
    }

    private static SKColor GetAircraftColor(AircraftModel ac)
    {
        return ac.CurrentPhase switch
        {
            "AtParking" or "Pushback" => AircraftParked,
            "HoldingShort" or "HoldingAfterExit" => AircraftHolding,
            _ => AircraftTaxiing,
        };
    }

    private static void DrawTriangle(SKCanvas canvas, float cx, float cy, float headingDeg, float size, SKPaint paint)
    {
        var rad = (headingDeg - 90) * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);

        var tip = new SKPoint(cx + cos * size, cy + sin * size);
        var left = new SKPoint(cx + MathF.Cos(rad + 2.5f) * size * 0.6f, cy + MathF.Sin(rad + 2.5f) * size * 0.6f);
        var right = new SKPoint(cx + MathF.Cos(rad - 2.5f) * size * 0.6f, cy + MathF.Sin(rad - 2.5f) * size * 0.6f);

        using var path = new SKPath();
        path.MoveTo(tip);
        path.LineTo(left);
        path.LineTo(right);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private void DrawDataBlocks(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation
    )
    {
        foreach (var ac in aircraft)
        {
            bool isAirborne = !ac.IsOnGround;
            if (isAirborne && !IsAirborneVisible(ac, airportCenterLat, airportCenterLon, airportElevation))
            {
                continue;
            }

            var (sx, sy) = vp.LatLonToScreen(ac.Latitude, ac.Longitude);

            SKPoint offset = DataBlockLayout.DefaultOffset;
            if (dataBlockOffsets is not null && dataBlockOffsets.TryGetValue(ac.Callsign, out var customOffset))
            {
                offset = customOffset;
            }

            var layout = DataBlockLayout.Compute(ac, sx, sy, offset, _dataBlockTextPaint, isAirborne);

            bool isSelected = ac == selectedAircraft;
            var color =
                isSelected ? AircraftSelected
                : isAirborne ? AircraftAirborne
                : GetAircraftColor(ac);

            _dataBlockTextPaint.Color = color;
            canvas.DrawRect(layout.Rect, _dataBlockBgPaint);

            if (isSelected)
            {
                _dataBlockLeaderPaint.Color = AircraftSelected;
                canvas.DrawRect(layout.Rect, _dataBlockLeaderPaint);
            }

            var leaderEnd = ClampToBlockEdge(sx, sy, layout.Rect);
            _dataBlockLeaderPaint.Color = color;
            canvas.DrawLine(sx, sy, leaderEnd.X, leaderEnd.Y, _dataBlockLeaderPaint);

            canvas.DrawText(layout.Line1, layout.TextX, layout.TextY, _dataBlockTextPaint);
            canvas.DrawText(layout.Line2, layout.TextX, layout.TextY + layout.LineHeight, _dataBlockTextPaint);
            if (layout.Line3.Length > 0)
            {
                canvas.DrawText(layout.Line3, layout.TextX, layout.TextY + layout.LineHeight * 2, _dataBlockTextPaint);
            }
        }
    }

    private static bool IsAirborneVisible(AircraftModel ac, double airportCenterLat, double airportCenterLon, double airportElevation)
    {
        double agl = ac.Altitude - airportElevation;
        if (agl <= 0 || agl > AirborneMaxAglFt)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, airportCenterLat, airportCenterLon);
        return dist <= AirborneMaxRangeNm;
    }

    private static SKPoint ClampToBlockEdge(float pointX, float pointY, SKRect rect)
    {
        if (rect.Contains(pointX, pointY))
        {
            return new SKPoint(pointX, pointY);
        }

        return new SKPoint(Math.Clamp(pointX, rect.Left, rect.Right), Math.Clamp(pointY, rect.Top, rect.Bottom));
    }

    private void DrawLabels(SKCanvas canvas)
    {
        _labelCandidates.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var placedRects = new List<SKRect>(_labelCandidates.Count);

        foreach (var label in _labelCandidates)
        {
            var paint = label.Paint;
            float textWidth = paint.MeasureText(label.Text);
            float textHeight = paint.TextSize;

            float left = paint.TextAlign == SKTextAlign.Center ? label.X - textWidth / 2f - 2 : label.X - 2;
            var rect = new SKRect(left, label.Y - textHeight - 1, left + textWidth + 4, label.Y + 1);

            bool overlaps = false;
            foreach (var placed in placedRects)
            {
                if (rect.IntersectsWith(placed))
                {
                    overlaps = true;
                    break;
                }
            }

            if (overlaps)
            {
                continue;
            }

            placedRects.Add(rect);

            if (label.ColorOverride is { } color)
            {
                paint.Color = color;
            }

            canvas.DrawText(label.Text, label.X, label.Y, paint);
        }
    }

    public void Dispose()
    {
        _runwayFillPaint.Dispose();
        _runwayOutlinePaint.Dispose();
        _runwayLabelPaint.Dispose();
        _runwayPaint.Dispose();
        _taxiwayPaint.Dispose();
        _taxiLabelPaint.Dispose();
        _activeRoutePaint.Dispose();
        _previewRoutePaint.Dispose();
        _nodePaint.Dispose();
        _nodeLabelPaint.Dispose();
        _aircraftPaint.Dispose();
        _hoverPaint.Dispose();
        _dataBlockLeaderPaint.Dispose();
        _dataBlockTextPaint.Dispose();
        _dataBlockBgPaint.Dispose();
        _bgPaint.Dispose();
        _debugLabelPaint.Dispose();
        _debugEdgeLabelPaint.Dispose();
    }
}
