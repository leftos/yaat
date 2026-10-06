using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// The status bar's right-click menus keep their quick toggles and link onward: both mic menus (speech on and off) carry
/// "Speech settings…", which opens Settings at Speech, and the live-traffic menu carries "Live traffic…", shown only while the
/// server offers live traffic and opening the session settings flyout on the command input that is showing.
/// </summary>
public class StatusBarMenuTests
{
    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData("MicStatusText")]
    [InlineData("MicOffText")]
    public void MicMenu_CarriesSpeechSettings_WhichRequestsTheSpeechSection(string indicatorName)
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;

        try
        {
            MenuItem item = Assert.Single(FlyoutItems(main, indicatorName), i => (i.Header as string) == "Speech settings…");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([SettingsSectionId.Speech], requests);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void LiveTrafficMenu_ShowsLiveTrafficLink_OnlyWhileLiveTrafficIsAvailable()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        vm.SessionLiveTrafficEnabled = true;
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(false, false, null, 0, null, null, false);
        Dispatcher.UIThread.RunJobs();
        TextBlock indicator = main.FindControl<TextBlock>("LiveTrafficStatusText")!;
        var flyout = (MenuFlyout)indicator.ContextFlyout!;

        try
        {
            flyout.ShowAt(indicator);
            Dispatcher.UIThread.RunJobs();
            MenuItem item = Assert.Single(flyout.Items.OfType<MenuItem>(), i => (i.Header as string) == "Live traffic…");
            Assert.False(vm.LiveTrafficAvailable);
            Assert.False(item.IsVisible);

            vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, null, 1, null, null, false);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.LiveTrafficAvailable);
            Assert.True(item.IsVisible);
        }
        finally
        {
            flyout.Hide();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void LiveTrafficMenu_Click_RestoresTheButtonsFlyoutModeWhenTheFlyoutCloses()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        LoadScenario(main, vm);
        PopupFlyoutBase flyout = SessionFlyout(main);
        // A mode other than the one the button's flyout loads with, standing in for the temporary Standard an open leaves
        // behind; closing must put back the loaded mode, not this one.
        ArrangeFlyoutMode(flyout, FlyoutShowMode.Transient);

        try
        {
            RaiseLiveTrafficClick(main);
            MainWindowHost.Pump();
            Assert.True(flyout.IsOpen, "the click opened the flyout");

            flyout.Hide();
            MainWindowHost.Pump();

            Assert.Equal(FlyoutShowMode.TransientWithDismissOnPointerMoveAway, flyout.ShowMode);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void LiveTrafficMenu_ClickedTwiceWhileOpen_StillRestoresTheButtonsFlyoutModeOnClose()
    {
        using var scope = new PreferencesFileScope();
        AssertDoubleClickRestoresDockedFlyoutMode();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void LiveTrafficMenu_ClickedTwice_WithTheTerminalLeftPoppedOutInPreferences_StillRestoresTheDockedFlyoutsMode()
    {
        using var scope = new PreferencesFileScope();
        new UserPreferences().SetPoppedOut("Terminal", true);
        AssertDoubleClickRestoresDockedFlyoutMode();
    }

    private static void AssertDoubleClickRestoresDockedFlyoutMode()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        LoadScenario(main, vm);
        PopupFlyoutBase flyout = SessionFlyout(main);
        ArrangeFlyoutMode(flyout, FlyoutShowMode.Transient);

        try
        {
            // Two clicks with no pump between them: the second finds the flyout open and must not re-arm the close handler
            // with the temporary Standard mode as the mode to restore.
            RaiseLiveTrafficClick(main);
            RaiseLiveTrafficClick(main);

            MainWindowHost.Pump();
            Assert.True(flyout.IsOpen, "the click opened the flyout");

            flyout.Hide();
            MainWindowHost.Pump();

            Assert.Equal(FlyoutShowMode.TransientWithDismissOnPointerMoveAway, flyout.ShowMode);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void LiveTrafficMenu_WithTheTerminalPoppedOut_OpensTheFlyoutOnTheTerminalCommandInput()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        LoadScenario(main, vm);
        vm.IsTerminalPoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        main.TerminalWindow!.UpdateLayout();
        MainWindowHost.Pump();

        PopupFlyoutBase terminalFlyout = SessionFlyout(main.TerminalWindow!);
        PopupFlyoutBase dockedFlyout = SessionFlyout(main);
        // A mode other than the loaded one, standing in for the temporary Standard an open leaves behind: the flyout the
        // click opens ends on the button's loaded mode, while the one it does not touch keeps the mode it was arranged with.
        ArrangeFlyoutMode(terminalFlyout, FlyoutShowMode.Transient);
        ArrangeFlyoutMode(dockedFlyout, FlyoutShowMode.Transient);

        try
        {
            RaiseLiveTrafficClick(main);
            MainWindowHost.Pump();
            Assert.True(terminalFlyout.IsOpen, "the click opened the terminal's flyout");

            terminalFlyout.Hide();
            dockedFlyout.Hide();
            MainWindowHost.Pump();

            Assert.Equal(FlyoutShowMode.TransientWithDismissOnPointerMoveAway, terminalFlyout.ShowMode);
            Assert.Equal(FlyoutShowMode.Transient, dockedFlyout.ShowMode);
        }
        finally
        {
            vm.IsTerminalPoppedOut = false;
            Dispatcher.UIThread.RunJobs();
            MainWindowHost.CloseAll(main);
        }
    }

    private static void LoadScenario(MainWindow main, MainViewModel vm)
    {
        vm.ActiveScenarioName = "Test Scenario";
        // The click opens the flyout on whichever command input is showing, and the window boots with the pop-out state the
        // shared preferences.json holds, so dock the terminal here. The pop-out test sets it back to true afterwards.
        vm.IsTerminalPoppedOut = false;
        // The menu item ("Live traffic…") and the flyout's live-traffic row only exist while the server offers live traffic.
        vm.SessionLiveTrafficEnabled = true;
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, null, 1, null, null, false);
        Dispatcher.UIThread.RunJobs();
        main.Activate();
        main.UpdateLayout();
        MainWindowHost.Pump();
        Assert.True(vm.LiveTrafficAvailable);
    }

    // Puts the flyout into the mode an open leaves behind, from a known closed state: a flyout another test left open would
    // send the click down the already-open path instead.
    private static void ArrangeFlyoutMode(PopupFlyoutBase flyout, FlyoutShowMode mode)
    {
        if (flyout.IsOpen)
        {
            flyout.Hide();
            MainWindowHost.Pump();
        }

        flyout.ShowMode = mode;
    }

    // Raises the menu item's click synchronously, with no dispatcher pump, so a caller can check the state the handler
    // left behind before any queued job can dismiss the flyout.
    private static void RaiseLiveTrafficClick(MainWindow main) => LiveTrafficItem(main).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static PopupFlyoutBase SessionFlyout(Window window) =>
        (PopupFlyoutBase)window.FindControl<CommandInputView>("CommandInputView")!.FindControl<Button>("SessionSettingsButton")!.Flyout!;

    private static MenuItem LiveTrafficItem(MainWindow main) =>
        Assert.Single(FlyoutItems(main, "LiveTrafficStatusText"), i => (i.Header as string) == "Live traffic…");

    private static IEnumerable<MenuItem> FlyoutItems(MainWindow main, string indicatorName) =>
        ((MenuFlyout)main.FindControl<TextBlock>(indicatorName)!.ContextFlyout!).Items.OfType<MenuItem>();
}
