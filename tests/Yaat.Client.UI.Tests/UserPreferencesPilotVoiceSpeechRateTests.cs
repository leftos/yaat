using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// Covers the solo pilot voice speech-rate preference (0.75–1.5, stored in 0.05 steps; 1.1 is the
// shipped default). UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a
// per-process temp dir; a fresh instance proves the disk round-trip. The tests share one
// preferences.json, so each mutating test restores the default in a finally.
public class UserPreferencesPilotVoiceSpeechRateTests
{
    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");

    [Fact]
    public void Default_IsOnePointOne() => Assert.Equal(UserPreferences.PilotVoiceSpeechRateDefault, new UserPreferences().PilotVoiceSpeechRate);

    [Fact]
    public void SetPilotVoiceSettings_PersistsRateAcrossInstances()
    {
        var prefs = new UserPreferences();

        try
        {
            prefs.SetPilotVoiceSettings(false, 80, true, 1.25);
            Assert.Equal(1.25, prefs.PilotVoiceSpeechRate);
            Assert.Equal(1.25, new UserPreferences().PilotVoiceSpeechRate);
        }
        finally
        {
            ResetRate(prefs);
        }

        Assert.Equal(UserPreferences.PilotVoiceSpeechRateDefault, new UserPreferences().PilotVoiceSpeechRate);
    }

    [Fact]
    public void SetPilotVoiceSettings_ClampsAboveMaximum()
    {
        var prefs = new UserPreferences();

        try
        {
            prefs.SetPilotVoiceSettings(false, 80, true, 3.0);
            Assert.Equal(UserPreferences.PilotVoiceSpeechRateMax, prefs.PilotVoiceSpeechRate);
        }
        finally
        {
            ResetRate(prefs);
        }
    }

    [Fact]
    public void SetPilotVoiceSettings_ClampsBelowMinimum()
    {
        var prefs = new UserPreferences();

        try
        {
            prefs.SetPilotVoiceSettings(false, 80, true, 0.1);
            Assert.Equal(UserPreferences.PilotVoiceSpeechRateMin, prefs.PilotVoiceSpeechRate);
        }
        finally
        {
            ResetRate(prefs);
        }
    }

    [Fact]
    public void SetPilotVoiceSettings_RoundsToNearestFiveHundredth()
    {
        var prefs = new UserPreferences();

        try
        {
            prefs.SetPilotVoiceSettings(false, 80, true, 1.12);
            Assert.Equal(1.1, prefs.PilotVoiceSpeechRate);
        }
        finally
        {
            ResetRate(prefs);
        }
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

    private static void ResetRate(UserPreferences prefs)
    {
        prefs.SetPilotVoiceSettings(
            prefs.PilotVoiceEnabled,
            prefs.PilotVoiceVolume,
            prefs.PilotVoiceRadioFxEnabled,
            UserPreferences.PilotVoiceSpeechRateDefault
        );
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a preferences.json holding exactly <paramref name="json"/>, then puts back
    /// whatever was there before — the tests share one per-process preferences.json.
    /// </summary>
    private static void WithPreferencesFile(string json, Action body)
    {
        string? original = File.Exists(PreferencesPath) ? File.ReadAllText(PreferencesPath) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, json);
            body();
        }
        finally
        {
            if (original is null)
            {
                File.Delete(PreferencesPath);
            }
            else
            {
                File.WriteAllText(PreferencesPath, original);
            }
        }
    }
}
