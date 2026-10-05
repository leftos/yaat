using Yaat.GuideCapture.Scenes;

namespace Yaat.GuideCapture.Capture;

internal static class SceneCatalog
{
    // Every scene the harness captures, in capture order, grouped by the guide
    // section that shows it. A scene's Name is its PNG file name.
    public static IReadOnlyList<Scene> All { get; } =
    [
        // Interface Overview
        new MainWindowEmptyScene(),
        new MainWindowConnectedEmptyScene(),
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
        // Popouts
        new MainWindowPoppedOutScene(),
        new GroundViewPopoutScene(),
        new RadarViewPopoutScene(),
        // Strips + flight plan editor
        new FlightStripsScene(),
        new FlightPlanEditorScene(),
        new FavoritesBarScene(),
        new FavoritesPanelScene(),
        // Standalone dialogs / windows
        new SettingsWindowScene(),
        new LoadScenarioDialogScene(),
        new LoadWeatherDialogScene(),
        new WeatherEditorScene(),
        new ArrivalGeneratorsEditorScene(),
        new AboutWindowScene(),
    ];
}
