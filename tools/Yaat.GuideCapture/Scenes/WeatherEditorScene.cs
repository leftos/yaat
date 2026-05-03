using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Scenarios and Weather > Weather Editor.
internal sealed class WeatherEditorScene : StandaloneWindowSceneBase
{
    public override string Name => "weather-editor";

    public override Window CreateWindow(CaptureContext ctx) => new WeatherTimelineEditorWindow();
}
