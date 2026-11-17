using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope view's draw-route mode: Draw route in the aircraft menu's view section and the EuroScope Destination field start it,
/// for an aircraft that may be commanded (not a live-traffic shadow, not one whose phase hides the flight commands); Enter sends
/// the route and Backspace undoes the last waypoint, ahead of the entry; a middle click on a waypoint opens its condition popup
/// and a right click its menu.
/// </summary>
public partial class ScopeView
{
    private ScopeWaypointConditionPopup? _routeConditionPopup;
    private ContextMenu? _routeWaypointMenu;

    /// <summary>The waypoint-condition popup a middle click on a waypoint opened, while it is open. Test seam.</summary>
    internal ScopeWaypointConditionPopup? ActiveRouteConditionPopup => (_routeConditionPopup is { IsOpen: true } popup) ? popup : null;

    /// <summary>
    /// Whether <paramref name="ac"/>'s fields may start a flight command: not for a live-traffic shadow on the surface, which is
    /// read-only, nor for an aircraft whose phase hides the flight commands (on the ground, landing or rolling).
    /// </summary>
    internal static bool AcceptsFlightCommands(AircraftModel? ac) =>
        !AircraftCommandApplicability.IsSurfaceShadow(ac) && !AircraftMenuBuilder.HidesFlightCommands(ac?.CurrentPhase, ac?.IsOnGround ?? false);

    partial void WireDrawRoute()
    {
        Canvas.RoutePointConditionRequested += OnRoutePointConditionRequested;
        Canvas.RouteWaypointRightClicked += OnRouteWaypointRightClicked;
    }

    partial void AttachDrawRoute(ScopeViewModel vm)
    {
        Canvas.PointNamer = vm.Main.ScopeMaps.DescribePoint;
        Canvas.DrawRoute = vm.DrawRoute;
        vm.DrawRoute.Changed += OnDrawRouteChanged;
        vm.CloseRouteOverlays = CloseRouteOverlays;
    }

    partial void DetachDrawRoute(ScopeViewModel vm)
    {
        vm.DrawRoute.Changed -= OnDrawRouteChanged;
        vm.CloseRouteOverlays = null;
        Canvas.DrawRoute = null;
        Canvas.PointNamer = null;
        CloseRouteOverlays();
    }

    partial void AddDrawRouteSection(List<Control> items, ScopeViewModel vm, AircraftModel? ac, MenuContext context)
    {
        if (AcceptsFlightCommands(ac))
        {
            string callsign = context.Callsign;
            items.Add(CanvasMenuItems.DrawRoute("Draw route", () => vm.EnterDrawRoute(callsign)));
        }
    }

    // While a route is drawn, Enter sends it and Backspace undoes its last waypoint, whatever the entry holds.
    partial void KeyDrawRoute(KeyEventArgs e) => RunRouteKey(e);

    // Enter sends the route and Backspace undoes its last waypoint, while one is drawn; marks the key handled when it took it.
    private void RunRouteKey(KeyEventArgs e)
    {
        if (e.Handled || (DataContext is not ScopeViewModel { DrawRoute.IsDrawing: true } vm))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _ = vm.ConfirmDrawRouteAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            vm.DrawRoute.Undo();
            e.Handled = true;
        }
    }

    // The EuroScope Destination field: starts the mode for its aircraft, inert for one that may not be commanded.
    private static void StartFieldDrawRoute(ScopeViewModel vm, AircraftModel ac)
    {
        if (AcceptsFlightCommands(ac))
        {
            vm.EnterDrawRoute(ac.Callsign);
        }
    }

    // Any change to the route closes the waypoint menu, whose rows name a waypoint by index; a route that ends also takes its
    // condition popup with it.
    private void OnDrawRouteChanged()
    {
        CloseRouteWaypointMenu();
        if (DataContext is ScopeViewModel { DrawRoute.IsDrawing: false })
        {
            CloseRouteConditionPopup();
        }
    }

    // Closes the condition popup and the waypoint menu. Returns whether either was open.
    private bool CloseRouteOverlays()
    {
        bool popup = CloseRouteConditionPopup();
        bool menu = CloseRouteWaypointMenu();
        return popup || menu;
    }

    // Closes the open waypoint menu. Returns whether one was open.
    private bool CloseRouteWaypointMenu()
    {
        ContextMenu? menu = _routeWaypointMenu;
        _routeWaypointMenu = null;
        if (menu is not { IsOpen: true })
        {
            return false;
        }

        menu.Close();
        return true;
    }

    private void OnRoutePointConditionRequested(int index, Point pos)
    {
        if ((DataContext is not ScopeViewModel vm) || (vm.DrawRoute.Waypoints is not { } waypoints) || (index >= waypoints.Count))
        {
            return;
        }

        CloseRouteConditionPopup();
        DrawRouteState route = vm.DrawRoute;
        WaypointCondition? existing = route.GetCondition(index);
        var popup = new ScopeWaypointConditionPopup(
            waypoints[index].ResolvedName,
            existing?.Altitude,
            existing?.Commands,
            (altitude, commands) => route.SetCondition(index, altitude, commands)
        );
        if (popup.Open(Canvas, pos))
        {
            _routeConditionPopup = popup;
        }
    }

    // Closes the open condition popup. Returns whether one was open.
    private bool CloseRouteConditionPopup()
    {
        ScopeWaypointConditionPopup? popup = _routeConditionPopup;
        _routeConditionPopup = null;
        if (popup is not { IsOpen: true })
        {
            return false;
        }

        popup.Close();
        return true;
    }

    private void OnRouteWaypointRightClicked(int index, Point pos)
    {
        if ((DataContext is not ScopeViewModel vm) || (vm.DrawRoute.Waypoints is not { } waypoints) || (index >= waypoints.Count))
        {
            return;
        }

        ContextMenu menu = BuildWaypointMenu(vm, waypoints, index);
        // The open menu holds the keyboard: Enter on no row and Backspace still reach the route.
        menu.AddHandler(KeyDownEvent, (_, e) => RunRouteKey(e));
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_routeWaypointMenu, menu))
            {
                _routeWaypointMenu = null;
            }
        };
        ShowContextMenu(menu);
        _routeWaypointMenu = menu;
    }

    // The waypoint's menu, as the Radar's: its name, then Confirm route, Delete waypoint, Delete waypoints after (not on the last
    // waypoint) and Cancel route.
    private static ContextMenu BuildWaypointMenu(ScopeViewModel vm, IReadOnlyList<DrawnWaypoint> waypoints, int index)
    {
        DrawRouteState route = vm.DrawRoute;
        var menu = new ContextMenu();
        menu.Items.Add(
            new MenuItem
            {
                Header = waypoints[index].ResolvedName,
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());
        menu.Items.Add(RouteMenuItem("Confirm route", () => _ = vm.ConfirmDrawRouteAsync()));
        menu.Items.Add(RouteMenuItem("Delete waypoint", () => route.RemoveAt(index)));
        if (index < (waypoints.Count - 1))
        {
            menu.Items.Add(RouteMenuItem("Delete waypoints after", () => route.RemoveAfter(index)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(RouteMenuItem("Cancel route", route.Cancel));
        return menu;
    }

    private static MenuItem RouteMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
