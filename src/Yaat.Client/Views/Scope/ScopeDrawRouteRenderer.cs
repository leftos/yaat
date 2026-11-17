using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// Draws the route being drawn on the Scope, as the Radar draws it, in amber: a dashed leg from the aircraft to the first
/// waypoint, solid legs between waypoints, a dashed rubber band from the last waypoint (or the aircraft, before the first) to the
/// pointer labelled with the pointer's FRD, and a diamond at each waypoint with its name and the summary of its condition below.
/// </summary>
public sealed class ScopeDrawRouteRenderer : IDisposable
{
    private const float WaypointSize = 5f;

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

    private readonly SKPaint _routeLabelPaint = new() { Color = RouteDrawColor, IsAntialias = true };

    private readonly SKFont _routeLabelFont = PlatformHelper.MonospaceFont(13);

    private readonly SKPaint _routeConditionLabelPaint = new() { Color = new SKColor(255, 200, 0, 180), IsAntialias = true };

    private readonly SKFont _routeConditionLabelFont = PlatformHelper.MonospaceFont(11);

    /// <summary>Draws <paramref name="overlay"/> over <paramref name="vp"/>.</summary>
    public void Draw(SKCanvas canvas, MapViewport vp, ScopeDrawRouteOverlay overlay)
    {
        IReadOnlyList<DrawnWaypoint> waypoints = overlay.Waypoints;
        if (waypoints.Count == 0)
        {
            if (overlay.Origin is { } origin)
            {
                DrawRubberBand(canvas, vp, origin, overlay);
            }

            return;
        }

        DrawLegs(canvas, vp, overlay);
        DrawnWaypoint last = waypoints[^1];
        DrawRubberBand(canvas, vp, new LatLon(last.Lat, last.Lon), overlay);
        DrawWaypoints(canvas, vp, overlay);
    }

    // The dashed leg from the aircraft to the first waypoint, then the solid legs between waypoints.
    private void DrawLegs(SKCanvas canvas, MapViewport vp, ScopeDrawRouteOverlay overlay)
    {
        IReadOnlyList<DrawnWaypoint> waypoints = overlay.Waypoints;
        if (overlay.Origin is { } origin)
        {
            (float ox, float oy) = vp.LatLonToScreen(origin.Lat, origin.Lon);
            (float fx, float fy) = vp.LatLonToScreen(waypoints[0].Lat, waypoints[0].Lon);
            canvas.DrawLine(ox, oy, fx, fy, _rubberBandPaint);
        }

        for (int i = 1; i < waypoints.Count; i++)
        {
            (float x1, float y1) = vp.LatLonToScreen(waypoints[i - 1].Lat, waypoints[i - 1].Lon);
            (float x2, float y2) = vp.LatLonToScreen(waypoints[i].Lat, waypoints[i].Lon);
            canvas.DrawLine(x1, y1, x2, y2, _routeLinePaint);
        }
    }

    // The dashed rubber band from `from` to the pointer, with the pointer's FRD beside it.
    private void DrawRubberBand(SKCanvas canvas, MapViewport vp, LatLon from, ScopeDrawRouteOverlay overlay)
    {
        if (overlay.Cursor is not { } target)
        {
            return;
        }

        (float lx, float ly) = vp.LatLonToScreen(from.Lat, from.Lon);
        (float cx, float cy) = vp.LatLonToScreen(target.Lat, target.Lon);
        canvas.DrawLine(lx, ly, cx, cy, _rubberBandPaint);
        if (overlay.CursorLabel is { } label)
        {
            canvas.DrawText(label, cx + 8, cy - 4, SKTextAlign.Left, _routeLabelFont, _routeLabelPaint);
        }
    }

    // A diamond at each waypoint, its name beside it and its condition's summary below the name.
    private void DrawWaypoints(SKCanvas canvas, MapViewport vp, ScopeDrawRouteOverlay overlay)
    {
        IReadOnlyList<DrawnWaypoint> waypoints = overlay.Waypoints;
        for (int i = 0; i < waypoints.Count; i++)
        {
            DrawnWaypoint wp = waypoints[i];
            (float sx, float sy) = vp.LatLonToScreen(wp.Lat, wp.Lon);
            using var path = new SKPath();
            path.MoveTo(sx, sy - WaypointSize);
            path.LineTo(sx + WaypointSize, sy);
            path.LineTo(sx, sy + WaypointSize);
            path.LineTo(sx - WaypointSize, sy);
            path.Close();
            canvas.DrawPath(path, _routeWaypointPaint);
            canvas.DrawText(wp.ResolvedName, sx + 8, sy - 2, SKTextAlign.Left, _routeLabelFont, _routeLabelPaint);
            string summary = (overlay.Conditions?.GetValueOrDefault(i) is { } cond) ? cond.ToSummary() : "";
            if (summary.Length > 0)
            {
                canvas.DrawText(summary, sx + 8, sy + 11, SKTextAlign.Left, _routeConditionLabelFont, _routeConditionLabelPaint);
            }
        }
    }

    public void Dispose()
    {
        _routeLinePaint.Dispose();
        _rubberBandPaint.Dispose();
        _routeWaypointPaint.Dispose();
        _routeLabelPaint.Dispose();
        _routeLabelFont.Dispose();
        _routeConditionLabelPaint.Dispose();
        _routeConditionLabelFont.Dispose();
    }
}
