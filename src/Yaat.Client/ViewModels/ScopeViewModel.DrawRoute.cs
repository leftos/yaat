using System.ComponentModel;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's draw-route mode: its own <see cref="DrawRouteState"/>, started from the aircraft menu's Draw route or a EuroScope
/// Destination field, left by Esc, by a selection that leaves the drawn aircraft and with the per-track state.
/// </summary>
public sealed partial class ScopeViewModel
{
    private DrawRouteState? _drawRoute;

    /// <summary>This scope's draw-route mode, made on first use.</summary>
    public DrawRouteState DrawRoute => _drawRoute ??= CreateDrawRoute();

    /// <summary>
    /// Sends a pilot command for a callsign with the user's initials: (callsign, command, initials). Null sends through
    /// <see cref="MainViewModel.SendCommandForViewAsync"/>. Test seam.
    /// </summary>
    internal Func<string, string, string, Task>? SendPilotCommand { get; set; }

    /// <summary>
    /// Asked by Esc before it cancels the route: closes the open waypoint-condition popup or waypoint menu and answers whether
    /// either was open, so that Esc closes only that. Set by the view.
    /// </summary>
    internal Func<bool>? CloseRouteOverlays { get; set; }

    /// <summary>
    /// Starts drawing a route for <paramref name="callsign"/>: selects the aircraft and disarms the Rings tool, so the next left
    /// clicks place waypoints.
    /// </summary>
    public void EnterDrawRoute(string callsign)
    {
        SelectAircraft(callsign);
        DisarmRingTool();
        DrawRoute.Enter(callsign);
    }

    /// <summary>Sends the drawn route with the user's initials and leaves the mode.</summary>
    public Task ConfirmDrawRouteAsync() => DrawRoute.ConfirmAsync(Main.Preferences.UserInitials);

    private DrawRouteState CreateDrawRoute()
    {
        var route = new DrawRouteState(() => Main.ScopeMaps.Fixes, SendRouteCommand);
        PropertyChanged += CancelDrawRouteOnSelection;
        return route;
    }

    /// <summary>Sends "FH {heading}" for <paramref name="callsign"/> with the user's initials.</summary>
    public Task FlyHeadingAsync(string callsign, int heading) => SendRouteCommand(callsign, $"FH {heading}", Main.Preferences.UserInitials);

    private Task SendRouteCommand(string callsign, string command, string initials) =>
        (SendPilotCommand ?? Main.SendCommandForViewAsync)(callsign, command, initials);

    // A selection that leaves the aircraft the route is drawn for cancels the route.
    private void CancelDrawRouteOnSelection(object? sender, PropertyChangedEventArgs e)
    {
        if (
            (e.PropertyName == nameof(SelectedAircraft))
            && (_drawRoute is { IsDrawing: true } route)
            && (SelectedAircraft?.Callsign != route.Callsign)
        )
        {
            route.Cancel();
        }
    }

    partial void EscapeDrawRoute(ref bool handled)
    {
        if (handled || (_drawRoute is not { IsDrawing: true } route))
        {
            return;
        }

        if (CloseRouteOverlays?.Invoke() != true)
        {
            route.Cancel();
        }

        handled = true;
    }

    partial void ClearDrawRouteTrackState() => _drawRoute?.Cancel();
}
