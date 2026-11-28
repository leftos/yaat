using Avalonia.Interactivity;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Tools menu's satellite windows: each opens once, modeless and owned by the editor, a second click brings it
/// forward, and it closes when the open copy closes or changes and when the editor closes.
/// </summary>
public partial class ScenarioEditorWindow
{
    private SpawnPlannerWindow? _spawnPlannerWindow;

    private void WireSatellites()
    {
        SpawnPlannerMenuItem.Click += OnSpawnPlannerClick;
        Editor.SatellitesClosing += OnSatellitesClosing;
    }

    private void UnwireSatellites()
    {
        SpawnPlannerMenuItem.Click -= OnSpawnPlannerClick;
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

    private void OnSatellitesClosing(object? sender, EventArgs e) => CloseSatellites();

    private void CloseSatellites() => _spawnPlannerWindow?.Close();
}
