using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The docked terminal's size (its share of the main window's tabs + terminal height, read from
/// <c>ContentGrid</c>'s star rows) is remembered across launches and pop-out toggles, and saved and restored by layouts.
/// Every test restores the shared preferences.json and puts the live view model's ratio and dock state back, because
/// the MainWindow outlives the test and its preferences instance writes its whole state on any later save.
/// </summary>
public class TerminalSplitRatioTests
{
    private static readonly HashSet<string> TerminalRowOnly = [ViewSettingsCopyCatalog.LayoutTerminalKey];

    [AvaloniaFact]
    public void CaptureCurrent_CarriesTheDockedTerminalShare()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow _, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.4;

            SavedLayout layout = new LayoutService(vm.Preferences).CaptureCurrent("terminal-split", vm);

            Assert.Equal(0.4, layout.TerminalSplitRatio);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_DockedWithHalf_SetsTheRowsToHalf_AndRemembersIt()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.25;
            Dispatcher.UIThread.RunJobs();

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "half", TerminalSplitRatio = 0.5 }, TerminalRowOnly);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0.5, TerminalShare(main), 6);
            Assert.Equal(0.5, new UserPreferences().TerminalSplitRatio);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_SavedBeforeTheSizeWasCaptured_LeavesACustomSplitUnchanged()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.4;
            Dispatcher.UIThread.RunJobs();

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "old" }, TerminalRowOnly);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0.4, TerminalShare(main), 6);
            Assert.Equal(0.4, vm.TerminalSplitRatio);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_ThatPopsTheTerminalOut_LeavesTheDockedRatioUntouched()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.3;
            Dispatcher.UIThread.RunJobs();

            await main.ApplyLayoutAsync(
                vm,
                new SavedLayout
                {
                    Name = "popped",
                    IsTerminalPoppedOut = true,
                    TerminalSplitRatio = 0.6,
                },
                TerminalRowOnly
            );
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsTerminalPoppedOut);
            Assert.Equal(0.3, vm.TerminalSplitRatio);
            Assert.Equal(0.3, new UserPreferences().TerminalSplitRatio);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public void CustomSplit_SurvivesATerminalPopOutAndBack()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.4;
            Dispatcher.UIThread.RunJobs();

            vm.IsTerminalPoppedOut = true;
            Dispatcher.UIThread.RunJobs();
            vm.IsTerminalPoppedOut = false;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0.4, TerminalShare(main), 6);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public void CustomSplit_SurvivesEveryTabPoppingOutAndBack()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.4;
            Dispatcher.UIThread.RunJobs();

            SetEveryTabPoppedOut(vm, true);
            Assert.False(vm.IsAnyTabVisible);
            SetEveryTabPoppedOut(vm, false);
            Assert.True(vm.IsAnyTabVisible);

            Assert.Equal(0.4, TerminalShare(main), 6);
        }
        finally
        {
            SetEveryTabPoppedOut(vm, false);
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public void FreshBoot_AppliesTheSavedSplit()
    {
        using var scope = new PreferencesFileScope();
        double original = new UserPreferences().TerminalSplitRatio;
        new UserPreferences().SetTerminalSplitRatio(0.35);

        (MainWindow main, MainViewModel vm, double _) = BootDocked();
        try
        {
            Assert.Equal(0.35, vm.TerminalSplitRatio);
            Assert.Equal(0.35, TerminalShare(main), 6);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public void SplitterDrag_RemembersTheDraggedShare()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.3;
            Dispatcher.UIThread.RunJobs();

            DragToShare(main, tabsStars: 360, terminalStars: 240);

            Assert.Equal(0.4, vm.TerminalSplitRatio, 2);
            Assert.Equal(0.4, new UserPreferences().TerminalSplitRatio, 2);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    [AvaloniaFact]
    public void SplitterDrag_PastTheClamp_SnapsTheRowsBackToTheStoredShare()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm, double original) = BootDocked();
        try
        {
            vm.TerminalSplitRatio = 0.4;
            Dispatcher.UIThread.RunJobs();
            vm.TerminalSplitRatio = 0.15;
            Dispatcher.UIThread.RunJobs();

            DragToShare(main, tabsStars: 380, terminalStars: 20);

            Assert.Equal(0.15, vm.TerminalSplitRatio, 6);
            Assert.Equal(0.15, TerminalShare(main), 6);
        }
        finally
        {
            Restore(vm, original);
        }
    }

    // Lays the content grid's rows out the way a finished splitter drag leaves them, then raises the release.
    private static void DragToShare(MainWindow main, double tabsStars, double terminalStars)
    {
        Grid grid = main.FindControl<Grid>("ContentGrid")!;
        grid.Height = 600;
        grid.RowDefinitions[0].Height = new GridLength(tabsStars, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(terminalStars, GridUnitType.Star);
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(grid.RowDefinitions[0].ActualHeight > 0, "the content grid did not lay out");

        main.FindControl<GridSplitter>("TerminalSplitter")!.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // The terminal row's share of the two star rows; both are star-sized while a tab and the terminal are docked.
    private static double TerminalShare(MainWindow main)
    {
        Grid grid = main.FindControl<Grid>("ContentGrid")!;
        GridLength tabs = grid.RowDefinitions[0].Height;
        GridLength terminal = grid.RowDefinitions[2].Height;
        Assert.True(tabs.IsStar && terminal.IsStar, $"rows are {tabs} / {terminal}, not both star");
        return terminal.Value / (tabs.Value + terminal.Value);
    }

    private static (MainWindow Main, MainViewModel Vm, double OriginalRatio) BootDocked()
    {
        var main = new MainWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        var vm = (MainViewModel)main.DataContext!;
        double original = vm.TerminalSplitRatio;
        vm.IsTerminalPoppedOut = false;
        vm.IsDataGridPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
        return (main, vm, original);
    }

    private static void SetEveryTabPoppedOut(MainViewModel vm, bool poppedOut)
    {
        vm.IsDataGridPoppedOut = poppedOut;
        vm.IsGroundViewPoppedOut = poppedOut;
        vm.IsRadarViewPoppedOut = poppedOut;
        vm.IsControllersPoppedOut = poppedOut;
        vm.IsMetarPoppedOut = poppedOut;
        vm.StripsEntries[0].IsPoppedOut = poppedOut;
        vm.TdlsEntries[0].IsPoppedOut = poppedOut;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Restore(MainViewModel vm, double originalRatio)
    {
        vm.IsTerminalPoppedOut = false;
        vm.TerminalSplitRatio = originalRatio;
        Dispatcher.UIThread.RunJobs();
    }
}
