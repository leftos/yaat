using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the solo pilot voice speech-rate preference (0.75–1.5, stored in 0.05 steps; 1.1 is the
// shipped default). UserPreferences writes to this test's own preferences file; a fresh instance
// proves the disk round-trip.
public class UserPreferencesPilotVoiceSpeechRateTests
{
    private static string PreferencesPath => UserPreferences.FilePath;

    [Fact]
    public void Default_IsOnePointOne() => Assert.Equal(UserPreferences.PilotVoiceSpeechRateDefault, new UserPreferences().PilotVoiceSpeechRate);

    [Fact]
    public void SetPilotVoiceSettings_PersistsRateAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetPilotVoiceSettings(false, 80, true, 1.25);
        Assert.Equal(1.25, prefs.PilotVoiceSpeechRate);
        Assert.Equal(1.25, new UserPreferences().PilotVoiceSpeechRate);
    }

    [Fact]
    public void SetPilotVoiceSettings_ClampsAboveMaximum()
    {
        var prefs = new UserPreferences();

        prefs.SetPilotVoiceSettings(false, 80, true, 3.0);
        Assert.Equal(UserPreferences.PilotVoiceSpeechRateMax, prefs.PilotVoiceSpeechRate);
    }

    [Fact]
    public void SetPilotVoiceSettings_ClampsBelowMinimum()
    {
        var prefs = new UserPreferences();

        prefs.SetPilotVoiceSettings(false, 80, true, 0.1);
        Assert.Equal(UserPreferences.PilotVoiceSpeechRateMin, prefs.PilotVoiceSpeechRate);
    }

    [Fact]
    public void SetPilotVoiceSettings_RoundsToNearestFiveHundredth()
    {
        var prefs = new UserPreferences();

        prefs.SetPilotVoiceSettings(false, 80, true, 1.12);
        Assert.Equal(1.1, prefs.PilotVoiceSpeechRate);
    }

    /// <summary>A preferences.json written before the field existed carries no key; the loader falls back to the default.</summary>
    [Fact]
    public void FileWithoutTheKey_ReadsDefault()
    {
        WithPreferencesFile(
            """{"preferencesVersion": 1, "pilotVoiceVolume": 40}""",
            () => Assert.Equal(UserPreferences.PilotVoiceSpeechRateDefault, new UserPreferences().PilotVoiceSpeechRate)
        );
    }

    /// <summary>A hand-written rate outside the slider's range loads clamped to the maximum.</summary>
    [Fact]
    public void FileWithOutOfRangeRate_ReadsClamped()
    {
        WithPreferencesFile(
            """{"preferencesVersion": 1, "pilotVoiceSpeechRate": 3.0}""",
            () => Assert.Equal(UserPreferences.PilotVoiceSpeechRateMax, new UserPreferences().PilotVoiceSpeechRate)
        );
    }

    /// <summary>Runs <paramref name="body"/> against a preferences.json holding exactly <paramref name="json"/>.</summary>
    private static void WithPreferencesFile(string json, Action body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, json);
        body();
    }
}
