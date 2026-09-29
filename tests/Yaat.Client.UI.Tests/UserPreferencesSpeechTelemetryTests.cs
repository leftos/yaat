using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip. Tests share that one file,
// so every mutating test restores the factory defaults in a finally.
public class UserPreferencesSpeechTelemetryTests
{
    [Fact]
    public void SpeechTelemetryEnabled_DefaultsOff_RoundTrips_And_Forces_Capture_On()
    {
        var prefs = new UserPreferences();
        Assert.False(prefs.SpeechTelemetryEnabled);
        Assert.False(prefs.SpeechSampleCaptureEnabled);

        try
        {
            prefs.SetSpeechTelemetryEnabled(true);

            Assert.True(prefs.SpeechTelemetryEnabled);
            Assert.True(prefs.SpeechSampleCaptureEnabled, "enabling telemetry must turn local capture on");

            var reloaded = new UserPreferences();
            Assert.True(reloaded.SpeechTelemetryEnabled);
            Assert.True(reloaded.SpeechSampleCaptureEnabled);
        }
        finally
        {
            prefs.SetSpeechTelemetryEnabled(false);
            prefs.SetSpeechSampleSettings(enabled: false, maxMb: 50);
        }

        Assert.False(new UserPreferences().SpeechTelemetryEnabled);
        Assert.False(new UserPreferences().SpeechSampleCaptureEnabled);
    }

    [Fact]
    public void Capture_Stays_On_When_Settings_Try_To_Disable_It_During_Telemetry()
    {
        var prefs = new UserPreferences();
        try
        {
            prefs.SetSpeechTelemetryEnabled(true);
            prefs.SetSpeechSampleSettings(enabled: false, maxMb: 50);

            Assert.True(prefs.SpeechSampleCaptureEnabled);
            Assert.True(new UserPreferences().SpeechSampleCaptureEnabled);
        }
        finally
        {
            prefs.SetSpeechTelemetryEnabled(false);
            prefs.SetSpeechSampleSettings(enabled: false, maxMb: 50);
        }
    }

    [Fact]
    public void SetSpeechTelemetryPromptShown_RoundTrips()
    {
        var prefs = new UserPreferences();
        bool original = prefs.SpeechTelemetryPromptShown;

        try
        {
            prefs.SetSpeechTelemetryPromptShown(true);

            Assert.True(prefs.SpeechTelemetryPromptShown);
            Assert.True(new UserPreferences().SpeechTelemetryPromptShown);
        }
        finally
        {
            prefs.SetSpeechTelemetryPromptShown(original);
        }

        Assert.Equal(original, new UserPreferences().SpeechTelemetryPromptShown);
    }
}
