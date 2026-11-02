using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
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
    public void LiveTrafficMenu_ClickedTwiceWhileOpen_StillRestoresTheButtonsFlyoutModeOnClose() => AssertDoubleClickRestoresDockedFlyoutMode();

    [AvaloniaFact(Timeout = 60_000)]
    public void LiveTrafficMenu_ClickedTwice_WithTheTerminalLeftPoppedOutInPreferences_StillRestoresTheDockedFlyoutsMode()
    {
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

    [AvaloniaFact(Timeout = 60_000)]
    public async Task SpeechModelMissingLink_ShowsOnlyWhileTheModelIsMissing()
    {
        using var guard = new SpeechPrefsGuard();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();

        try
        {
            await EnableSpeechAsync(vm);
            Button link = main.FindControl<Button>("SpeechModelMissingLink")!;

            Assert.False(link.IsVisible, "hidden at Idle");

            vm.SpeechStatus = SpeechStatus.ModelMissing;
            Dispatcher.UIThread.RunJobs();
            Assert.True(link.IsVisible, "shown after a push-to-talk that was refused for the missing model");

            vm.IsSpeechEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(link.IsVisible, "hidden while speech recognition is off");

            await EnableSpeechAsync(vm);
            vm.SpeechStatus = SpeechStatus.ModelMissing;
            Dispatcher.UIThread.RunJobs();
            Assert.True(link.IsVisible, "shown again once speech recognition is back on");

            vm.SpeechStatus = SpeechStatus.Warming;
            Dispatcher.UIThread.RunJobs();
            Assert.False(link.IsVisible, "hidden once the download lets the model load");

            vm.SpeechStatus = SpeechStatus.Idle;
            Dispatcher.UIThread.RunJobs();
            Assert.False(link.IsVisible, "hidden again at rest");
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task SpeechModelMissingLink_RequestsTheSpeechSection()
    {
        using var guard = new SpeechPrefsGuard();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;

        try
        {
            await EnableSpeechAsync(vm);
            vm.SpeechStatus = SpeechStatus.ModelMissing;
            Dispatcher.UIThread.RunJobs();
            Button link = main.FindControl<Button>("SpeechModelMissingLink")!;
            Assert.True(link.IsVisible, "the link is shown while the model is missing");

            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([SettingsSectionId.Speech], requests);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    /// <summary>
    /// Turns speech recognition on and waits for the background prewarm the toggle starts to finish, rather than for a
    /// duration: the warm-up drives the status through Warming and back, and a status a test writes while it is still
    /// running is overwritten by its last post.
    /// </summary>
    private static async Task EnableSpeechAsync(MainViewModel vm)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int transitions = 0;

        void OnStatusChanged(SpeechStatus status)
        {
            if (Interlocked.Increment(ref transitions) >= 2)
            {
                settled.TrySetResult();
            }
        }

        vm.SpeechService.StatusChanged += OnStatusChanged;
        try
        {
            vm.IsSpeechEnabled = true;
            // Warming first, then the status the warm-up ends on; the guard only keeps a broken pipeline from hanging.
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            vm.SpeechService.StatusChanged -= OnStatusChanged;
        }

        MainWindowHost.Pump();
    }

    /// <summary>
    /// Puts the speech preferences into the state one test starts from, and restores them afterwards. Turning speech on
    /// kicks a background prewarm, and a configured LLM model source makes that warm-up load a model (over the network)
    /// before it ends, so the sources are blanked while the guard is alive.
    /// </summary>
    private sealed class SpeechPrefsGuard : IDisposable
    {
        private readonly bool _speechEnabled;
        private readonly string _whisperModelSize;
        private readonly string _llmModelPath;
        private readonly int _llmGpuLayers;
        private readonly bool _autoFocusInputAfterSpeech;
        private readonly SttEngine _engine;
        private readonly SttMode _mode;
        private readonly SttDevice _device;

        public SpeechPrefsGuard()
        {
            var prefs = new UserPreferences();
            _speechEnabled = prefs.SpeechEnabled;
            _whisperModelSize = prefs.WhisperModelSize;
            _llmModelPath = prefs.LlmModelPath;
            _llmGpuLayers = prefs.LlmGpuLayers;
            _autoFocusInputAfterSpeech = prefs.AutoFocusInputAfterSpeech;
            _engine = prefs.SttEngine;
            _mode = prefs.SttMode;
            _device = prefs.SttDevice;
            prefs.SetSpeechSettings(false, "", "", _llmGpuLayers, _autoFocusInputAfterSpeech, _engine, _mode, _device);
        }

        public void Dispose() =>
            new UserPreferences().SetSpeechSettings(
                _speechEnabled,
                _whisperModelSize,
                _llmModelPath,
                _llmGpuLayers,
                _autoFocusInputAfterSpeech,
                _engine,
                _mode,
                _device
            );
    }

    private static void LoadScenario(MainWindow main, MainViewModel vm)
    {
        vm.ActiveScenarioName = "Test Scenario";
        // The click opens the flyout on whichever command input is showing, so dock the terminal here.
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
