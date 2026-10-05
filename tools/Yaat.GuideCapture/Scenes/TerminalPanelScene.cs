using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Interface Overview > Terminal Panel. A close-up of the
// terminal panel in a small host window, bound to the connected main window's
// view model after the first-command exchange (S3-NCTC-3 loaded, FH 270 sent
// to an airborne aircraft), so it shows the room and load lines, the command
// echo and the aircraft's response.
internal sealed class TerminalPanelScene : ScenarioSceneBase
{
    private Window? _host;

    public override string Name => "terminal-panel";

    protected override int TabIndex => 0;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await FirstCommandScene.SendFirstCommandAsync(vm);

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

        // A freshly loaded panel renders the whole log from the top; scroll to
        // the newest lines, where the command and its response are.
        // The log's own ScrollViewer sits inside the TerminalEditor; the filter
        // box ahead of it has one too.
        Control editor =
            panel.FindControl<Control>("TerminalEditor") ?? throw new InvalidOperationException("TerminalPanelView has no TerminalEditor");
        ScrollViewer log =
            editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidOperationException("TerminalEditor has no ScrollViewer");
        log.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        _host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _host ?? primary;

    public override IEnumerable<Window> ExtraWindows => _host is null ? [] : [_host];
}
