using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for the exit/confirm-close decision (GitHub #347). File > Exit used to call the
// force variant of desktop.Shutdown(), which ignores OnClosing's e.Cancel — the confirm-exit
// dialog was bypassed AND the if-close-proceeds teardown (global key hook dispose) never ran,
// leaving the SharpHook native thread alive through CLR shutdown. Exit now routes through
// MainWindow.Close() so OnClosing governs, and the confirm dialog is gated on the close
// reason so OS/application shutdown is never blocked by (or bypasses) it.
public class MainWindowExitFlowTests
{
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, false, false, true, true)] // user close + scenario → confirm
    [InlineData(WindowCloseReason.WindowClosing, true, false, true, true)] // programmatic Close() (File > Exit) → confirm
    [InlineData(WindowCloseReason.Undefined, false, false, true, true)] // platform gave no reason → treat as user close
    [InlineData(WindowCloseReason.WindowClosing, false, true, true, false)] // already confirmed → proceed
    [InlineData(WindowCloseReason.WindowClosing, false, false, false, false)] // no scenario → nothing to confirm
    [InlineData(WindowCloseReason.ApplicationShutdown, false, false, true, true)] // macOS Cmd+Q (non-forced, cancel honored) → confirm
    [InlineData(WindowCloseReason.ApplicationShutdown, true, false, true, false)] // programmatic Shutdown() ignores cancel — never dialog
    [InlineData(WindowCloseReason.OSShutdown, false, false, true, false)] // OS shutdown must not be blocked
    [InlineData(WindowCloseReason.OwnerWindowClosing, false, false, true, false)] // owner cascade → owner already decided
    public void ShouldConfirmExit_GatesOnCloseReasonAndOrigin(
        WindowCloseReason reason,
        bool isProgrammatic,
        bool isConfirmedClose,
        bool hasActiveScenario,
        bool expected
    )
    {
        Assert.Equal(expected, MainWindow.ShouldConfirmExit(reason, isProgrammatic, isConfirmedClose, hasActiveScenario));
    }

    [AvaloniaFact]
    public void CloseWithActiveScenario_CancelsAndStaysOpen()
    {
        var main = new MainWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        var vm = (MainViewModel)main.DataContext!;

        // Fake an in-room scenario session so OnClosing wants confirmation.
        vm.IsConnected = true;
        vm.ActiveRoomId = "room-1";
        vm.ActiveScenarioId = "scenario-1";
        Dispatcher.UIThread.RunJobs();

        // Close() raises OnClosing with WindowCloseReason.WindowClosing; the confirm-exit path
        // must cancel the close (the dialog is now pending) and leave the window open. The test
        // deliberately never confirms — letting the close proceed would latch the process-wide
        // AppLifetime.IsShuttingDown flag and poison every later test in this process.
        main.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(main.IsVisible);
    }
}
