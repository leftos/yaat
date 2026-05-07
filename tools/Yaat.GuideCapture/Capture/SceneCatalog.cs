using Yaat.GuideCapture.Scenes;

namespace Yaat.GuideCapture.Capture;

internal static class SceneCatalog
{
    // Static catalog. Phase A starts with one proof-of-pipeline scene; Phase D
    // populates the full list (~28 scenes — see plan).
    public static IReadOnlyList<Scene> All { get; } =
    [
        // Interface Overview
        new MainWindowEmptyScene(),
        new MainWindowConnectedEmptyScene(),
        new MainWindowWithScenarioScene(),
        // Views
        new AircraftListScene(),
        new GroundViewScene(),
        new RadarViewScene(),
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
