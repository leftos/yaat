using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The View menu groups its items into Windows, Bars and Layout submenus. Every dockable panel is toggled the
/// same way — a checkable item under View › Windows › Pop out, showing its hotkey. The Terminal
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

    private static MenuItem ViewMenu(MainWindow window) => window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");

    /// <summary>The View menu item with this header, at any depth of its submenus.</summary>
    private static MenuItem ViewMenuItem(MainWindow window, string header) =>
        Flatten(ViewMenu(window)).Single(m => m.Header is string s && s == header);

    private static IEnumerable<MenuItem> Flatten(MenuItem menu)
    {
        foreach (MenuItem child in menu.Items.OfType<MenuItem>())
        {
            yield return child;
            foreach (MenuItem grandchild in Flatten(child))
            {
                yield return grandchild;
            }
        }
    }

    // A submenu's entries as headers, with "-" for a separator, so a test can pin their order.
    private static List<string?> Headers(MenuItem menu) => [.. menu.Items.Select(i => i is Separator ? "-" : (i as MenuItem)?.Header as string)];

    [AvaloniaFact]
    public void ViewMenu_HasWindowsBarsAndLayoutSubmenus()
    {
        (MainWindow? window, MainViewModel _) = BootMainWindow();

        Assert.Equal(["_Windows", "_Bars", "_Layout"], Headers(ViewMenu(window)));
        Assert.Equal(
            [
                "Pop out",
                "_Aircraft list",
                "_Ground view",
                "_Radar view",
                "T_erminal",
                "_Controllers",
                "_METAR",
                "-",
                "New gr_ound window…",
                "New radar _window…",
                "-",
                "_Strips",
                "v_TDLS",
            ],
            Headers(ViewMenuItem(window, "_Windows"))
        );
        Assert.Equal(["Fa_vorites bar", "_Favorites panel…", "_Timeline bar"], Headers(ViewMenuItem(window, "_Bars")));
        // The "Pop out" caption labels the toggles below it and does nothing itself.
        Assert.False(ViewMenuItem(window, "Pop out").IsEnabled);
    }

    [AvaloniaFact]
    public void ViewMenu_HasAPopOutItemForEveryDockablePanel()
    {
        (MainWindow? window, MainViewModel _) = BootMainWindow();

        var popOutHeaders = ViewMenuItem(window, "_Windows")
            .Items.OfType<MenuItem>()
            .Where(m => m.ToggleType == MenuItemToggleType.CheckBox)
            .Select(m => m.Header as string)
            .ToList();

        Assert.Equal(["_Aircraft list", "_Ground view", "_Radar view", "T_erminal", "_Controllers", "_METAR"], popOutHeaders);
    }

    [AvaloniaFact]
    public void ViewMenu_PopOutAndBarItems_ShowTheirCurrentHotkey()
    {
        (MainWindow? window, MainViewModel? vm) = BootMainWindow();
        UserPreferences prefs = vm.Preferences;

        (string Header, string Keybind)[] expected =
        [
            ("_Aircraft list", prefs.PopOutAircraftListKey),
            ("_Ground view", prefs.PopOutGroundViewKey),
            ("_Radar view", prefs.PopOutRadarViewKey),
            ("T_erminal", prefs.PopOutTerminalKey),
            ("_Controllers", prefs.PopOutControllersKey),
            ("_METAR", prefs.PopOutMetarKey),
            ("Fa_vorites bar", prefs.FavoritesBarKey),
        ];

        foreach ((string header, string keybind) in expected)
        {
            Assert.True(KeybindHelper.ParseKeybind(keybind, out Key key, out KeyModifiers modifiers), keybind);
            Assert.Equal(new KeyGesture(key, modifiers), ViewMenuItem(window, header).InputGesture);
            // Display only: the window hotkeys dispatch the key, so a menu HotKey would fire it twice.
            Assert.Null(ViewMenuItem(window, header).HotKey);
        }

        // The timeline bar has no hotkey, so it shows none.
        Assert.Null(ViewMenuItem(window, "_Timeline bar").InputGesture);
    }

    [AvaloniaFact]
    public void ViewMenu_HasNewRadarAndGroundWindowItems()
    {
        (MainWindow? window, MainViewModel _) = BootMainWindow();

        // Both items open a modal airport picker from code-behind rather than binding a command, so the
        // menu-level contract is only that they are there and clickable.
        MenuItem radarItem = ViewMenuItem(window, "New radar _window…");
        MenuItem groundItem = ViewMenuItem(window, "New gr_ound window…");

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

        var headers = Flatten(ViewMenu(window)).Select(m => m.Header as string).ToList();

        // The Layout submenu replaced both entries, and its reset item replaced the top-level one.
        Assert.DoesNotContain("Window _Profiles", headers);
        Assert.DoesNotContain("Copy View _Settings...", headers);
        Assert.DoesNotContain("_Reset Aircraft List Layout", headers);
    }

    [AvaloniaFact]
    public void ViewMenu_TerminalItem_TracksAndDrivesThePoppedOutState()
    {
        (MainWindow? window, MainViewModel? vm) = BootMainWindow();
        MenuItem item = ViewMenuItem(window, "T_erminal");

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
