using Avalonia;
using Avalonia.Input;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope canvas's draw-route mode: while a route is drawn, a left press places a waypoint (one within reach of a waypoint
/// places none), a middle press on a waypoint asks for its conditions and swallows any other, and a right press on a waypoint
/// asks for its menu; an Alt+left press and the armed measuring tool's presses go on to the measure rung. Each move redraws the
/// rubber band, and the pointer is a cross.
/// </summary>
public sealed partial class ScopeCanvas
{
    // How near a press must land to a waypoint to be on it, in pixels.
    private const float WaypointHitRadiusPx = 20f;

    /// <summary>The pointer while a route is drawn.</summary>
    internal static readonly Cursor DrawRouteCursor = new(StandardCursorType.Cross);

    private DrawRouteState? _drawRoute;

    /// <summary>The view model's draw-route mode this canvas draws and feeds; null draws and takes nothing.</summary>
    internal DrawRouteState? DrawRoute
    {
        get => _drawRoute;
        set
        {
            _drawRoute?.Changed -= OnDrawRouteChanged;
            _drawRoute = value;
            _drawRoute?.Changed += OnDrawRouteChanged;
            OnDrawRouteChanged();
        }
    }

    /// <summary>Names a point by FRD for the rubber band's label: (lat, lon) to a name, or null. Null labels nothing.</summary>
    internal Func<double, double, string?>? PointNamer { get; set; }

    /// <summary>Fired for a middle press on a waypoint while a route is drawn, with its index and the press point.</summary>
    public event Action<int, Point>? RoutePointConditionRequested;

    /// <summary>Fired for a right press on a waypoint while a route is drawn, with its index and the press point.</summary>
    public event Action<int, Point>? RouteWaypointRightClicked;

    private bool IsDrawingRoute => _drawRoute is { IsDrawing: true };

    private void OnDrawRouteChanged()
    {
        Cursor = IsDrawingRoute ? DrawRouteCursor : null;
        MarkDirty();
    }

    partial void PressDrawRoute(in ScopePress press, ref bool handled)
    {
        if (handled || (_drawRoute is not { IsDrawing: true } route) || IsMeasuring || IsAltLeftPress(press.Props, press.Event.KeyModifiers))
        {
            return;
        }

        handled = RoutePress(route, press.Props.PointerUpdateKind, press.Pos);
    }

    // A press while drawing: whether the mode takes it. A right press off every waypoint goes on down the ladder.
    private bool RoutePress(DrawRouteState route, PointerUpdateKind kind, Point pos)
    {
        int near = NearestWaypointIndex(route.Waypoints, pos);
        switch (kind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                if (near < 0)
                {
                    LatLon at = PointAt(pos);
                    route.Place(at.Lat, at.Lon);
                }

                return true;
            case PointerUpdateKind.MiddleButtonPressed:
                if (near >= 0)
                {
                    RoutePointConditionRequested?.Invoke(near, pos);
                }

                return true;
            case PointerUpdateKind.RightButtonPressed when near >= 0:
                RouteWaypointRightClicked?.Invoke(near, pos);
                return true;
            default:
                return false;
        }
    }

    // The index of the waypoint nearest the point within reach, or -1.
    private int NearestWaypointIndex(IReadOnlyList<DrawnWaypoint>? waypoints, Point pos)
    {
        if (waypoints is null)
        {
            return -1;
        }

        int bestIdx = -1;
        float bestDist = WaypointHitRadiusPx;
        for (int i = 0; i < waypoints.Count; i++)
        {
            (float sx, float sy) = Viewport.LatLonToScreen(waypoints[i].Lat, waypoints[i].Lon);
            float dx = (float)pos.X - sx;
            float dy = (float)pos.Y - sy;
            float dist = MathF.Sqrt((dx * dx) + (dy * dy));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    // The rubber band follows the pointer.
    partial void MoveDrawRoute(in ScopeMove move)
    {
        if (IsDrawingRoute)
        {
            MarkDirty();
        }
    }

    partial void CollectDrawRoute(ScopeFrame frame, ref ScopeOverlays overlays)
    {
        if (_drawRoute is not { IsDrawing: true } route)
        {
            return;
        }

        AircraftModel? origin = Aircraft?.FirstOrDefault(a => a.Callsign == route.Callsign);
        LatLon? cursor = (MeasureCursor is { } c) ? PointAt(c) : null;
        string? label = (cursor is { } at) ? PointNamer?.Invoke(at.Lat, at.Lon) : null;
        overlays = overlays with
        {
            DrawRoute = new ScopeDrawRouteOverlay(route.Waypoints ?? [], route.ConditionsSnapshot, origin?.Position, cursor, label),
        };
    }
}
