using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the speech-recognition engine and mode preferences. UserPreferences writes to this test's own preferences file; a
// fresh instance proves the disk round-trip.
public class UserPreferencesSttSettingsTests
{
    [Fact]
    public void FreshInstall_DefaultsToRasrFp32AccurateCpu()
    {
        var prefs = new UserPreferences();

        Assert.Equal(SttEngine.RasrV2Fp32, prefs.SttEngine);
        Assert.Equal(SttMode.Accurate, prefs.SttMode);
        Assert.Equal(SttDevice.Cpu, prefs.SttDevice);
    }

    [Fact]
    public void StoredPrefsWithoutSttKeys_DefaultToRasrFp32AndAccurate()
    {
        File.WriteAllText(UserPreferences.FilePath, """{ "whisperModelSize": "medium" }""");

        var prefs = new UserPreferences();

        Assert.Equal("medium", prefs.WhisperModelSize);
        Assert.Equal(SttEngine.RasrV2Fp32, prefs.SttEngine);
        Assert.Equal(SttMode.Accurate, prefs.SttMode);
    }

    [Fact]
    public void UnrecognisedStoredValues_FallBackToDefaults()
    {
        File.WriteAllText(UserPreferences.FilePath, """{ "sttEngine": "Parakeet", "sttMode": "Turbo" }""");

        var prefs = new UserPreferences();

        Assert.Equal(SttEngine.RasrV2Fp32, prefs.SttEngine);
        Assert.Equal(SttMode.Accurate, prefs.SttMode);
    }

    [Fact]
    public void SetSpeechSettings_PersistsEngineModeAndDeviceAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetSpeechSettings(
            prefs.SpeechEnabled,
            prefs.WhisperModelSize,
            prefs.LlmModelPath,
            prefs.LlmGpuLayers,
            prefs.AutoFocusInputAfterSpeech,
            SttEngine.RasrV2Int8,
            SttMode.Fast,
            SttDevice.Gpu
        );

        var reloaded = new UserPreferences();
        Assert.Equal(SttEngine.RasrV2Int8, reloaded.SttEngine);
        Assert.Equal(SttMode.Fast, reloaded.SttMode);
        Assert.Equal(SttDevice.Gpu, reloaded.SttDevice);
    }

    [Fact]
    public void ManifestModelId_MapsEachEngine()
    {
        Assert.Equal("rasr-parakeet-v2-fp32", SttEngine.RasrV2Fp32.ManifestModelId());
        Assert.Equal("rasr-parakeet-v2-int8", SttEngine.RasrV2Int8.ManifestModelId());
        Assert.Null(SttEngine.Whisper.ManifestModelId());
    }
}
