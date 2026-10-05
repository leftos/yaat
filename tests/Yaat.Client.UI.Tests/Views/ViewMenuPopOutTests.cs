using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
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
        MenuItem view = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");
        return view.Items.OfType<MenuItem>().Single(m => m.Header is string s && s == header);
    }

    [AvaloniaFact]
    public void ViewMenu_HasAPopOutItemForEveryDockablePanel()
    {
        (MainWindow? window, MainViewModel _) = BootMainWindow();

        MenuItem view = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");
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
    public void ViewMenu_HasNewRadarAndGroundWindowItems()
    {
        (MainWindow? window, MainViewModel _) = BootMainWindow();

        // Both items open a modal airport picker from code-behind rather than binding a command, so the
        // menu-level contract is only that they are there and clickable.
        MenuItem radarItem = ViewMenuItem(window, "New Radar _Window");
        MenuItem groundItem = ViewMenuItem(window, "New Gr_ound Window");

        Assert.True(radarItem.IsEnabled);
        Assert.True(groundItem.IsEnabled);
    }

    [AvaloniaFact]
    public void ViewMenu_LayoutSubmenu_ListsSavedLayoutsThenTheFixedItems()
    {
        (MainWindow? window, MainViewModel? vm) = BootMainWindow();
        try
        {
            vm.Preferences.SaveLayout(new SavedLayout { Name = "VMT-Layout-B" });
            vm.Preferences.SaveLayout(new SavedLayout { Name = "VMT-Layout-A" });
            Dispatcher.UIThread.RunJobs();

            MenuItem layout = ViewMenuItem(window, "_Layout");
            List<object?> items = [.. layout.Items];
            int separator = items.FindIndex(i => i is Separator);

            // One item per saved layout (they re-populate when the list changes), in the saved order...
            Assert.Equal(vm.Preferences.Layouts.Select(l => l.Name), items.Take(separator).Select(i => ((MenuItem)i!).Header as string));
            Assert.Contains(items.Take(separator), i => i is MenuItem { Header: "VMT-Layout-A" });
            Assert.Contains(items.Take(separator), i => i is MenuItem { Header: "VMT-Layout-B" });
            // ...then the fixed items below the separator.
            Assert.Equal(
                ["Save current as layout…", "From this scenario's views…", "Manage layouts…", "Reset aircraft list columns"],
                items.Skip(separator + 1).Select(i => ((MenuItem)i!).Header as string)
            );
        }
        finally
        {
            vm.Preferences.DeleteLayout("VMT-Layout-A");
            vm.Preferences.DeleteLayout("VMT-Layout-B");
        }
    }

    [AvaloniaFact]
    public void ViewMenu_LayoutSubmenu_ReplacesTheOldEntries()
    {
        (MainWindow? window, MainViewModel _) = BootMainWindow();

        MenuItem view = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");
        var headers = view.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();

        // The Layout submenu replaced both entries, and its reset item replaced the top-level one.
        Assert.DoesNotContain("Window _Profiles", headers);
        Assert.DoesNotContain("Copy View _Settings...", headers);
        Assert.DoesNotContain("_Reset Aircraft List Layout", headers);
    }

    [AvaloniaFact]
    public void ViewMenu_TerminalItem_TracksAndDrivesThePoppedOutState()
    {
        (MainWindow? window, MainViewModel? vm) = BootMainWindow();
        MenuItem item = ViewMenuItem(window, "Pop Out T_erminal");

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
        (MainWindow? window, MainViewModel? vm) = BootMainWindow();
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(), b => b.Content is string s && (s == "Dock" || s == "Pop Out"));
    }
}
