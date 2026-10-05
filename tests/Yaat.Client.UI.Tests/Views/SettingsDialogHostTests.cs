using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;
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
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
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
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Ok_CommitsAndCloses()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
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
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsMenu_OpensOnGeneral()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = MainWindowHost.Boot();
        try
        {
            SettingsWindow dialog = OpenSettings(main);

            Assert.IsType<GeneralSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsSettingsItem_RaisesSettingsRequestedWithGeneral()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;

        try
        {
            main.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([SettingsSectionId.General], requests);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void PilotVoiceSettingsRequest_OpensOnSpeech()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.OpenPilotVoiceSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            SettingsWindow dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.IsType<SpeechSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SpeechDebugRequest_WhileSettingsIsOpen_ShowsSpeechInTheOpenWindow()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = MainWindowHost.Boot();
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
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ViewMenuLink_WhileSettingsIsOpenAtSpeech_MovesToThatViewsSection()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            SettingsWindow dialog = OpenSettings(main);
            vm.RequestSettings(SettingsSectionId.Speech);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<SpeechSection>(ShownSection(dialog));

            var view = new RadarView { DataContext = vm.Radar };
            var host = new Grid { DataContext = vm };
            host.Children.Add(view);
            ContextMenu? menu = view.BuildMapContextMenu(37.620, -122.380, new Point(10, 10));
            Assert.NotNull(menu);
            MenuItem item = Assert.Single(menu.Items.OfType<MenuItem>(), i => (i.Header as string) == "Settings for this view…");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.IsType<RadarSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
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
}
