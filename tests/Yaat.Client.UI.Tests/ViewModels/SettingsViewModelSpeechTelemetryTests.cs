using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Settings → Speech switch for automatic speech telemetry. Saving it records the answer the one-time
/// opt-in dialog would otherwise have collected, so the offer never comes back; turning it on takes local
/// sample capture with it, since there is nothing to upload otherwise. The preferences file is shared by
/// every test in the process, so each test restores the speech flags it found.
/// </summary>
public class SettingsViewModelSpeechTelemetryTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void SavingTelemetryOn_MarksTheOfferAnswered_AndTurnsCaptureOn()
    {
        var before = new UserPreferences();
        bool speechEnabled = before.SpeechEnabled;
        bool telemetryEnabled = before.SpeechTelemetryEnabled;
        bool promptShown = before.SpeechTelemetryPromptShown;
        bool captureEnabled = before.SpeechSampleCaptureEnabled;
        int cacheMaxMb = before.SpeechSampleCacheMaxMb;

        try
        {
            var seed = new UserPreferences();
            seed.SetSpeechTelemetryEnabled(false);
            seed.SetSpeechSampleSettings(enabled: false, maxMb: cacheMaxMb);
            seed.SetSpeechTelemetryPromptShown(false);

            var vm = new SettingsViewModel { SpeechTelemetryEnabled = true };

            Assert.True(vm.SpeechSampleCaptureEnabled, "ticking telemetry ticks local capture with it");

            vm.ApplyCommand.Execute(null);

            var saved = new UserPreferences();
            Assert.True(saved.SpeechTelemetryEnabled);
            Assert.True(saved.SpeechTelemetryPromptShown);
            Assert.True(saved.SpeechSampleCaptureEnabled);
        }
        finally
        {
            var restore = new UserPreferences();
            restore.SetSpeechTelemetryEnabled(telemetryEnabled);
            restore.SetSpeechSampleSettings(captureEnabled, cacheMaxMb);
            restore.SetSpeechTelemetryPromptShown(promptShown);
            restore.SetSpeechEnabled(speechEnabled);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SavingWithoutTouchingTelemetry_LeavesTheOfferDue()
    {
        var before = new UserPreferences();
        bool speechEnabled = before.SpeechEnabled;
        bool telemetryEnabled = before.SpeechTelemetryEnabled;
        bool promptShown = before.SpeechTelemetryPromptShown;
        bool captureEnabled = before.SpeechSampleCaptureEnabled;
        int cacheMaxMb = before.SpeechSampleCacheMaxMb;

        try
        {
            var seed = new UserPreferences();
            seed.SetSpeechTelemetryEnabled(false);
            seed.SetSpeechSampleSettings(enabled: false, maxMb: cacheMaxMb);
            seed.SetSpeechTelemetryPromptShown(false);

            var vm = new SettingsViewModel();
            Assert.False(vm.SpeechTelemetryEnabled);

            vm.ApplyCommand.Execute(null);

            var saved = new UserPreferences();
            Assert.False(saved.SpeechTelemetryEnabled);
            Assert.False(saved.SpeechTelemetryPromptShown, "a save that never touched the switch must not answer the offer");
        }
        finally
        {
            var restore = new UserPreferences();
            restore.SetSpeechTelemetryEnabled(telemetryEnabled);
            restore.SetSpeechSampleSettings(captureEnabled, cacheMaxMb);
            restore.SetSpeechTelemetryPromptShown(promptShown);
            restore.SetSpeechEnabled(speechEnabled);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ApplyingTelemetryOnThenOff_InOneWindow_LeavesItOff()
    {
        using var scope = new PreferencesFileScope();
        var seed = new UserPreferences();
        seed.SetSpeechTelemetryEnabled(false);
        seed.SetSpeechTelemetryPromptShown(false);

        var vm = new SettingsViewModel { SpeechTelemetryEnabled = true };
        vm.ApplyCommand.Execute(null);
        Assert.True(new UserPreferences().SpeechTelemetryEnabled);

        vm.SpeechTelemetryEnabled = false;
        vm.ApplyCommand.Execute(null);

        Assert.False(new UserPreferences().SpeechTelemetryEnabled, "the second Apply compares against the first, not against the value at open");
    }
}
