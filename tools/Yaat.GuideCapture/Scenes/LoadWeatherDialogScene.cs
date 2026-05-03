using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Scenarios and Weather > Loading a Weather Profile.
internal sealed class LoadWeatherDialogScene : StandaloneWindowSceneBase
{
    public override string Name => "load-weather-dialog";

    public override Window CreateWindow(CaptureContext ctx) => new LoadWeatherWindow();
}
