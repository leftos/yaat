using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

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

        // Every pop-out flip persists to the shared per-process preferences.json, so leave the
        // state docked — a later test's MainWindow would otherwise boot with the view popped out.
        vm.IsRadarViewPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
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
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsAnyTabVisible);
        Assert.True(vm.IsContentGridVisible);

        vm.IsDataGridPoppedOut = true;
        vm.IsGroundViewPoppedOut = true;
        vm.IsRadarViewPoppedOut = true;
        vm.IsControllersPoppedOut = true;
        vm.IsMetarPoppedOut = true;
        // Student strips entry is index 0 and must also be popped out for the
        // "every tab popped out" collapse case (commit f30f6b3). The TDLS
        // student entry — added once vTDLS landed — counts the same way.
        vm.StripsEntries[0].IsPoppedOut = true;
        vm.TdlsEntries[0].IsPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsAnyTabVisible);
        // Terminal is still docked, so the content grid remains visible overall.
        Assert.True(vm.IsContentGridVisible);

        // Undocking the terminal collapses the entire content grid — menu bar only.
        vm.IsTerminalPoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsContentGridVisible);

        // Every pop-out flip persists to the shared per-process preferences.json, so re-dock
        // everything — a later test's MainWindow would otherwise boot with all views popped out
        // and no docked GroundCanvas/RadarCanvas to find.
        vm.IsDataGridPoppedOut = false;
        vm.IsGroundViewPoppedOut = false;
        vm.IsRadarViewPoppedOut = false;
        vm.IsControllersPoppedOut = false;
        vm.IsMetarPoppedOut = false;
        vm.StripsEntries[0].IsPoppedOut = false;
        vm.TdlsEntries[0].IsPoppedOut = false;
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
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

    [AvaloniaFact]
    public void RadarPopOut_ClosedViaWindowChrome_UndocksWithoutReenteringClose()
    {
        var (main, vm) = BootMainWindow();
        vm.IsRadarViewPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        vm.IsRadarViewPoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        var popOut = main.RadarViewWindow;
        Assert.NotNull(popOut);

        // Close the pop-out window itself — the native title-bar X path (#347). The Closing
        // handler flips IsRadarViewPoppedOut, whose PropertyChanged fan-out re-enters
        // CloseRadarViewWindow on this same call stack; it must no-op instead of calling
        // Close() again on the window that is already inside its own Closing event.
        popOut!.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsRadarViewPoppedOut);
        Assert.Null(main.RadarViewWindow);
    }

    [AvaloniaFact]
    public void StripsEntryPopOut_ClosedViaWindowChrome_UndocksWithoutReenteringClose()
    {
        var (main, vm) = BootMainWindow();
        var studentEntry = vm.StripsEntries[0];
        studentEntry.IsPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        studentEntry.IsPoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        var window = main.StripsWindows[studentEntry];

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(studentEntry.IsPoppedOut);
        Assert.Empty(main.StripsWindows);
    }

    [AvaloniaFact]
    public void ExtraRadarWindow_OpensWithInstanceVmAndMainViewModelContext()
    {
        var (main, vm) = BootMainWindow();
        try
        {
            vm.OpenExtraRadarViewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var instance = Assert.Single(vm.ExtraRadarViews);
            var entry = Assert.Single(main.ExtraRadarWindows);
            Assert.Same(instance, entry.Key);

            // The window DataContext stays the MainViewModel (the inner view binds Aircraft through
            // $parent[Window]); only the hosted RadarView gets the per-instance view-model.
            Assert.IsType<MainViewModel>(entry.Value.DataContext);
            Assert.Same(instance.Vm, entry.Value.RadarVm);
            Assert.NotSame(vm.Radar, entry.Value.RadarVm);
            Assert.Equal("Radar View #2", entry.Value.Title);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
    }

    [AvaloniaFact]
    public void ExtraGroundWindow_OpensWithInstanceVmAndMainViewModelContext()
    {
        var (main, vm) = BootMainWindow();
        try
        {
            vm.OpenExtraGroundViewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var instance = Assert.Single(vm.ExtraGroundViews);
            var entry = Assert.Single(main.ExtraGroundWindows);
            Assert.Same(instance, entry.Key);

            Assert.IsType<MainViewModel>(entry.Value.DataContext);
            Assert.Same(instance.Vm, entry.Value.GroundVm);
            Assert.NotSame(vm.Ground, entry.Value.GroundVm);
            Assert.Equal("Ground View #2", entry.Value.Title);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
    }

    [AvaloniaFact]
    public void ExtraRadarWindow_ClosedViaChrome_RemovesInstanceWithoutReenteringClose()
    {
        var (main, vm) = BootMainWindow();
        try
        {
            vm.OpenExtraRadarViewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var window = main.ExtraRadarWindows.Values.Single();

            // Title-bar X: the Closing handler drops the window BEFORE removing the instance, whose
            // CollectionChanged fan-out re-enters the close path on this same call stack.
            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(vm.ExtraRadarViews);
            Assert.Empty(main.ExtraRadarWindows);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
    }

    [AvaloniaFact]
    public void ExtraGroundWindow_ClosedViaChrome_RemovesInstanceWithoutReenteringClose()
    {
        var (main, vm) = BootMainWindow();
        try
        {
            vm.OpenExtraGroundViewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var window = main.ExtraGroundWindows.Values.Single();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(vm.ExtraGroundViews);
            Assert.Empty(main.ExtraGroundWindows);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
    }

    [AvaloniaFact]
    public void ExtraViews_RestoreFromPreferencesOnBoot()
    {
        // The ordinals live in the shared per-process preferences.json, which the MainViewModel
        // constructor reads — so writing them here is what a previous session's shutdown looks like.
        var seed = new UserPreferences();
        seed.SetExtraRadarViewOrdinals([2]);
        seed.SetExtraGroundViewOrdinals([3]);
        try
        {
            var (main, vm) = BootMainWindow();

            Assert.Equal([2], vm.ExtraRadarViews.Select(i => i.Ordinal).ToList());
            Assert.Equal([3], vm.ExtraGroundViews.Select(i => i.Ordinal).ToList());
            Assert.Equal("Radar View #2", main.ExtraRadarWindows.Values.Single().Title);
            Assert.Equal("Ground View #3", main.ExtraGroundWindows.Values.Single().Title);

            CloseAllExtraViews(vm);
        }
        finally
        {
            seed.SetExtraRadarViewOrdinals([]);
            seed.SetExtraGroundViewOrdinals([]);
        }
    }

    [AvaloniaFact]
    public void CaptureCurrent_RecordsExtraViewOrdinals()
    {
        var (_, vm) = BootMainWindow();
        try
        {
            vm.OpenExtraRadarViewCommand.Execute(null);
            vm.OpenExtraGroundViewCommand.Execute(null);
            vm.OpenExtraGroundViewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var profile = new WindowProfileService(vm.Preferences).CaptureCurrent("extra-views", vm);

            Assert.Equal([2], profile.ExtraRadarViewOrdinals);
            Assert.Equal([2, 3], profile.ExtraGroundViewOrdinals);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
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

    // The open extra-view set persists to the shared per-process preferences.json, so every test that
    // opens one must leave the set empty — a later test's MainWindow would otherwise boot with windows.
    private static void CloseAllExtraViews(MainViewModel vm)
    {
        vm.ReconcileExtraViews([], []);
        Dispatcher.UIThread.RunJobs();
    }
}
