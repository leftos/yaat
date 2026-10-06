using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Simulation Controls > Filing a Bug Report. The File Bug
// Report dialog with an empty form, as opened from a room (its note says the
// bundle attaches the session recording).
internal sealed class FileBugReportDialogScene : StandaloneWindowSceneBase
{
    public override string Name => "file-bug-report-dialog";

    public override Window CreateWindow(CaptureContext ctx) => new FileBugReportDialog(attachesRecording: true);

    // The dialog focuses its Title box on open, and that box's blinking caret
    // would be caught on or off at random. With the carets hidden every run
    // captures the same frame; the focused box keeps its focus border.
    public override Task AfterShowAsync(Window window, CaptureContext ctx)
    {
        foreach (TextBox box in window.GetVisualDescendants().OfType<TextBox>())
        {
            box.CaretBrush = Brushes.Transparent;
        }
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }
}
