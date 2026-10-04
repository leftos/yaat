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
/// The Settings window opened from the main window: Apply commits and refreshes the live views while the window
/// stays open, Cancel then puts back the state as of the last Apply (not as of opening), and OK commits and closes.
/// Tools › Settings opens on General and the pilot voice request on Speech; a second request while Settings is open
/// moves the open window to the requested section instead of opening another.
/// </summary>
public class SettingsDialogHostTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_RefreshesTheLiveViews_AndCancelRollsBackOnlyWhatCameAfterIt()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = BootMainWindow();
        try
        {
            SettingsWindow dialog = OpenSettings(main);
            SettingsViewModel settingsVm = dialog.ViewModel;
            bool showAllTaxiRoutes = !vm.Ground.ShowAllTaxiRoutes;
            int appliedFontSize = vm.Preferences.TerminalFontSize + 2;

            settingsVm.GroundShowAllTaxiRoutes = showAllTaxiRoutes;
            settingsVm.TerminalFontSize = appliedFontSize;
            Click(dialog, "ApplyButton");

            Assert.True(dialog.IsVisible, "Apply leaves the window open");
            Assert.Equal(showAllTaxiRoutes, vm.Ground.ShowAllTaxiRoutes);
            Assert.Equal(appliedFontSize, new UserPreferences().TerminalFontSize);

            settingsVm.TerminalFontSize = appliedFontSize + 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(appliedFontSize + 2, vm.TerminalFontSize);

            Click(dialog, "CancelButton");

            Assert.False(dialog.IsVisible);
            Assert.Equal(appliedFontSize, vm.TerminalFontSize);
            Assert.Equal(appliedFontSize, new UserPreferences().TerminalFontSize);
        }
        finally
        {
            CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Ok_CommitsAndCloses()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = BootMainWindow();
        try
        {
            SettingsWindow dialog = OpenSettings(main);
            bool showAllTaxiRoutes = !vm.Ground.ShowAllTaxiRoutes;

            dialog.ViewModel.GroundShowAllTaxiRoutes = showAllTaxiRoutes;
            Click(dialog, "OkButton");

            Assert.False(dialog.IsVisible);
            Assert.Equal(showAllTaxiRoutes, vm.Ground.ShowAllTaxiRoutes);
            Assert.Equal(showAllTaxiRoutes, new UserPreferences().GroundShowAllTaxiRoutes);
        }
        finally
        {
            CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsMenu_OpensOnGeneral()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = BootMainWindow();
        try
        {
            SettingsWindow dialog = OpenSettings(main);

            Assert.IsType<GeneralSection>(ShownSection(dialog));
        }
        finally
        {
            CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void PilotVoiceSettingsRequest_OpensOnSpeech()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = BootMainWindow();
        try
        {
            vm.OpenPilotVoiceSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            SettingsWindow dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.IsType<SpeechSection>(ShownSection(dialog));
        }
        finally
        {
            CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SpeechDebugRequest_WhileSettingsIsOpen_ShowsSpeechInTheOpenWindow()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = BootMainWindow();
        try
        {
            SettingsWindow first = OpenSettings(main);
            Assert.IsType<GeneralSection>(ShownSection(first));

            main.FindControl<MenuItem>("MicMenuDebugItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            SpeechDebugWindow speechDebug = Assert.IsType<SpeechDebugWindow>(main.OpenSpeechDebugWindow);
            speechDebug.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            SettingsWindow shown = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.Same(first, shown);
            Assert.IsType<SpeechSection>(ShownSection(shown));
        }
        finally
        {
            CloseAll(main);
        }
    }

    private static (MainWindow Main, MainViewModel Vm) BootMainWindow()
    {
        var main = new MainWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        return (main, (MainViewModel)main.DataContext!);
    }

    private static SettingsWindow OpenSettings(MainWindow main)
    {
        MenuItem settingsItem = main.FindControl<MenuItem>("SettingsMenuItem")!;
        settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
    }

    private static object? ShownSection(SettingsWindow dialog) => dialog.FindControl<ContentControl>("SectionHost")!.Content;

    private static void Click(SettingsWindow dialog, string buttonName)
    {
        dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // Closes what the test opened and hides the main window. The main window is never closed: a close that goes
    // through latches the process-wide AppLifetime.IsShuttingDown flag, after which every later test's pop-out
    // windows treat their own close as an app shutdown.
    private static void CloseAll(MainWindow main)
    {
        foreach (SettingsWindow dialog in main.OwnedWindows.OfType<SettingsWindow>().ToList())
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }

        main.OpenSpeechDebugWindow?.Close();
        main.Hide();
        Dispatcher.UIThread.RunJobs();
    }
}
