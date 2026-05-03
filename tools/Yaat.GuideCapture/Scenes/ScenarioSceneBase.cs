using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Most "scene with X tab visible" captures share the same setup: connect →
// create room → load the OAK clearances scenario → switch a tab → settle. This
// base class owns the boilerplate so concrete scenes only declare a name, a
// tab index, and any extra UI tweaks via OnSceneReadyAsync.
internal abstract class ScenarioSceneBase : Scene
{
    public override TimeSpan SettleAfterShow => TimeSpan.FromSeconds(2);

    protected abstract int TabIndex { get; }

    // Default fixture: S1-OAK-1 Clearances Intro (18 aircraft on OAK parking).
    // Override per-scene when a different scenario fits better — e.g. radar
    // scenes use S3-NCTC-3 because it has 59 airborne aircraft in NCT TRACON
    // airspace, not 18 stopped on a ramp.
    protected virtual string ScenarioFile => "01H06NVK7VN8BS7MCDXHKJZ7MQ.json";

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

        vm.SelectedTabIndex = TabIndex;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        await OnSceneReadyAsync(window, vm, ctx);
    }

    // Hook for scenes that need to do extra UI manipulation after the tab is
    // switched and the basic scenario state is loaded (e.g. select an aircraft,
    // open a flyout, type into the command bar).
    protected virtual Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx) => Task.CompletedTask;
}
