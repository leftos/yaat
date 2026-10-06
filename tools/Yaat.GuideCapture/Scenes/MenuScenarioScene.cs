using Avalonia.Controls;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// GETTING_STARTED.md > Step 4. Connected, in a room with the OAK scenario
// loaded, and the Scenario menu open so Load Scenario and the weather items
// show.
internal sealed class MenuScenarioScene : ScenarioSceneBase
{
    public override string Name => "menu-scenario";

    protected override int TabIndex => 0;

    protected override Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx) =>
        SceneActions.OpenMenuAsync(window, "_Scenario", TimeSpan.FromSeconds(5));
}
