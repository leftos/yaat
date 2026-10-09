using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > active runways. The OAK scenario's room given active
// runways for two airports with ARWY, then the Scenario › Active Runways…
// window opened on it the way the Scenario menu opens it, one row per airport.
internal sealed class WhatsNewActiveRunwaysScene : ScenarioSceneBase
{
    private static readonly string[] Commands = ["ARWY OAK 28L 28R", "ARWY SFO 28L 28R 01L 01R"];

    private ActiveRunwaysWindow? _window;

    public override string Name => "whats-new-active-runways";

    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        foreach (string command in Commands)
        {
            CommandResultDto result = await vm.Connection.SendCommandAsync("", command, vm.Preferences.UserInitials);
            if (!result.Success)
            {
                throw new InvalidOperationException($"'{command}' was refused: {result.Message}");
            }
        }

        // Each ARWY replaces the room's whole list, in send order, so the second
        // airport arriving means the first one's change has landed too.
        await SceneActions.WaitUntilAsync(
            () => vm.RoomActiveRunways.ContainsKey("SFO"),
            TimeSpan.FromSeconds(10),
            "the room's active runways to list SFO"
        );

        _window = new ActiveRunwaysWindow(
            new ActiveRunwaysWindowViewModel(vm),
            vm.Preferences,
            command => vm.Connection.SendCommandAsync("", command, vm.Preferences.UserInitials)
        );
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        _window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _window ?? primary;

    public override IEnumerable<Window> ExtraWindows => _window is null ? [] : [_window];
}
