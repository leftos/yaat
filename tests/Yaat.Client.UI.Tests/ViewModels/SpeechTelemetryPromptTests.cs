using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The one-time offer to send push-to-talk recordings to the YAAT developers. Turning speech-to-text on
/// raises it, and a user who already had speech-to-text on when the feature shipped is offered once when
/// the main window opens. The dialog is the view-supplied
/// <see cref="MainViewModel.SpeechTelemetryPrompt"/>: no delegate (a headless host) never prompts, and a
/// dialog already on screen suppresses a second one.
/// </summary>
public class SpeechTelemetryPromptTests
{
    /// <summary>Counts how often the dialog was raised; answers with <see cref="Answer"/> unless the test holds a task open.</summary>
    private sealed class PromptStub(bool answer)
    {
        public int Shown { get; private set; }

        public bool Answer { get; set; } = answer;

        /// <summary>Set by a test that needs the dialog to stay up until it completes this task.</summary>
        public TaskCompletionSource<bool>? Pending { get; set; }

        public Task<bool> Show()
        {
            Shown++;
            return Pending?.Task ?? Task.FromResult(Answer);
        }
    }

    /// <summary>
    /// Puts the shared speech preferences into the state one test starts from and restores what it found.
    /// The model sources are blanked while the guard is alive so the speech-enabled toggle's background
    /// prewarm finds nothing configured instead of fetching a model over the network.
    /// </summary>
    private sealed class SpeechPrefsGuard : IDisposable
    {
        private readonly bool _speechEnabled;
        private readonly bool _telemetryEnabled;
        private readonly bool _promptShown;
        private readonly bool _captureEnabled;
        private readonly string _whisperModelSource;
        private readonly string _llmModelSource;
        private readonly int _llmGpuLayers;
        private readonly bool _autoFocusInputAfterSpeech;

        public SpeechPrefsGuard()
        {
            var prefs = new UserPreferences();
            _speechEnabled = prefs.SpeechEnabled;
            _telemetryEnabled = prefs.SpeechTelemetryEnabled;
            _promptShown = prefs.SpeechTelemetryPromptShown;
            _captureEnabled = prefs.SpeechSampleCaptureEnabled;
            _whisperModelSource = prefs.WhisperModelSize;
            _llmModelSource = prefs.LlmModelPath;
            _llmGpuLayers = prefs.LlmGpuLayers;
            _autoFocusInputAfterSpeech = prefs.AutoFocusInputAfterSpeech;
        }

        public void Prepare(bool speechEnabled, bool promptShown)
        {
            var prefs = new UserPreferences();
            prefs.SetSpeechTelemetryEnabled(false);
            prefs.SetSpeechSampleSettings(enabled: false, maxMb: prefs.SpeechSampleCacheMaxMb);
            prefs.SetSpeechTelemetryPromptShown(promptShown);
            prefs.SetSpeechSettings(speechEnabled, "", "", _llmGpuLayers, _autoFocusInputAfterSpeech);
        }

        public void Dispose()
        {
            var prefs = new UserPreferences();
            prefs.SetSpeechTelemetryEnabled(_telemetryEnabled);
            prefs.SetSpeechSampleSettings(_captureEnabled, prefs.SpeechSampleCacheMaxMb);
            prefs.SetSpeechTelemetryPromptShown(_promptShown);
            prefs.SetSpeechSettings(_speechEnabled, _whisperModelSource, _llmModelSource, _llmGpuLayers, _autoFocusInputAfterSpeech);
        }
    }

    [AvaloniaFact]
    public void FirstEnable_RaisesTheOfferOnce_AndAcceptingOptsIn()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        var prompt = new PromptStub(answer: true);
        vm.SpeechTelemetryPrompt = prompt.Show;

        vm.IsSpeechEnabled = true;

