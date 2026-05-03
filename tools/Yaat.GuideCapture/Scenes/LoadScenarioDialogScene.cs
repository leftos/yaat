using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Scenarios and Weather > Loading a Scenario. Captures the
// dialog itself in its initial state (no live vNAS data fetched — that
// requires network and isn't reproducible in CI).
internal sealed class LoadScenarioDialogScene : StandaloneWindowSceneBase
{
    public override string Name => "load-scenario-dialog";

    public override Window CreateWindow(CaptureContext ctx) => new LoadScenarioWindow();
}
