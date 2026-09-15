using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Every dockable panel is toggled the same way — a checkable "Pop Out …" item under View. The Terminal
/// used to be the exception, carrying its own Pop Out / Dock button in the panel header, which also meant
/// its state was the only one stored in docked sense.
/// </summary>
public class ViewMenuPopOutTests
{
    private static (MainWindow Window, MainViewModel Vm) BootMainWindow()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, (MainViewModel)window.DataContext!);
    }

    private static MenuItem ViewMenuItem(MainWindow window, string header)
    {
        var view = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");
        return view.Items.OfType<MenuItem>().Single(m => m.Header is string s && s == header);
    }

    [AvaloniaFact]
    public void ViewMenu_HasAPopOutItemForEveryDockablePanel()
    {
        var (window, _) = BootMainWindow();

        var view = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");
        var popOutHeaders = view
            .Items.OfType<MenuItem>()
            .Select(m => m.Header as string)
            .Where(h => h is not null && h.StartsWith("Pop Out ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            ["Pop Out Aircraft _List", "Pop Out _Ground View", "Pop Out _Radar View", "Pop Out _Controllers", "Pop Out _METAR", "Pop Out T_erminal"],
            popOutHeaders
        );
    }

    [AvaloniaFact]
    public void ViewMenu_NewRadarWindow_AddsInstance()
    {
        var (window, vm) = BootMainWindow();
        var item = ViewMenuItem(window, "New Radar _Window");
        try
        {
            item.Command!.Execute(item.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            var instance = Assert.Single(vm.ExtraRadarViews);
            Assert.Equal(2, instance.Ordinal);
        }
        finally
        {
            // Shared per-process preferences.json: leave no extra windows for the next test's boot.
            vm.ReconcileExtraViews([], []);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ViewMenu_TerminalItem_TracksAndDrivesThePoppedOutState()
    {
        var (window, vm) = BootMainWindow();
        var item = ViewMenuItem(window, "Pop Out T_erminal");

        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(item.IsChecked);

        // Menu → view model.
        item.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTerminalPoppedOut);

        // View model → menu, so closing the popped-out window re-checks the item.
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public void IsFavoritesBarDocked_FalseWhenBarHiddenOrTerminalPoppedOut()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.ShowFavoritesBar = true;
            vm.IsTerminalPoppedOut = false;
            Assert.True(vm.IsFavoritesBarDocked);

            // Popped-out Terminal takes the bar with it, so the main window must not show one too.
            vm.IsTerminalPoppedOut = true;
            Assert.False(vm.IsFavoritesBarDocked);

            vm.ShowFavoritesBar = false;
            Assert.False(vm.IsFavoritesBarDocked);

            vm.IsTerminalPoppedOut = false;
            Assert.False(vm.IsFavoritesBarDocked);
        }
        finally
        {
            // Shared per-process preferences.json: restore the factory defaults the other tests read.
            vm.ShowFavoritesBar = true;
            vm.IsTerminalPoppedOut = false;
        }
    }

    [AvaloniaFact]
    public void TerminalPanel_HasNoDockButtonOfItsOwn()
    {
        var (window, vm) = BootMainWindow();
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(), b => b.Content is string s && (s == "Dock" || s == "Pop Out"));
    }
}
