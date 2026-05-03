using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Phase A proof-of-pipeline: boot a disconnected MainWindow with no server,
// no scenario, no scenario-derived state. Confirms the headless real-Skia
// rendering path produces a non-blank Yaat UI image.
internal sealed class MainWindowEmptyScene : Scene
{
    public override string Name => "main-window-empty";

    public override Window CreateWindow(CaptureContext ctx) => new MainWindow();
}
