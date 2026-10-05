using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Scenarios and Weather > Viewing METARs. The METAR window,
// opened as MainWindow does (its own window bound to the main view model),
// once the OAK clearances scenario's weather has reached the client.
internal sealed class MetarWindowScene : ScenarioSceneBase
{
    private MetarWindow? _metarWindow;

    public override string Name => "metar-window";

    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(() => vm.Metars.Count > 0, TimeSpan.FromSeconds(10), "the scenario's METARs");

        _metarWindow = new MetarWindow(vm.Preferences) { DataContext = vm };
        _metarWindow.Show();
        Dispatcher.UIThread.RunJobs();
        _metarWindow.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _metarWindow ?? primary;

    public override IEnumerable<Window> ExtraWindows => _metarWindow is null ? [] : [_metarWindow];
}
