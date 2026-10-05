using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Simulation Controls > Timeline / Rewind. From the
// timeline-playback state, Take Control is invoked the way the bar's button
// invokes it, and the confirmation MainWindow shows before ending playback is
// captured. Once the capture is taken the dialog is cancelled, so the room
// stays in playback, and the command the cancel completes is checked so a
// failure surfaces instead of being dropped.
internal sealed class TakeControlDialogScene : TimelineSceneBase
{
    private const string DialogTitle = "End playback and take control?";

    private Window? _dialog;
    private Task? _takeControlCommand;

    public override string Name => "take-control-dialog";

    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await StagePlaybackAsync(vm, ctx);

        // Not awaited: the command waits on the confirmation until the dialog closes. The task is kept
        // so the cancel below can observe it.
        _takeControlCommand = vm.TakeControlCommand.ExecuteAsync(null);
        await SceneActions.WaitUntilAsync(
            () => FindDialog(window) is { IsArrangeValid: true, Bounds.Width: > 0 },
            TimeSpan.FromSeconds(5),
            $"the '{DialogTitle}' dialog to open"
        );
        _dialog = FindDialog(window);
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _dialog ?? primary;

    public override void AfterCapture()
    {
        try
        {
            if (_dialog is { IsVisible: true })
            {
                Button cancel =
                    _dialog.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => (b.Content is string content) && (content == "Cancel"))
                    ?? throw new InvalidOperationException($"The '{DialogTitle}' dialog has no Cancel button.");
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                if (_takeControlCommand is { IsFaulted: true })
                {
                    throw new InvalidOperationException("Take Control failed after the dialog was cancelled.", _takeControlCommand.Exception);
                }
            }
        }
        finally
        {
            base.AfterCapture();
        }
    }

    public override IEnumerable<Window> ExtraWindows => _dialog is { IsVisible: true } ? [_dialog] : [];

    private static Window? FindDialog(Window owner) => owner.OwnedWindows.FirstOrDefault(w => w.Title == DialogTitle);
}
