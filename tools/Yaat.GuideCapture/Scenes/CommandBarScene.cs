using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Interface Overview > Command Bar. A close-up of the command
// bar in a small host window, bound to the connected main window's view model
// (S3-NCTC-3 loaded, an airborne aircraft selected), with a partly typed
// command so its signature help and suggestion list show above the input.
// The host window is tall enough for the popup, which opens upward.
internal sealed class CommandBarScene : ScenarioSceneBase
{
    private const string PartialCommand = "DCT ";

    private Window? _host;

    public override string Name => "command-bar";

    protected override int TabIndex => 0;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(
            () => vm.AircraftView.OfType<AircraftModel>().Any(a => (!a.IsOnGround) && (!a.IsDelayed)),
            TimeSpan.FromSeconds(10),
            "an active airborne aircraft in the Aircraft List"
        );
        vm.SelectedAircraft = vm.AircraftView.OfType<AircraftModel>().First(a => (!a.IsOnGround) && (!a.IsDelayed));
        Dispatcher.UIThread.RunJobs();

        var input = new CommandInputView { DataContext = vm, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        _host = new Window
        {
            Title = "Command Bar",
            Width = 900,
            Height = 360,
            Content = input,
        };
        _host.Show();
        Dispatcher.UIThread.RunJobs();
        _host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.CommandText = PartialCommand;
        vm.CommandCaretIndex = PartialCommand.Length;
        Dispatcher.UIThread.RunJobs();
        await SceneActions.WaitUntilAsync(
            () => (vm.CommandInput.IsPopupVisible) && (vm.CommandInput.IsSuggestionsVisible),
            TimeSpan.FromSeconds(5),
            $"the command popup for '{PartialCommand}'"
        );
        _host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _host ?? primary;

    public override IEnumerable<Window> ExtraWindows => _host is null ? [] : [_host];
}
