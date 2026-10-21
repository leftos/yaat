using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A window an earlier test leaves behind (the main window is hidden, never closed) keeps its preferences instance and its
/// debounced geometry save; neither may reach a later test's preferences. A commits a setting through Settings on a main
/// window it then hides, and B, run after it, flushes every registered window and still reads the defaults.
/// </summary>
[TestCaseOrderer(typeof(MethodNameOrderer))]
public class PreferencesIsolationTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void A_CommitsShowAllThroughSettings_ThenPutsTheFileBack()
    {
        (MainWindow main, MainViewModel _) = MainWindowHost.Boot();
        try
        {
            MenuItem settingsItem = main.FindControl<MenuItem>("SettingsMenuItem")!;
            settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            SettingsWindow dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());

            dialog.ViewModel.GroundShowAllTaxiRoutes = true;
            dialog.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(new UserPreferences().GroundShowAllTaxiRoutes);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
            new UserPreferences().SetGroundTaxiRouteDisplay(onHover: true, showAll: false);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void B_AWindowAnEarlierTestLeftBehind_CannotWriteThisTestsPreferences()
    {
        WindowGeometryHelper.FlushAllSavedGeometries();

        Assert.False(new UserPreferences().GroundShowAllTaxiRoutes);
    }
}
