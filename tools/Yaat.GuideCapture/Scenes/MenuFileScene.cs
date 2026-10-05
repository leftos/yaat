using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// GETTING_STARTED.md > Step 3. The disconnected main window (as
// main-window-empty) with the File menu open, so Connect... shows.
internal sealed class MenuFileScene : Scene
{
    public override string Name => "menu-file";

    public override Window CreateWindow(CaptureContext ctx) => new MainWindow();

    public override Task AfterShowAsync(Window window, CaptureContext ctx) => SceneActions.OpenMenuAsync(window, "_File", TimeSpan.FromSeconds(5));
}
