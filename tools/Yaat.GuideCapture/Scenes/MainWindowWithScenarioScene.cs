using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Phase C smoke test: connect → create room → load an OAK scenario with 18
// aircraft on parking spots → switch to the Ground View tab. The aircraft
// dots on the OAK ramp are rendered by GroundCanvas via the same
// MapDrawOperation path as production, so a non-blank ground render proves
// real-Skia headless drives the custom DrawingContext.Custom() pipeline
// end-to-end.
internal sealed class MainWindowWithScenarioScene : Scene
{
    private const string ScenarioFile = "01H06NVK7VN8BS7MCDXHKJZ7MQ.json";

    // Give the ground view's snapshot timer + initial layout a chance to fire
    // after we switch tabs. The timer fires every 100ms.
    public override TimeSpan SettleAfterShow => TimeSpan.FromSeconds(2);

    public override string Name => "main-window-with-scenario";

    public override Task BeforeWindowAsync(CaptureContext ctx)
    {
        App.AutoConnectTarget = ctx.ServerUrl;
        return Task.CompletedTask;
    }

    public override Window CreateWindow(CaptureContext ctx) => new MainWindow();

    public override async Task AfterShowAsync(Window window, CaptureContext ctx)
    {
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow.DataContext is not MainViewModel");
        }

        await SceneActions.WaitForConnectionAsync(vm, TimeSpan.FromSeconds(15));
        await SceneActions.CreateRoomAsync(vm, TimeSpan.FromSeconds(10));

        var scenarioPath = Path.Combine(ctx.RepoRoot, "docs", "atctrainer-scenario-examples", ScenarioFile);
        await SceneActions.LoadScenarioAsync(vm, scenarioPath, TimeSpan.FromSeconds(30));

        // Switch to the Ground View tab. Aircraft List = 0, Ground View = 1,
        // Radar View = 2 — see MainWindow.axaml.
        vm.SelectedTabIndex = 1;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
