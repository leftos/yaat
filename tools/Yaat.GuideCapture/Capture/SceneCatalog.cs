using Yaat.GuideCapture.Scenes;

namespace Yaat.GuideCapture.Capture;

internal static class SceneCatalog
{
    // Every scene the harness captures, in capture order, grouped by the guide
    // section that shows it. A scene's Name is its PNG file name. The popout
    // scenes run after every other main-window scene: the pop-out state they
    // set is saved to preferences and restored by every later MainWindow,
    // which would drop the Ground and Radar tabs from those shots.
    public static IReadOnlyList<Scene> All { get; } =
    [
        // Interface Overview
        new MainWindowEmptyScene(),
        new MainWindowConnectedEmptyScene(),
        new TerminalPanelScene(),
        new CommandBarScene(),
        // Getting Started
        new MenuFileScene(),
        new ConnectDialogScene(),
        new MenuScenarioScene(),
        new FirstCommandScene(),
        new MainWindowWithScenarioScene(),
        // Views
        new AircraftListScene(),
        new GroundViewScene(),
        new RadarViewScene(),
#if HAS_YAAT_SERVER
        new LiveTrafficScene(),
#endif
        // Strips, vTDLS + flight plan editor
        new FlightStripsScene(),
        new VtdlsTabScene(),
        new FlightPlanEditorScene(),
        // Command Input > Favorite Commands
        new FavoritesBarScene(),
        new FavoritesPanelScene(),
        // Scenarios and Weather (connected) + Simulation Controls
        new MetarWindowScene(),
        new ExportRoomScenarioScene(),
        // Popouts
        new MainWindowPoppedOutScene(),
        new GroundViewPopoutScene(),
        new RadarViewPopoutScene(),
        // Standalone dialogs / windows
        new SettingsWindowScene(),
        new LoadScenarioDialogScene(),
        new LoadWeatherDialogScene(),
        new WeatherEditorScene(),
        new ArrivalGeneratorsEditorScene(),
        new FileBugReportDialogScene(),
        new AboutWindowScene(),
    ];
}
