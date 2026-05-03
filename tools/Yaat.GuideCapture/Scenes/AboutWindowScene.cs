using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Help > About. Bonus screenshot.
internal sealed class AboutWindowScene : StandaloneWindowSceneBase
{
    public override string Name => "about-window";

    public override Window CreateWindow(CaptureContext ctx) => new AboutWindow();
}
