using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Scenario › Active Runways…: the menu item is enabled only while a scenario is loaded, and a click opens one window —
/// a second click brings the open window forward rather than opening another.
/// </summary>
public class ActiveRunwaysMenuTests
{
    [AvaloniaFact]
    public void MenuItem_OpensOneWindow_AndASecondClickActivatesIt()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        ActiveRunwaysWindow? opened = null;
        try
        {
            LoadScenario(vm);
            MenuItem item = main.FindControl<MenuItem>("ActiveRunwaysMenuItem")!;
            Assert.True(item.IsEnabled);

            Click(item);
            opened = Assert.Single(OpenWindows.All.OfType<ActiveRunwaysWindow>());

            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<ActiveRunwaysWindow>()));
        }
        finally
        {
            opened?.Close();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void MenuItem_IsDisabledUntilAScenarioIsLoaded()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            MenuItem item = main.FindControl<MenuItem>("ActiveRunwaysMenuItem")!;
            Assert.False(item.IsEnabled);

            LoadScenario(vm);

            Assert.True(item.IsEnabled);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    private static void LoadScenario(MainViewModel vm)
    {
        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-1", "OAK Ground", "OAK", true, 1, [], []));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }
}