        Assert.Equal(1, prompt.Shown);
        Assert.True(vm.Preferences.SpeechTelemetryEnabled);
        Assert.True(vm.Preferences.SpeechSampleCaptureEnabled, "accepting telemetry must turn local capture on");
        Assert.True(vm.Preferences.SpeechTelemetryPromptShown);
    }

    [AvaloniaFact]
    public void FirstEnable_Declining_LeavesSpeechOn_AndTelemetryOff()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        var prompt = new PromptStub(answer: false);
        vm.SpeechTelemetryPrompt = prompt.Show;

        vm.IsSpeechEnabled = true;

        Assert.Equal(1, prompt.Shown);
        Assert.False(vm.Preferences.SpeechTelemetryEnabled);
        Assert.False(vm.Preferences.SpeechSampleCaptureEnabled);
        Assert.True(vm.Preferences.SpeechTelemetryPromptShown);
        Assert.True(vm.IsSpeechEnabled, "declining the offer must not turn speech-to-text off");
    }

    [AvaloniaFact]
    public void SecondEnable_AfterTheOfferWasAnswered_DoesNotRaiseItAgain()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        var prompt = new PromptStub(answer: true);
        vm.SpeechTelemetryPrompt = prompt.Show;

        vm.IsSpeechEnabled = true;
        Assert.Equal(1, prompt.Shown);

        vm.IsSpeechEnabled = false;
        vm.IsSpeechEnabled = true;

        Assert.Equal(1, prompt.Shown);
    }

    [AvaloniaFact]
    public async Task OfferIfDue_WithSpeechAlreadyOn_RaisesTheOffer()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        var prompt = new PromptStub(answer: true);
        vm.SpeechTelemetryPrompt = prompt.Show;
        // The state the window-opened path runs against: speech already on, offer never shown.
        vm.Preferences.SetSpeechEnabled(true);

        await vm.OfferSpeechTelemetryIfDueAsync();

        Assert.Equal(1, prompt.Shown);
        Assert.True(vm.Preferences.SpeechTelemetryEnabled);
        Assert.True(vm.Preferences.SpeechTelemetryPromptShown);
    }

    [AvaloniaFact]
    public async Task OfferIfDue_WithSpeechOff_DoesNotRaiseTheOffer()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        var prompt = new PromptStub(answer: true);
        vm.SpeechTelemetryPrompt = prompt.Show;

        await vm.OfferSpeechTelemetryIfDueAsync();

        Assert.Equal(0, prompt.Shown);
        Assert.False(vm.Preferences.SpeechTelemetryPromptShown, "an offer never shown is still due");
    }

    [AvaloniaFact]
    public async Task OfferIfDue_WhenTheDialogThrows_LogsAndLeavesTheOfferDue()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        int shown = 0;
        vm.SpeechTelemetryPrompt = () =>
        {
            shown++;
            throw new InvalidOperationException("dialog failed to open");
        };
        vm.Preferences.SetSpeechEnabled(true);

        await vm.OfferSpeechTelemetryIfDueAsync();

        Assert.False(vm.Preferences.SpeechTelemetryEnabled);
        Assert.False(vm.Preferences.SpeechTelemetryPromptShown, "an offer that never got an answer is still due");

        // The on-screen guard reset, so the next trigger raises the dialog again.
        await vm.OfferSpeechTelemetryIfDueAsync();
        Assert.Equal(2, shown);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task OfferIfDue_WhileADialogIsPending_RaisesOnlyOneDialog()
    {
        using var guard = new SpeechPrefsGuard();
        guard.Prepare(speechEnabled: false, promptShown: false);
        var vm = new MainViewModel(new FakeFilePickerService());
        var prompt = new PromptStub(answer: true) { Pending = new TaskCompletionSource<bool>() };
        vm.SpeechTelemetryPrompt = prompt.Show;
        vm.Preferences.SetSpeechEnabled(true);

        Task first = vm.OfferSpeechTelemetryIfDueAsync();
        Task second = vm.OfferSpeechTelemetryIfDueAsync();

        Assert.Equal(1, prompt.Shown);
        Assert.True(second.IsCompleted, "a second trigger must return while the first dialog is still up");

        prompt.Pending.SetResult(true);
        Dispatcher.UIThread.RunJobs();
        await first;
        await second;

        Assert.Equal(1, prompt.Shown);
        Assert.True(vm.Preferences.SpeechTelemetryEnabled);
    }
}
