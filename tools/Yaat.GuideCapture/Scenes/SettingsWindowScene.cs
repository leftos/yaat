using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Customization > Settings.
internal sealed class SettingsWindowScene : StandaloneWindowSceneBase
{
    public override string Name => "settings-window";

    public override Window CreateWindow(CaptureContext ctx) => new SettingsWindow();
}
