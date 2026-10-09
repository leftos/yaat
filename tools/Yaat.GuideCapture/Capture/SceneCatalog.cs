using Yaat.GuideCapture.Scenes;

namespace Yaat.GuideCapture.Capture;

internal static class SceneCatalog
{
    // Every scene the harness captures, in capture order, grouped by the guide
    // section that shows it. A scene's Name is its PNG file name. The popout
    // scene runs after every other main-window scene: the pop-out state it
    // sets is saved to preferences and restored by every later MainWindow,
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
        // Applying a Layout (adds a saved layout, deleted in AfterCapture)
        new MenuViewLayoutsScene(),
        // Command Input > Favorite Commands
        new FavoritesBarScene(),
        new FavoritesPanelScene(),
        // Scenarios and Weather (connected) + Simulation Controls
        new MetarWindowScene(),
        new ExportRoomScenarioScene(),
        // Timeline / Rewind + Bookmarks + Terminal > Scrub to a Moment
        new TimelinePlaybackScene(),
        new BookmarksListScene(),
        new TakeControlDialogScene(),
        new TerminalRewindMenuScene(),
        // Ground View: a taxi route and a landing roll-out
        new GroundTaxiRouteScene(),
        new JustLandedScene(),
        // Popouts
        new MainWindowPoppedOutScene(),
        // Standalone dialogs / windows
        new SettingsWindowScene(),
        new SettingsRadarScene(),
        new SettingsGroundScene(),
        new SettingsQuickCommandsScene(),
        new SettingsKeysScene(),
        new ImportExportHubScene(),
        new LoadScenarioDialogScene(),
        new LoadWeatherDialogScene(),
        new WeatherEditorScene(),
        new ArrivalGeneratorsEditorScene(),
        new FileBugReportDialogScene(),
        // Feature showcase (docs/releases/): left out of a run without --scene
        // and captured one at a time with --scene whats-new-<topic>
        new WhatsNewActiveRunwaysScene(),
        new WhatsNewScenarioDefaultsScene(),
        new WhatsNewRblLabelScene(),
        new WhatsNewQuickCommandsScene(),
        new WhatsNewPointMenuScene(),
        new WhatsNewPickerScene(),
        new WhatsNewExitsAheadScene(),
        new WhatsNewTaxiToRunwayScene(),
    ];

    // The scenes a run captures: the one named by --scene (case-insensitive),
    // or with no --scene every scene except the showcase ones.
    public static IReadOnlyList<Scene> Select(string? sceneFilter) =>
        sceneFilter is null
            ? [.. All.Where(s => !s.IsShowcase)]
            : [.. All.Where(s => string.Equals(s.Name, sceneFilter, StringComparison.OrdinalIgnoreCase))];
}
