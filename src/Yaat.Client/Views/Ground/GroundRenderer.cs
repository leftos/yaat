using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;

namespace Yaat.Client.Views.Ground;

/// <summary>
/// Tri-state filter for ground view elements: both icon+label, icon only, or fully hidden.
/// </summary>
public enum GroundFilterMode
{
    LabelsAndIcons,
    IconsOnly,
    Off,
}

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
        string line2 = ac.AircraftType;
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
    private static readonly SKColor TaxiLabelColor = new(230, 210, 80, 200);
    private static readonly SKColor NodeIntersection = new(80, 120, 200, 100);
    private static readonly SKColor NodeParking = new(60, 180, 80, 140);
    private static readonly SKColor NodeHelipad = new(180, 60, 220, 140);
    private static readonly SKColor NodeSpot = new(220, 160, 40, 140);
    private static readonly SKColor NodeHoldShort = new(220, 200, 60, 180);
    private static readonly SKColor ActiveRouteColor = new(60, 220, 60);
    private static readonly SKColor PreviewRouteColor = new(80, 180, 255, 180);
    private static readonly SKColor AircraftTaxiing = new(255, 255, 255);
    private static readonly SKColor AircraftSelected = new(255, 255, 255);
    private static readonly SKColor AircraftAirborne = new(255, 255, 255);
    private static readonly SKColor TerminalGreen = new(0, 230, 0);
    private static readonly SKColor DrawnRouteColor = new(0, 200, 255);
    private static readonly SKColor DrawHoverPreviewColor = new(255, 180, 50);
    private static readonly SKColor WaypointMarkerColor = new(255, 200, 0);
    private static readonly SKColor HoverRingColor = new(255, 255, 255, 160);

    private const double AirborneMaxAglFt = 4000;
    private const double AirborneMaxRangeNm = 10;

    private enum LabelPriority
    {
        Hovered,
        Runway,
        HoldShort,
        Taxiway,
        ParkingSpot,
    }

    private record struct LabelCandidate(string[] Lines, float X, float Y, LabelPriority Priority, SKPaint Paint, SKColor? ColorOverride);

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
        Typeface = PlatformHelper.MonospaceTypeface,
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
        Typeface = PlatformHelper.MonospaceTypeface,
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

    private readonly SKPaint _holdShortBarPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2f,
        IsAntialias = true,
    };

    private readonly SKPaint _nodeLabelPaint = new()
    {
        Color = new SKColor(200, 200, 200),
        TextSize = 12,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
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
        Typeface = PlatformHelper.MonospaceTypefaceBold,
    };

    private readonly SKPaint _dataBlockBgPaint = new() { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Fill };
    private readonly SKPaint _labelBgPaint = new() { Color = new SKColor(0, 0, 0, 220), Style = SKPaintStyle.Fill };

    private readonly SKPaint _drawnRoutePaint = new()
    {
        Color = DrawnRouteColor,
        StrokeWidth = 5,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _drawHoverPreviewPaint = new()
    {
        Color = DrawHoverPreviewColor,
        StrokeWidth = 5,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _waypointMarkerPaint = new()
    {
        Color = WaypointMarkerColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _waypointTextPaint = new()
    {
        Color = SKColors.Black,
        TextSize = 10,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypefaceBold,
        TextAlign = SKTextAlign.Center,
    };

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
        TextSize = 14,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _debugEdgeLabelPaint = new()
    {
        Color = new SKColor(100, 255, 100, 180),
        TextSize = 13,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint[] _shownTaxiRoutePaints;

    private readonly List<LabelCandidate> _labelCandidates = new(256);

    public GroundRenderer()
    {
        _shownTaxiRoutePaints = new SKPaint[TaxiRouteColorValues.Length];
        for (int i = 0; i < TaxiRouteColorValues.Length; i++)
        {
            _shownTaxiRoutePaints[i] = new SKPaint
            {
                Color = TaxiRouteColorValues[i],
                StrokeWidth = 3,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
            };
        }
    }

    private static readonly SKColor[] TaxiRouteColorValues =
    [
        SKColor.Parse("#FF6B6B"),
        SKColor.Parse("#4ECDC4"),
        SKColor.Parse("#FFE66D"),
        SKColor.Parse("#A8E6CF"),
        SKColor.Parse("#FF8B94"),
        SKColor.Parse("#B088F9"),
        SKColor.Parse("#F8B500"),
        SKColor.Parse("#45B7D1"),
    ];

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto? layout,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        int? hoveredNodeId,
        TaxiRoute? activeRoute,
        TaxiRoute? previewRoute,
        TaxiRoute? drawnRoutePreview,
        TaxiRoute? drawHoverPreview,
        IReadOnlyList<int>? drawWaypoints,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation,
        bool showDebugInfo,
        WeatherDisplayInfo? weatherInfo,
        bool showRunwayLabels,
        bool showTaxiwayLabels,
        GroundFilterMode showHoldShort,
        GroundFilterMode showParking,
        GroundFilterMode showSpot,
        IReadOnlyList<ShownTaxiRouteEntry>? shownTaxiRoutes
    )
    {
        canvas.Clear(BackgroundColor);

        if (layout is null)
        {
            return;
        }

        _labelCandidates.Clear();

        DrawRunways(canvas, vp, layout, showRunwayLabels);
        DrawEdges(canvas, vp, layout, showDebugInfo, showTaxiwayLabels);
        DrawActiveRoute(canvas, vp, layout, activeRoute);
        DrawPreviewRoute(canvas, vp, layout, previewRoute);
        DrawShownTaxiRoutes(canvas, vp, layout, shownTaxiRoutes);
        DrawDrawnRoute(canvas, vp, layout, drawnRoutePreview, drawWaypoints);
        DrawDrawHoverPreview(canvas, vp, layout, drawHoverPreview);
        DrawNodes(canvas, vp, layout, hoveredNodeId, showDebugInfo, showHoldShort, showParking, showSpot);
        DrawLabels(canvas, hoveredOnly: false);
        DrawAircraft(canvas, vp, aircraft, selectedAircraft, airportCenterLat, airportCenterLon, airportElevation);
        DrawDataBlocks(canvas, vp, aircraft, selectedAircraft, dataBlockOffsets, airportCenterLat, airportCenterLon, airportElevation);
        DrawLabels(canvas, hoveredOnly: true);

        if (showDebugInfo)
        {
            DrawDebugOverlay(canvas, vp, layout);
        }

        if (weatherInfo is not null)
        {
            DrawWeatherOverlay(canvas, weatherInfo);
        }
    }

    private static void DrawWeatherOverlay(SKCanvas canvas, WeatherDisplayInfo info)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0xCC, 0xCC, 0xCC), // light gray
            TextSize = 14,
            Typeface = PlatformHelper.MonospaceTypeface,
            IsAntialias = true,
        };

        canvas.DrawText(info.ToDisplayString(), 10, 20, paint);
    }

    private void DrawDebugOverlay(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout)
    {
        var nodeScreenPos = new Dictionary<int, (float X, float Y)>(layout.Nodes.Count);
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        foreach (var edge in layout.Edges)
        {
            if (!nodeScreenPos.TryGetValue(edge.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            var mx = (from.X + to.X) / 2f;
            var my = (from.Y + to.Y) / 2f;
            string debugLabel = $"{edge.TaxiwayName} {edge.FromNodeId}-{edge.ToNodeId}";
            canvas.DrawText(debugLabel, mx + 2, my + 4, _debugEdgeLabelPaint);
        }

        foreach (var node in layout.Nodes)
        {
            var (sx, sy) = nodeScreenPos[node.Id];
            string debugLabel = node.Name is not null ? $"{node.Id} {node.Name} ({node.Type})" : $"{node.Id} ({node.Type})";
            canvas.DrawText(debugLabel, sx + 5, sy - 3, _debugLabelPaint);
        }
    }

    private void DrawRunways(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, bool showLabels)
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

            if (showLabels)
            {
                string label = rwy.Name.Replace(" - ", "/");
                _labelCandidates.Add(new LabelCandidate([label], mx, my + 4, LabelPriority.Runway, _runwayLabelPaint, null));
            }
        }
    }

    private void DrawEdges(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, bool showDebugInfo, bool showTaxiwayLabels)
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

            bool isRamp = string.Equals(edge.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase);
            if (!showDebugInfo && !isRunway && !isRamp && showTaxiwayLabels)
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
                _labelCandidates.Add(new LabelCandidate([edge.TaxiwayName], mx + 3, my - 3, LabelPriority.Taxiway, _taxiLabelPaint, null));
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

    private void DrawShownTaxiRoutes(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, IReadOnlyList<ShownTaxiRouteEntry>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            int colorIdx = Array.IndexOf(TaxiRouteColorValues, entry.Color);
            if (colorIdx < 0)
            {
                colorIdx = 0;
            }

            DrawRoute(canvas, vp, layout, entry.Route, _shownTaxiRoutePaints[colorIdx]);
        }
    }

    private void DrawDrawHoverPreview(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? hoverRoute)
    {
        DrawRoute(canvas, vp, layout, hoverRoute, _drawHoverPreviewPaint);
    }

    private void DrawDrawnRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? drawnRoute, IReadOnlyList<int>? waypoints)
    {
        DrawRoute(canvas, vp, layout, drawnRoute, _drawnRoutePaint);

        if (waypoints is null || waypoints.Count == 0 || layout.Nodes.Count == 0)
        {
            return;
        }

        var nodePositions = new Dictionary<int, (float X, float Y)>();
        foreach (var node in layout.Nodes)
        {
            nodePositions[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        for (int i = 0; i < waypoints.Count; i++)
        {
            if (!nodePositions.TryGetValue(waypoints[i], out var pos))
            {
                continue;
            }

            canvas.DrawCircle(pos.X, pos.Y, 8f, _waypointMarkerPaint);
            canvas.DrawText($"{i + 1}", pos.X, pos.Y + _waypointTextPaint.TextSize / 3f, _waypointTextPaint);
        }
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

    private void DrawNodes(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto layout,
        int? hoveredNodeId,
        bool showDebugInfo,
        GroundFilterMode showHoldShort,
        GroundFilterMode showParking,
        GroundFilterMode showSpot
    )
    {
        // Pre-build node→taxiway screen angle map for hold short bar rendering
        var holdShortAngles = new Dictionary<int, float>();
        if (showHoldShort != GroundFilterMode.Off)
        {
            foreach (var edge in layout.Edges)
            {
                ComputeHoldShortAngle(holdShortAngles, vp, layout, edge, edge.FromNodeId);
                ComputeHoldShortAngle(holdShortAngles, vp, layout, edge, edge.ToNodeId);
            }
        }

        // Pre-build node→connected taxiway names for hover tooltips
        var nodeEdgeNames = new Dictionary<int, List<string>>();
        foreach (var edge in layout.Edges)
        {
            if (
                edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(edge.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            AddEdgeName(nodeEdgeNames, edge.FromNodeId, edge.TaxiwayName);
            AddEdgeName(nodeEdgeNames, edge.ToNodeId, edge.TaxiwayName);
        }

        foreach (var node in layout.Nodes)
        {
            var (sx, sy) = vp.LatLonToScreen(node.Latitude, node.Longitude);
            bool isHovered = hoveredNodeId == node.Id;

            if (node.Type == "RunwayHoldShort")
            {
                bool drawIcon = showHoldShort != GroundFilterMode.Off || isHovered;
                if (drawIcon)
                {
                    DrawHoldShortBar(canvas, sx, sy, holdShortAngles.GetValueOrDefault(node.Id, 0f));
                }

                if (node.RunwayId is not null)
                {
                    if (isHovered)
                    {
                        var twyNames = ResolveNearbyTaxiwayNames(node.Id, layout, nodeEdgeNames);
                        string[] lines = twyNames.Count > 0 ? [$"HS {node.RunwayId}", string.Join("/", twyNames)] : [$"HS {node.RunwayId}"];
                        _labelCandidates.Add(
                            new LabelCandidate(lines, sx + 12, sy - 14, LabelPriority.Hovered, _nodeLabelPaint, new SKColor(255, 255, 255))
                        );
                    }
                    else if (showHoldShort == GroundFilterMode.LabelsAndIcons)
                    {
                        _labelCandidates.Add(
                            new LabelCandidate([$"HS {node.RunwayId}"], sx + 5, sy - 3, LabelPriority.HoldShort, _nodeLabelPaint, null)
                        );
                    }
                }
            }
            else if (node.Type is "Parking" or "Helipad" or "Spot")
            {
                var mode = node.Type == "Spot" ? showSpot : showParking;
                bool drawIcon = mode != GroundFilterMode.Off || isHovered;

                if (drawIcon)
                {
                    _nodePaint.Color = node.Type switch
                    {
                        "Parking" => NodeParking,
                        "Helipad" => NodeHelipad,
                        _ => NodeSpot,
                    };

                    float radius = node.Type switch
                    {
                        "Parking" => 4f,
                        "Helipad" => 5f,
                        _ => 2.5f,
                    };

                    canvas.DrawCircle(sx, sy, radius, _nodePaint);

                    if (node.Type == "Helipad")
                    {
                        _nodeLabelPaint.Color = NodeHelipad;
                        canvas.DrawText("H", sx - 3, sy + 3, _nodeLabelPaint);
                    }
                }

                if (!showDebugInfo && node.Name is not null)
                {
                    if (isHovered)
                    {
                        var lines = BuildHoverLines(node.Name, node.Id, nodeEdgeNames);
                        _labelCandidates.Add(
                            new LabelCandidate(lines, sx + 12, sy - 14, LabelPriority.Hovered, _nodeLabelPaint, new SKColor(255, 255, 255))
                        );
                    }
                    else if (mode == GroundFilterMode.LabelsAndIcons)
                    {
                        _labelCandidates.Add(new LabelCandidate([node.Name], sx + 5, sy - 3, LabelPriority.ParkingSpot, _nodeLabelPaint, null));
                    }
                }
            }
            else
            {
                // Intersection nodes — always drawn
                _nodePaint.Color = NodeIntersection;
                canvas.DrawCircle(sx, sy, 2.5f, _nodePaint);

                if (isHovered && nodeEdgeNames.TryGetValue(node.Id, out var twyNames) && twyNames.Count > 0)
                {
                    _labelCandidates.Add(
                        new LabelCandidate(
                            [string.Join("/", twyNames)],
                            sx + 12,
                            sy - 14,
                            LabelPriority.Hovered,
                            _nodeLabelPaint,
                            new SKColor(255, 255, 255)
                        )
                    );
                }
            }

            if (isHovered)
            {
                canvas.DrawCircle(sx, sy, 8f, _hoverPaint);
            }
        }
    }

    private static void AddEdgeName(Dictionary<int, List<string>> map, int nodeId, string name)
    {
        if (!map.TryGetValue(nodeId, out var list))
        {
            list = [];
            map[nodeId] = list;
        }

        if (!list.Contains(name))
        {
            list.Add(name);
        }
    }

    /// <summary>
    /// For hold-short nodes, finds taxiway names by looking at direct edges first,
    /// then one hop through RAMP edges to reach named taxiways.
    /// </summary>
    private static List<string> ResolveNearbyTaxiwayNames(int nodeId, GroundLayoutDto layout, Dictionary<int, List<string>> nodeEdgeNames)
    {
        // Direct taxiway edges on this node
        if (nodeEdgeNames.TryGetValue(nodeId, out var direct) && direct.Count > 0)
        {
            return direct;
        }

        // One hop: find neighbors via any edge, then check their taxiway names
        var result = new List<string>();
        foreach (var edge in layout.Edges)
        {
            int neighborId;
            if (edge.FromNodeId == nodeId)
            {
                neighborId = edge.ToNodeId;
            }
            else if (edge.ToNodeId == nodeId)
            {
                neighborId = edge.FromNodeId;
            }
            else
            {
                continue;
            }

            if (nodeEdgeNames.TryGetValue(neighborId, out var neighborNames))
            {
                foreach (var name in neighborNames)
                {
                    if (!result.Contains(name))
                    {
                        result.Add(name);
                    }
                }
            }
        }

        return result;
    }

    private static string[] BuildHoverLines(string primaryLabel, int nodeId, Dictionary<int, List<string>> nodeEdgeNames)
    {
        if (!nodeEdgeNames.TryGetValue(nodeId, out var twyNames) || twyNames.Count == 0)
        {
            return [primaryLabel];
        }

        return [primaryLabel, string.Join("/", twyNames)];
    }

    /// <summary>
    /// Computes the screen-space angle (radians) of the taxiway edge at a hold-short node.
    /// Uses screen coordinates so rotation is handled correctly.
    /// </summary>
    private static void ComputeHoldShortAngle(Dictionary<int, float> angles, MapViewport vp, GroundLayoutDto layout, GroundEdgeDto edge, int nodeId)
    {
        if (angles.ContainsKey(nodeId))
        {
            return;
        }

        // Only use non-runway edges so the bar is perpendicular to the taxiway, not the runway
        if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var node = layout.Nodes.Find(n => n.Id == nodeId);
        if (node is null || node.Type != "RunwayHoldShort")
        {
            return;
        }

        int otherId = edge.FromNodeId == nodeId ? edge.ToNodeId : edge.FromNodeId;
        var other = layout.Nodes.Find(n => n.Id == otherId);
        if (other is null)
        {
            return;
        }

        var (nx, ny) = vp.LatLonToScreen(node.Latitude, node.Longitude);
        var (ox, oy) = vp.LatLonToScreen(other.Latitude, other.Longitude);
        float angle = MathF.Atan2(oy - ny, ox - nx);
        angles[nodeId] = angle;
    }

    private void DrawHoldShortBar(SKCanvas canvas, float sx, float sy, float taxiwayAngleRad)
    {
        const float halfLen = 7f;
        // Perpendicular to the taxiway direction
        float perpAngle = taxiwayAngleRad + MathF.PI / 2f;
        float dx = halfLen * MathF.Cos(perpAngle);
        float dy = halfLen * MathF.Sin(perpAngle);

        _holdShortBarPaint.Color = NodeHoldShort;
        canvas.DrawLine(sx - dx, sy - dy, sx + dx, sy + dy, _holdShortBarPaint);
    }

    /// <summary>Minimum triangle half-length in pixels (ensures visibility when zoomed out).</summary>
    private const float MinAircraftPx = 8f;

    /// <summary>
    /// The user-selected aircraft can have a slight scale-up for visual emphasis.
    /// Disabled for now.
    /// </summary>
    private const float SelectedScaleFactor = 1.0f;

    /// <summary>Fallback dimensions when FAA ACD data is unavailable (small GA aircraft).</summary>
    private const float FallbackLengthFt = 60f;
    private const float FallbackWingspanFt = 50f;

    /// <summary>Feet per degree of latitude (constant).</summary>
    private const double FeetPerDegLat = 364_567.2;

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
        // Pixels per foot at current zoom (latitude direction, no cosine correction needed for small areas)
        var pxPerFt = (float)(vp.Zoom * 5000.0 / FeetPerDegLat);

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
                : GetAircraftColor();

            var (lengthPx, widthPx) = ComputeAircraftPixelSize(ac.AircraftType, pxPerFt);
            if (isSelected)
            {
                lengthPx *= SelectedScaleFactor;
                widthPx *= SelectedScaleFactor;
            }

            bool isHeli = AircraftCategorization.Categorize(ac.AircraftType) == AircraftCategory.Helicopter;
            float headingDeg = (float)(ac.Heading - vp.RotationDeg);

            if (isHeli)
            {
                float rotorPx = ComputeRotorPixelRadius(ac.AircraftType, pxPerFt);
                DrawHelicopterSilhouette(canvas, sx, sy, headingDeg, lengthPx, widthPx, rotorPx, _aircraftPaint);
            }
            else
            {
                DrawFixedWingSilhouette(canvas, sx, sy, headingDeg, lengthPx, widthPx, _aircraftPaint);
            }
        }
    }

    /// <summary>
    /// Returns (halfLengthPx, halfWidthPx) for the aircraft silhouette.
    /// Uses FAA ACD dimensions when available, otherwise category fallbacks.
    /// Clamps to <see cref="MinAircraftPx"/> so aircraft remain visible when zoomed out.
    /// </summary>
    private static (float HalfLengthPx, float HalfWidthPx) ComputeAircraftPixelSize(string? aircraftType, float pxPerFt)
    {
        float lengthFt = FallbackLengthFt;
        float wingspanFt = FallbackWingspanFt;

        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record is not null)
        {
            if (record.LengthFt is { } len)
            {
                lengthFt = (float)len;
            }

            if (record.WingspanFt is { } ws)
            {
                wingspanFt = (float)ws;
            }
        }

        float halfLenPx = MathF.Max(lengthFt * 0.5f * pxPerFt, MinAircraftPx);
        float halfWsPx = MathF.Max(wingspanFt * 0.5f * pxPerFt, MinAircraftPx * (wingspanFt / lengthFt));
        return (halfLenPx, halfWsPx);
    }

    /// <summary>Fallback rotor diameter when FAA ACD data is unavailable (medium helicopter).</summary>
    private const float FallbackRotorDiameterFt = 40f;

    private static float ComputeRotorPixelRadius(string? aircraftType, float pxPerFt)
    {
        float diameterFt = FallbackRotorDiameterFt;
        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.RotorDiameterFt is { } rd)
        {
            diameterFt = (float)rd;
        }

        return MathF.Max(diameterFt * 0.5f * pxPerFt, MinAircraftPx);
    }

    private static SKColor GetAircraftColor()
    {
        return AircraftTaxiing;
    }

    /// <summary>
    /// Draws a top-down fixed-wing aircraft silhouette centered at (cx, cy).
    /// The silhouette spans exactly nose-to-tail = 2 * halfLength and wingtip-to-wingtip = 2 * halfWidth.
    /// Fuselage, swept wings, and horizontal stabilizer are drawn as a single filled path.
    /// </summary>
    private static void DrawFixedWingSilhouette(
        SKCanvas canvas,
        float cx,
        float cy,
        float headingDeg,
        float halfLength,
        float halfWidth,
        SKPaint paint
    )
    {
        // All coordinates in normalized space: X = along heading (-1 = tail, +1 = nose), Y = lateral (-1 = left, +1 = right).
        // Scaled by halfLength (X) and halfWidth (Y), then rotated and translated.

        // Fuselage width as fraction of wingspan
        float fuseW = 0.08f;

        // --- Build the silhouette path in normalized coords ---
        // We trace clockwise starting from the nose.
        // Nose tip
        SKPoint[] pts =
        [
            // Nose cone
            new(1.0f, 0f),
            // Fuselage widens
            new(0.7f, fuseW),
            // Wing leading edge root
            new(0.25f, fuseW),
            // Wing tip leading edge (swept back)
            new(0.05f, 1.0f),
            // Wing tip trailing edge
            new(-0.15f, 0.85f),
            // Wing trailing edge root
            new(-0.15f, fuseW),
            // Fuselage continues aft
            new(-0.6f, fuseW),
            // Horizontal stabilizer tip leading edge
            new(-0.7f, 0.4f),
            // Horizontal stabilizer tip trailing edge
            new(-0.9f, 0.35f),
            // Tail tip
            new(-0.95f, fuseW),
            // Tail end center
            new(-1.0f, 0f),
            // Mirror: tail tip (left side)
            new(-0.95f, -fuseW),
            // Horizontal stabilizer tip trailing edge (left)
            new(-0.9f, -0.35f),
            // Horizontal stabilizer tip leading edge (left)
            new(-0.7f, -0.4f),
            // Fuselage continues aft (left)
            new(-0.6f, -fuseW),
            // Wing trailing edge root (left)
            new(-0.15f, -fuseW),
            // Wing tip trailing edge (left)
            new(-0.15f, -0.85f),
            // Wing tip leading edge (left)
            new(0.05f, -1.0f),
            // Wing leading edge root (left)
            new(0.25f, -fuseW),
            // Fuselage narrows toward nose (left)
            new(0.7f, -fuseW),
        ];

        DrawRotatedSilhouette(canvas, cx, cy, headingDeg, halfLength, halfWidth, pts, paint);
    }

    /// <summary>
    /// Draws a top-down helicopter silhouette centered at (cx, cy).
    /// Shows fuselage (oval body), tail boom, tail rotor disc, and main rotor disc.
    /// HalfLength/halfWidth define the fuselage; rotorRadius defines the main rotor disc.
    /// </summary>
    private static void DrawHelicopterSilhouette(
        SKCanvas canvas,
        float cx,
        float cy,
        float headingDeg,
        float halfLength,
        float halfWidth,
        float rotorRadius,
        SKPaint paint
    )
    {
        float rad = (headingDeg - 90) * MathF.PI / 180f;
        float cosH = MathF.Cos(rad);
        float sinH = MathF.Sin(rad);
        float cosP = MathF.Cos(rad + MathF.PI / 2f);
        float sinP = MathF.Sin(rad + MathF.PI / 2f);

        // --- Fuselage body (teardrop shape) ---
        // Wider at front, tapers to tail boom
        float fuseW = 0.25f;
        SKPoint[] bodyPts =
        [
            new(0.5f, 0f),
            new(0.3f, fuseW),
            new(-0.1f, fuseW),
            new(-0.3f, 0.08f),
            // Tail boom
            new(-0.9f, 0.03f),
            new(-1.0f, 0f),
            new(-0.9f, -0.03f),
            // Back to body
            new(-0.3f, -0.08f),
            new(-0.1f, -fuseW),
            new(0.3f, -fuseW),
        ];

        DrawRotatedSilhouette(canvas, cx, cy, headingDeg, halfLength, halfWidth, bodyPts, paint);

        // --- Main rotor disc (circle centered slightly forward of body center) ---
        float rotorCenterOffset = halfLength * 0.1f;
        float rotorCx = cx + cosH * rotorCenterOffset;
        float rotorCy = cy + sinH * rotorCenterOffset;
        float effectiveRotorR = MathF.Max(rotorRadius, MinAircraftPx);

        using var rotorPaint = paint.Clone();
        rotorPaint.Style = SKPaintStyle.Stroke;
        rotorPaint.StrokeWidth = MathF.Max(1f, halfLength * 0.03f);
        canvas.DrawCircle(rotorCx, rotorCy, effectiveRotorR, rotorPaint);

        // Rotor cross lines
        canvas.DrawLine(
            rotorCx + cosH * effectiveRotorR,
            rotorCy + sinH * effectiveRotorR,
            rotorCx - cosH * effectiveRotorR,
            rotorCy - sinH * effectiveRotorR,
            rotorPaint
        );
        canvas.DrawLine(
            rotorCx + cosP * effectiveRotorR,
            rotorCy + sinP * effectiveRotorR,
            rotorCx - cosP * effectiveRotorR,
            rotorCy - sinP * effectiveRotorR,
            rotorPaint
        );

        // --- Tail rotor disc (small circle at tail boom end) ---
        float tailRotorOffset = halfLength * 0.95f;
        float tailRotorCx = cx - cosH * tailRotorOffset;
        float tailRotorCy = cy - sinH * tailRotorOffset;
        float tailRotorR = MathF.Max(halfLength * 0.12f, 2f);
        canvas.DrawCircle(tailRotorCx, tailRotorCy, tailRotorR, rotorPaint);
    }

    /// <summary>
    /// Transforms normalized silhouette points (X along heading, Y lateral) to screen space
    /// and draws them as a filled polygon.
    /// </summary>
    private static void DrawRotatedSilhouette(
        SKCanvas canvas,
        float cx,
        float cy,
        float headingDeg,
        float halfLength,
        float halfWidth,
        SKPoint[] normalizedPts,
        SKPaint paint
    )
    {
        float rad = (headingDeg - 90) * MathF.PI / 180f;
        float cosH = MathF.Cos(rad);
        float sinH = MathF.Sin(rad);
        float cosP = MathF.Cos(rad + MathF.PI / 2f);
        float sinP = MathF.Sin(rad + MathF.PI / 2f);

        using var path = new SKPath();
        for (int i = 0; i < normalizedPts.Length; i++)
        {
            float ax = normalizedPts[i].X * halfLength;
            float ay = normalizedPts[i].Y * halfWidth;
            float screenX = cx + cosH * ax + cosP * ay;
            float screenY = cy + sinH * ax + sinP * ay;

            if (i == 0)
            {
                path.MoveTo(screenX, screenY);
            }
            else
            {
                path.LineTo(screenX, screenY);
            }
        }

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
            var dbColor = isSelected ? AircraftSelected : TerminalGreen;

            _dataBlockTextPaint.Color = dbColor;
            canvas.DrawRect(layout.Rect, _dataBlockBgPaint);

            if (isSelected)
            {
                _dataBlockLeaderPaint.Color = AircraftSelected;
                canvas.DrawRect(layout.Rect, _dataBlockLeaderPaint);
            }

            var leaderEnd = ClampToBlockEdge(sx, sy, layout.Rect);
            _dataBlockLeaderPaint.Color = dbColor;
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

    private void DrawLabels(SKCanvas canvas, bool hoveredOnly)
    {
        _labelCandidates.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var placedRects = new List<SKRect>(_labelCandidates.Count);

        foreach (var label in _labelCandidates)
        {
            bool isHovered = label.Priority == LabelPriority.Hovered;
            if (hoveredOnly != isHovered)
            {
                continue;
            }

            var paint = label.Paint;
            float textHeight = paint.TextSize;
            float lineSpacing = textHeight + 2;
            int lineCount = label.Lines.Length;

            float maxWidth = 0;
            foreach (var line in label.Lines)
            {
                float w = paint.MeasureText(line);
                if (w > maxWidth)
                {
                    maxWidth = w;
                }
            }

            float left = paint.TextAlign == SKTextAlign.Center ? label.X - maxWidth / 2f - 2 : label.X - 2;
            float totalHeight = textHeight + (lineCount - 1) * lineSpacing;
            var rect = new SKRect(left, label.Y - textHeight - 1, left + maxWidth + 4, label.Y + (totalHeight - textHeight) + 1);

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

            canvas.DrawRect(rect, _labelBgPaint);

            if (label.ColorOverride is { } color)
            {
                paint.Color = color;
            }

            float y = label.Y;
            foreach (var line in label.Lines)
            {
                canvas.DrawText(line, label.X, y, paint);
                y += lineSpacing;
            }
        }
    }

    public void Dispose()
    {
        foreach (var paint in _shownTaxiRoutePaints)
        {
            paint.Dispose();
        }

        _runwayFillPaint.Dispose();
        _runwayOutlinePaint.Dispose();
        _runwayLabelPaint.Dispose();
        _runwayPaint.Dispose();
        _taxiwayPaint.Dispose();
        _taxiLabelPaint.Dispose();
        _activeRoutePaint.Dispose();
        _previewRoutePaint.Dispose();
        _drawnRoutePaint.Dispose();
        _drawHoverPreviewPaint.Dispose();
        _waypointMarkerPaint.Dispose();
        _waypointTextPaint.Dispose();
        _nodePaint.Dispose();
        _holdShortBarPaint.Dispose();
        _nodeLabelPaint.Dispose();
        _aircraftPaint.Dispose();
        _hoverPaint.Dispose();
        _dataBlockLeaderPaint.Dispose();
        _dataBlockTextPaint.Dispose();
        _dataBlockBgPaint.Dispose();
        _labelBgPaint.Dispose();
        _bgPaint.Dispose();
        _debugLabelPaint.Dispose();
        _debugEdgeLabelPaint.Dispose();
    }
}
