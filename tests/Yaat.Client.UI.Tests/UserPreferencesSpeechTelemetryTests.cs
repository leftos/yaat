using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesSpeechTelemetryTests
{
    [Fact]
    public void SpeechTelemetryEnabled_DefaultsOff_RoundTrips_And_Forces_Capture_On()
    {
        var prefs = new UserPreferences();
        Assert.False(prefs.SpeechTelemetryEnabled);
        Assert.False(prefs.SpeechSampleCaptureEnabled);

        prefs.SetSpeechTelemetryEnabled(true);

        Assert.True(prefs.SpeechTelemetryEnabled);
        Assert.True(prefs.SpeechSampleCaptureEnabled, "enabling telemetry must turn local capture on");

        var reloaded = new UserPreferences();
        Assert.True(reloaded.SpeechTelemetryEnabled);
        Assert.True(reloaded.SpeechSampleCaptureEnabled);
    }

    [Fact]
    public void Capture_Stays_On_When_Settings_Try_To_Disable_It_During_Telemetry()
    {
        var prefs = new UserPreferences();
        prefs.SetSpeechTelemetryEnabled(true);
        prefs.SetSpeechSampleSettings(enabled: false, maxMb: 50);

        Assert.True(prefs.SpeechSampleCaptureEnabled);
        Assert.True(new UserPreferences().SpeechSampleCaptureEnabled);
    }

    [Fact]
    public void SetSpeechTelemetryPromptShown_RoundTrips()
    {
        var prefs = new UserPreferences();

        prefs.SetSpeechTelemetryPromptShown(true);

        Assert.True(prefs.SpeechTelemetryPromptShown);
        Assert.True(new UserPreferences().SpeechTelemetryPromptShown);
    }
}
