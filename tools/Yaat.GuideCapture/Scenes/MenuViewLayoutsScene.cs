using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Applying a Layout or Another Scenario's Views. The
// disconnected main window with View > Layout open, listing one saved layout
// the scene saves first and deletes in AfterCapture.
internal sealed class MenuViewLayoutsScene : Scene
{
    private const string LayoutName = "Tower cab";

    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(5);

    private UserPreferences? _preferences;

    public override string Name => "menu-view-layouts";

    public override Window CreateWindow(CaptureContext ctx) => new MainWindow();

    public override async Task AfterShowAsync(Window window, CaptureContext ctx)
    {
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow.DataContext is not MainViewModel");
        }

        _preferences = vm.Preferences;
        _preferences.SaveLayout(new SavedLayout { Name = LayoutName });
        Dispatcher.UIThread.RunJobs();

        MenuItem view = await SceneActions.OpenMenuAsync(window, "_View", MenuTimeout);
        await SceneActions.OpenSubmenuAsync(view, "_Layout", MenuTimeout);
    }

    public override void AfterCapture()
    {
        _preferences?.DeleteLayout(LayoutName);
        _preferences = null;
    }
}
