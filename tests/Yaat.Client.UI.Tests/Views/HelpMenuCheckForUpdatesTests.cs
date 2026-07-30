using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Help → Check for Updates is the only user-invoked entry into the Velopack check; the startup check
/// runs silently. The item greys out while a check is in flight so a second click can't stack another
/// result dialog behind the first.
/// </summary>
public class HelpMenuCheckForUpdatesTests
{
    private const string CheckForUpdatesHeader = "Chec_k for Updates...";

    private static (MainWindow Window, MainViewModel Vm) BootMainWindow()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, (MainViewModel)window.DataContext!);
    }

    private static MenuItem HelpMenuItem(MainWindow window, string header)
    {
        var help = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_Help");
        return help.Items.OfType<MenuItem>().Single(m => m.Header is string s && s == header);
    }

    [AvaloniaFact]
    public void HelpMenu_HasACheckForUpdatesItemAboveAbout()
    {
        var (window, _) = BootMainWindow();

        var help = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_Help");
        var headers = help.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();

        Assert.Contains(CheckForUpdatesHeader, headers);
        Assert.Equal(headers.IndexOf("_About YAAT...") - 1, headers.IndexOf(CheckForUpdatesHeader));
    }

    [AvaloniaFact]
    public void CheckForUpdatesItem_IsDisabledWhileACheckIsInFlight()
    {
        var (window, vm) = BootMainWindow();
        var item = HelpMenuItem(window, CheckForUpdatesHeader);

        Assert.False(vm.IsCheckingForUpdate);
        Assert.True(item.IsEnabled);

        vm.IsCheckingForUpdate = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(item.IsEnabled);

        vm.IsCheckingForUpdate = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(item.IsEnabled);
    }
}
