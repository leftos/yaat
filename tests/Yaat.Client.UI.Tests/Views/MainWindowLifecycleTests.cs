using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Xunit;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for the MainWindow <-> MainViewModel observable-driven window
// lifecycle (recent commits 04cdd67/f30f6b3/c85a6e2/7f9dd7c — per-facility
// strips, collapse-when-all-popped-out).
public class MainWindowLifecycleTests
{
    [AvaloniaFact]
    public void MainWindow_BootsInHeadless()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.IsType<MainViewModel>(window.DataContext);
    }

    [AvaloniaFact]
    public void DataGridPopOut_CreatesAndClosesSubordinateWindow()
    {
        var (main, vm) = BootMainWindow();
        // Start from a known docked state — ignores whatever the user's saved
        // UserPreferences say about the initial pop-out state.
        vm.IsDataGridPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(main.DataGridWindow);

        vm.IsDataGridPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(main.DataGridWindow);
        Assert.True(main.DataGridWindow!.IsVisible);

        vm.IsDataGridPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(main.DataGridWindow);
    }

    [AvaloniaFact]
    public void GroundAndRadarPopOut_EachCreatesItsOwnWindow()
    {
        var (main, vm) = BootMainWindow();
        vm.IsGroundViewPoppedOut = false;
        vm.IsRadarViewPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        vm.IsGroundViewPoppedOut = true;
        vm.IsRadarViewPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(main.GroundViewWindow);
        Assert.NotNull(main.RadarViewWindow);
        Assert.True(main.GroundViewWindow!.IsVisible);
        Assert.True(main.RadarViewWindow!.IsVisible);

        vm.IsGroundViewPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(main.GroundViewWindow);
        Assert.NotNull(main.RadarViewWindow); // other window untouched
    }

    [AvaloniaFact]
    public void AllTabsPoppedOut_CollapsesContentGrid()
    {
        var (_, vm) = BootMainWindow();
        // Normalise initial state (ignore user's prefs).
        vm.IsDataGridPoppedOut = false;
        vm.IsGroundViewPoppedOut = false;
        vm.IsRadarViewPoppedOut = false;
        vm.StripsEntries[0].IsPoppedOut = false;
        vm.IsTerminalDocked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsAnyTabVisible);
        Assert.True(vm.IsContentGridVisible);

        vm.IsDataGridPoppedOut = true;
        vm.IsGroundViewPoppedOut = true;
        vm.IsRadarViewPoppedOut = true;
        // Student strips entry is index 0 and must also be popped out for the
        // "every tab popped out" collapse case (commit f30f6b3).
        vm.StripsEntries[0].IsPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsAnyTabVisible);
        // Terminal is still docked, so the content grid remains visible overall.
        Assert.True(vm.IsContentGridVisible);

        // Undocking the terminal collapses the entire content grid — menu bar only.
        vm.IsTerminalDocked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsContentGridVisible);
    }

    [AvaloniaFact]
    public void StripsEntry_PopOut_CreatesFacilityWindow()
    {
        var (main, vm) = BootMainWindow();
        var studentEntry = vm.StripsEntries[0];
        studentEntry.IsPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(main.StripsWindows);

        studentEntry.IsPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(main.StripsWindows);
        Assert.True(main.StripsWindows[studentEntry].IsVisible);

        studentEntry.IsPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(main.StripsWindows);
    }

    private static (MainWindow main, MainViewModel vm) BootMainWindow()
    {
        var main = new MainWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (main, (MainViewModel)main.DataContext!);
    }
}
