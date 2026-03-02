using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Ground;

/// <summary>
/// Stateless SkiaSharp renderer for the airport ground layout.
/// All SKPaint objects are pre-allocated and reused.
/// </summary>
public sealed class GroundRenderer : IDisposable
{
    private static readonly SKColor BackgroundColor = SKColor.Parse("#0e0e1a");
    private static readonly SKColor RunwayColor = new(120, 120, 120);
    private static readonly SKColor TaxiwayColor = new(200, 180, 60);
    private static readonly SKColor TaxiLabelColor = new(200, 180, 60, 160);
    private static readonly SKColor NodeIntersection = new(80, 120, 200);
    private static readonly SKColor NodeParking = new(60, 180, 80);
    private static readonly SKColor NodeSpot = new(220, 160, 40);
    private static readonly SKColor NodeHoldShort = new(220, 60, 60);
    private static readonly SKColor ActiveRouteColor = new(60, 220, 60);
    private static readonly SKColor AircraftTaxiing = new(0, 200, 255);
    private static readonly SKColor AircraftHolding = new(255, 200, 0);
    private static readonly SKColor AircraftParked = new(100, 100, 100);
    private static readonly SKColor AircraftSelected = new(255, 255, 255);
    private static readonly SKColor AircraftDimmed = new(80, 80, 100);
    private static readonly SKColor HoverRingColor = new(255, 255, 255, 160);
    private static readonly SKColor CallsignColor = new(220, 220, 220);

