using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Interface Overview > Terminal Panel > Scrub to a Moment. The
// terminal panel in a small host window, as the terminal-panel scene hosts it,
// with a line's right-click menu open on "Rewind to 1:30". The S3-NCTC-3 room
// is run forward a paused 1:30 before the first-command exchange (FH 270 to an
// airborne aircraft), so the aircraft's response carries that scenario time;
// the timeline bar is turned on, since the menu offers the rewind only when the
// timeline is available. The right-click is real pointer input on the
// response line.
internal sealed class TerminalRewindMenuScene : TimelineSceneBase
{
    private Window? _host;

    public override string Name => "terminal-rewind-menu";

    protected override int TabIndex => 0;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        ShowTimelineBar(vm);
        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: 90, secondsPerStep: 15);
        AircraftModel aircraft = await FirstCommandScene.SendFirstCommandAsync(vm);

        var panel = new TerminalPanelView { DataContext = vm };
        _host = new Window
        {
            Title = "Terminal",
            Width = 1000,
            Height = 300,
            Content = panel,
        };
        _host.Show();
        Dispatcher.UIThread.RunJobs();
        _host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        TextEditor editor =
            panel.FindControl<TextEditor>("TerminalEditor") ?? throw new InvalidOperationException("TerminalPanelView has no TerminalEditor");
        ScrollViewer log =
            editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidOperationException("TerminalEditor has no ScrollViewer");
        log.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        _host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Point click = ResponseLinePoint(editor, _host, aircraft.Callsign);
        _host.MouseMove(click);
        _host.MouseDown(click, MouseButton.Right);
        _host.MouseUp(click, MouseButton.Right);

        ContextMenu menu = editor.ContextMenu ?? throw new InvalidOperationException("The terminal has no context menu.");
        await SceneActions.WaitUntilAsync(
            () =>
                menu.IsOpen
                && menu.Items.OfType<MenuItem>().All(m => (TopLevel.GetTopLevel(m) is not null) && m.IsArrangeValid && (m.Bounds.Width > 0)),
            TimeSpan.FromSeconds(5),
            "the terminal's context menu to open"
        );

        MenuItem rewind = menu.Items.OfType<MenuItem>().First();
        if (!rewind.IsEnabled || (rewind.Header is not string header) || (!header.StartsWith("Rewind to ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The terminal's context menu offers '{rewind.Header}' (enabled: {rewind.IsEnabled}), not a rewind to the line's time."
            );
        }
    }

    public override Window GetCaptureTarget(Window primary) => _host ?? primary;

    public override IEnumerable<Window> ExtraWindows => _host is null ? [] : [_host];

    // The middle of the newest terminal line that names the callsign (the
    // aircraft's response), in the host window's coordinates.
    private static Point ResponseLinePoint(TextEditor editor, Window host, string callsign)
    {
        TextDocument document = editor.Document;
        int lineNumber = 0;
        for (int n = document.LineCount; n >= 1; n--)
        {
            if (document.GetText(document.GetLineByNumber(n)).Contains(callsign, StringComparison.Ordinal))
            {
                lineNumber = n;
                break;
            }
        }
        if (lineNumber == 0)
        {
            throw new InvalidOperationException($"No terminal line names {callsign}.");
        }

        TextView textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        Point inDocument = textView.GetVisualPosition(new TextViewPosition(lineNumber, 12), VisualYPosition.LineMiddle);
        Point inTextView = inDocument - textView.ScrollOffset;
        return textView.TranslatePoint(inTextView, host)
            ?? throw new InvalidOperationException("The terminal's text view is not in the host window.");
    }
}
