using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Customization > Settings > Importing and exporting settings.
// The hub as Tools > Import / Export... opens it: the Export tab, nothing
// ticked. It reads the capture run's own preferences and favorites, which
// ModuleInit keeps in a temp folder, and the scene never exports or imports.
internal sealed class ImportExportHubScene : StandaloneWindowSceneBase
{
    public override string Name => "import-export-hub";

    public override Window CreateWindow(CaptureContext ctx)
    {
        var preferences = new UserPreferences();
        var favorites = new FavoriteStore(FavoriteStore.DefaultRootDir);
        return new ImportExportWindow(
            preferences,
            new UserPreferencesImportTarget(preferences, favorites),
            new UserPreferencesExportSource(preferences, favorites),
            new HashSet<SettingsItemType>(),
            ImportExportTab.Export
        );
    }
}