    private readonly SKPaint _runwayPaint = new()
    {
        Color = RunwayColor, StrokeWidth = 6, Style = SKPaintStyle.Stroke,
        IsAntialias = true, StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _taxiwayPaint = new()
    {
        Color = TaxiwayColor, StrokeWidth = 2, Style = SKPaintStyle.Stroke,
        IsAntialias = true, StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _taxiLabelPaint = new()
    {
        Color = TaxiLabelColor, TextSize = 10, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    private readonly SKPaint _activeRoutePaint = new()
    {
        Color = ActiveRouteColor, StrokeWidth = 4, Style = SKPaintStyle.Stroke,
        IsAntialias = true, StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _nodePaint = new()
    {
        Style = SKPaintStyle.Fill, IsAntialias = true,
    };

    private readonly SKPaint _nodeLabelPaint = new()
    {
        Color = new SKColor(200, 200, 200), TextSize = 9, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    private readonly SKPaint _aircraftPaint = new()
    {
        Style = SKPaintStyle.Fill, IsAntialias = true,
    };

    private readonly SKPaint _callsignPaint = new()
    {
        Color = CallsignColor, TextSize = 11, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    private readonly SKPaint _hoverPaint = new()
    {
        Color = HoverRingColor, StrokeWidth = 2, Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _bgPaint = new()
    {
        Color = BackgroundColor, Style = SKPaintStyle.Fill,
    };

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto? layout,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        int? hoveredNodeId,
        TaxiRoute? activeRoute)
    {
        canvas.Clear(BackgroundColor);

        if (layout is null)
        {
            return;
        }

        DrawEdges(canvas, vp, layout);
        DrawActiveRoute(canvas, vp, layout, activeRoute);
        DrawNodes(canvas, vp, layout, hoveredNodeId);
        DrawAircraft(canvas, vp, aircraft, selectedAircraft);
    }

    private void DrawEdges(
        SKCanvas canvas, MapViewport vp, GroundLayoutDto layout)
    {
        // Build node lookup for screen positions
        var nodeScreenPos = new Dictionary<int, (float X, float Y)>(
            layout.Nodes.Count);
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(
                node.Latitude, node.Longitude);
        }

        foreach (var edge in layout.Edges)
        {
            if (!nodeScreenPos.TryGetValue(edge.FromNodeId, out var from)
                || !nodeScreenPos.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            bool isRunway = edge.TaxiwayName.StartsWith(
                "RWY", StringComparison.OrdinalIgnoreCase);
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

            // Taxiway name label at midpoint (skip runway edges)
            if (!isRunway)
            {
                var mx = (from.X + to.X) / 2f;
                var my = (from.Y + to.Y) / 2f;
                canvas.DrawText(edge.TaxiwayName, mx + 3, my - 3,
                    _taxiLabelPaint);
            }
        }
    }

    private void DrawActiveRoute(
        SKCanvas canvas, MapViewport vp, GroundLayoutDto layout,
        TaxiRoute? route)
    {
        if (route is null)
        {
            return;
        }

        var nodeScreenPos = new Dictionary<int, (float X, float Y)>();
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(
                node.Latitude, node.Longitude);
        }

        foreach (var seg in route.Segments)
        {
            if (!nodeScreenPos.TryGetValue(seg.FromNodeId, out var from)
                || !nodeScreenPos.TryGetValue(seg.ToNodeId, out var to))
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
                canvas.DrawPath(path, _activeRoutePaint);
            }
            else
            {
                canvas.DrawLine(from.X, from.Y, to.X, to.Y,
                    _activeRoutePaint);
            }
        }
    }

    private void DrawNodes(
        SKCanvas canvas, MapViewport vp, GroundLayoutDto layout,
        int? hoveredNodeId)
    {
        foreach (var node in layout.Nodes)
        {
            var (sx, sy) = vp.LatLonToScreen(
                node.Latitude, node.Longitude);

            _nodePaint.Color = node.Type switch
            {
                "Parking" => NodeParking,
                "Spot" => NodeSpot,
                "RunwayHoldShort" => NodeHoldShort,
                _ => NodeIntersection,
            };

            float radius = node.Type switch
            {
                "Parking" => 4f,
                "RunwayHoldShort" => 3.5f,
                _ => 2.5f,
            };

            canvas.DrawCircle(sx, sy, radius, _nodePaint);

            // Label parking and hold-short nodes
            if (node.Name is not null
                && node.Type is "Parking" or "Spot")
            {
                canvas.DrawText(node.Name, sx + 5, sy - 3,
                    _nodeLabelPaint);
            }
            else if (node.RunwayId is not null
                && node.Type == "RunwayHoldShort")
            {
                canvas.DrawText($"HS {node.RunwayId}", sx + 5, sy - 3,
                    _nodeLabelPaint);
            }

            // Hover highlight
            if (hoveredNodeId == node.Id)
            {
                canvas.DrawCircle(sx, sy, 8f, _hoverPaint);
            }
        }
    }

    private void DrawAircraft(
        SKCanvas canvas, MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft)
    {
        foreach (var ac in aircraft)
        {
            if (!ac.IsOnGround)
            {
                continue;
            }

            var (sx, sy) = vp.LatLonToScreen(ac.Latitude, ac.Longitude);
            bool isSelected = ac == selectedAircraft;

            _aircraftPaint.Color = isSelected
                ? AircraftSelected
                : selectedAircraft is not null
                    ? AircraftDimmed
                    : GetAircraftColor(ac);

            DrawTriangle(canvas, sx, sy, (float)ac.Heading,
                isSelected ? 8f : 6f, _aircraftPaint);

            // Callsign label
            _callsignPaint.Color = isSelected
                ? SKColors.White
                : new SKColor(180, 180, 180);
            canvas.DrawText(ac.Callsign, sx + 10, sy + 4,
                _callsignPaint);
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

    private static void DrawTriangle(
        SKCanvas canvas, float cx, float cy, float headingDeg,
        float size, SKPaint paint)
    {
        var rad = (headingDeg - 90) * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);

        // Isoceles triangle pointing in heading direction
        var tip = new SKPoint(cx + cos * size, cy + sin * size);
        var left = new SKPoint(
            cx + MathF.Cos(rad + 2.5f) * size * 0.6f,
            cy + MathF.Sin(rad + 2.5f) * size * 0.6f);
        var right = new SKPoint(
            cx + MathF.Cos(rad - 2.5f) * size * 0.6f,
            cy + MathF.Sin(rad - 2.5f) * size * 0.6f);

        using var path = new SKPath();
        path.MoveTo(tip);
        path.LineTo(left);
        path.LineTo(right);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    public void Dispose()
    {
        _runwayPaint.Dispose();
        _taxiwayPaint.Dispose();
        _taxiLabelPaint.Dispose();
        _activeRoutePaint.Dispose();
        _nodePaint.Dispose();
        _nodeLabelPaint.Dispose();
        _aircraftPaint.Dispose();
        _callsignPaint.Dispose();
        _hoverPaint.Dispose();
        _bgPaint.Dispose();
    }
}
