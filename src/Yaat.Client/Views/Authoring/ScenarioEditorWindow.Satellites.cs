using Avalonia.Interactivity;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Tools menu's satellite windows — the spawn planner and the route composer: each opens once, modeless and owned by
/// the editor, a second click brings it forward, and it closes when the open copy closes or changes and when the editor
/// closes. Apply Route, from the Tools menu or the aircraft detail's button, opens modal over the editor for the selected
/// aircraft.
/// </summary>
public partial class ScenarioEditorWindow
{
    private SpawnPlannerWindow? _spawnPlannerWindow;

    private RouteComposerWindow? _routeComposerWindow;

    // Set while Apply Route is open, so a second request does nothing.
    private bool _applyRouteOpen;

    private void WireSatellites()
    {
        SpawnPlannerMenuItem.Click += OnSpawnPlannerClick;
        RouteComposerMenuItem.Click += OnRouteComposerClick;
        ApplyRouteMenuItem.Click += OnApplyRouteClick;
        AircraftView.ApplyRouteRequested += OnApplyRouteRequested;
        Editor.SatellitesClosing += OnSatellitesClosing;
    }

    private void UnwireSatellites()
    {
        SpawnPlannerMenuItem.Click -= OnSpawnPlannerClick;
        RouteComposerMenuItem.Click -= OnRouteComposerClick;
        ApplyRouteMenuItem.Click -= OnApplyRouteClick;
        AircraftView.ApplyRouteRequested -= OnApplyRouteRequested;
        Editor.SatellitesClosing -= OnSatellitesClosing;
        CloseSatellites();
    }

    private void OnSpawnPlannerClick(object? sender, RoutedEventArgs e) => OpenSpawnPlanner();

    private void OpenSpawnPlanner()
    {
        if (_spawnPlannerWindow is { } open)
        {
            open.RestoreAndActivate();
            return;
        }

        if (Editor.CreateSpawnPlanner() is not { } planner)
        {
            return;
        }

        var window = SpawnPlannerWindow.Create(planner, _preferences, OpenCopyName());
        window.Closed += OnSpawnPlannerClosed;
        _spawnPlannerWindow = window;
        window.Show(this);
    }

    private void OnRouteComposerClick(object? sender, RoutedEventArgs e) => OpenRouteComposer();

    private void OpenRouteComposer()
    {
        if (_routeComposerWindow is { } open)
        {
            open.RestoreAndActivate();
            return;
        }

        if (Editor.CreateRouteComposerContext() is not { } context)
        {
            return;
        }

        var window = RouteComposerWindow.Create(context, Editor.Routes, _preferences);
        window.Closed += OnRouteComposerClosed;
        _routeComposerWindow = window;
        window.Show(this);
    }

    private async void OnApplyRouteClick(object? sender, RoutedEventArgs e) => await OpenApplyRouteAsync();

    private async void OnApplyRouteRequested(object? sender, EventArgs e) => await OpenApplyRouteAsync();

    // Apply Route for the selected aircraft, modal over the editor, once a field still being typed in is committed, so the
    // stamp starts from the aircraft as shown; nothing with no aircraft selected or one already open.
    private async Task OpenApplyRouteAsync()
    {
        if (_applyRouteOpen)
        {
            return;
        }

        GeneralView.CommitFocusedField();
        AircraftView.CommitFocusedField();
        if ((Editor.Aircraft is not { } aircraft) || (Editor.CreateApplyRoute() is not { } applyRoute))
        {
            return;
        }

        _applyRouteOpen = true;
        try
        {
            var window = ApplyRouteWindow.Create(applyRoute, aircraft, _preferences, () => Editor.CreateApplyRouteFinder(applyRoute));
            await window.ShowAsync(this);
        }
        finally
        {
            _applyRouteOpen = false;
        }
    }

    // The open copy's name for a satellite's title; its id when the name is empty.
    private string OpenCopyName() => (Editor.General is { Name: { Length: > 0 } name }) ? name : (Editor.Session?.ScenarioId ?? "");

    // The window disposed its planner; the editor stops refreshing it.
    private void OnSpawnPlannerClosed(object? sender, EventArgs e)
    {
        if (sender is not SpawnPlannerWindow window)
        {
            return;
        }

        window.Closed -= OnSpawnPlannerClosed;
        if (window.DataContext is SpawnPlannerViewModel planner)
        {
            Editor.ReleaseSpawnPlanner(planner);
        }

        if (ReferenceEquals(window, _spawnPlannerWindow))
        {
            _spawnPlannerWindow = null;
        }
    }

    private void OnRouteComposerClosed(object? sender, EventArgs e)
    {
        if (sender is not RouteComposerWindow window)
        {
            return;
        }

        window.Closed -= OnRouteComposerClosed;
        if (ReferenceEquals(window, _routeComposerWindow))
        {
            _routeComposerWindow = null;
        }
    }

    private void OnSatellitesClosing(object? sender, EventArgs e) => CloseSatellites();

    private void CloseSatellites()
    {
        _spawnPlannerWindow?.Close();
        _routeComposerWindow?.Close();
    }
}
