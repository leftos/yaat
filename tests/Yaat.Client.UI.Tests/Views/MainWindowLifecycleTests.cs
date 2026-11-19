using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.VStrips;
using Yaat.Sim.Testing;

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
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
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
    public void GroundAndScopePopOut_EachCreatesItsOwnWindow()
    {
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
        vm.IsGroundViewPoppedOut = false;
        vm.IsScopePoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        vm.IsGroundViewPoppedOut = true;
        vm.IsScopePoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(main.GroundViewWindow);
        Assert.NotNull(main.ScopeWindow);
        Assert.True(main.GroundViewWindow!.IsVisible);
        Assert.True(main.ScopeWindow!.IsVisible);

        vm.IsGroundViewPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(main.GroundViewWindow);
        Assert.NotNull(main.ScopeWindow); // other window untouched

        // Leave the view docked: the main window outlives the test.
        vm.IsScopePoppedOut = false;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void AllTabsPoppedOut_CollapsesContentGrid()
    {
        (MainWindow _, MainViewModel? vm) = BootMainWindow();
        // Normalise initial state (ignore user's prefs).
        vm.IsDataGridPoppedOut = false;
        vm.IsGroundViewPoppedOut = false;
        vm.IsScopePoppedOut = false;
        vm.StripsEntries[0].IsPoppedOut = false;
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsAnyTabVisible);
        Assert.True(vm.IsContentGridVisible);

        vm.IsDataGridPoppedOut = true;
        vm.IsGroundViewPoppedOut = true;
        vm.IsScopePoppedOut = true;
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

        // Re-dock everything: the main window outlives the test.
        vm.IsDataGridPoppedOut = false;
        vm.IsGroundViewPoppedOut = false;
        vm.IsScopePoppedOut = false;
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
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
        VStripsDockEntryViewModel studentEntry = vm.StripsEntries[0];
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
    public void ScopePopOut_ClosedViaWindowChrome_UndocksWithoutReenteringClose()
    {
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
        vm.IsScopePoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        vm.IsScopePoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        ScopeWindow? popOut = main.ScopeWindow;
        Assert.NotNull(popOut);

        // Close the pop-out window itself — the native title-bar X path (#347). The Closing
        // handler flips IsScopePoppedOut, whose PropertyChanged fan-out re-enters
        // CloseScopeWindow on this same call stack; it must no-op instead of calling
        // Close() again on the window that is already inside its own Closing event.
        popOut!.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsScopePoppedOut);
        Assert.Null(main.ScopeWindow);
    }

    [AvaloniaFact]
    public void StripsEntryPopOut_ClosedViaWindowChrome_UndocksWithoutReenteringClose()
    {
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
        VStripsDockEntryViewModel studentEntry = vm.StripsEntries[0];
        studentEntry.IsPoppedOut = false;
        Dispatcher.UIThread.RunJobs();

        studentEntry.IsPoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        VStripsViewWindow window = main.StripsWindows[studentEntry];

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(studentEntry.IsPoppedOut);
        Assert.Empty(main.StripsWindows);
    }

    [AvaloniaFact]
    public void ExtraGroundWindow_OpensWithInstanceVmAndMainViewModelContext()
    {
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
        MarkNavDataReady(vm);
        try
        {
            vm.OpenExtraGroundView("KOAK");
            Dispatcher.UIThread.RunJobs();

            GroundViewInstance instance = Assert.Single(vm.ExtraGroundViews);
            KeyValuePair<GroundViewInstance, GroundViewWindow> entry = Assert.Single(main.ExtraGroundWindows);
            Assert.Same(instance, entry.Key);

            Assert.IsType<MainViewModel>(entry.Value.DataContext);
            Assert.Same(instance.Vm, entry.Value.GroundVm);
            Assert.NotSame(vm.Ground, entry.Value.GroundVm);
            Assert.Equal("Ground View #2 — OAK", entry.Value.Title);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
    }

    [AvaloniaFact]
    public void ExtraGroundWindow_ClosedViaChrome_RemovesInstanceWithoutReenteringClose()
    {
        (MainWindow? main, MainViewModel? vm) = BootMainWindow();
        try
        {
            vm.OpenExtraGroundView("KOAK");
            Dispatcher.UIThread.RunJobs();
            GroundViewWindow window = main.ExtraGroundWindows.Values.Single();

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
        // The ordinals live in the preferences file, which the MainViewModel constructor reads — so
        // writing them here is what a previous session's shutdown looks like.
        var seed = new UserPreferences();
        seed.SetExtraGroundViews([new SavedExtraView(3, "KSFO")]);
        try
        {
            (MainWindow? main, MainViewModel? vm) = BootMainWindow();

            Assert.Equal([3], vm.ExtraGroundViews.Select(i => i.Ordinal).ToList());
            Assert.Equal("Ground View #3 — KSFO", main.ExtraGroundWindows.Values.Single().Title);

            CloseAllExtraViews(vm);
        }
        finally
        {
            seed.SetExtraGroundViews([]);
        }
    }

    [AvaloniaFact]
    public void CaptureCurrent_RecordsExtraViews()
    {
        (MainWindow _, MainViewModel? vm) = BootMainWindow();
        MarkNavDataReady(vm);
        try
        {
            vm.OpenExtraGroundView("KOAK");
            vm.OpenExtraGroundView("KOAK");
            Dispatcher.UIThread.RunJobs();

            SavedLayout layout = new LayoutService(vm.Preferences).CaptureCurrent("extra-views", vm);

            // Ordinal and base airport in the FAA form ("KOAK" is stored as "OAK"), so applying the
            // layout reopens each window where it was.
            Assert.Equal([new SavedExtraView(2, "OAK"), new SavedExtraView(3, "OAK")], layout.ExtraGroundViews);
        }
        finally
        {
            CloseAllExtraViews(vm);
        }
    }

    // The extra-view open path canonicalises the airport id to its FAA form ("KOAK" -> "OAK") once
    // navdata is loaded, and the view model's own navdata initialization races the test body — pin it
    // ready first so the id a test opens on is deterministically the id that gets stored and shown.
    private static void MarkNavDataReady(MainViewModel vm)
    {
        TestVnasData.EnsureInitialized();
        vm.MarkNavDbReady();
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

    // Closes the extra view windows a test opened, which outlive the test otherwise.
    private static void CloseAllExtraViews(MainViewModel vm)
    {
        vm.ReconcileExtraViews([]);
        Dispatcher.UIThread.RunJobs();
    }
}
