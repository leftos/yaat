using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// The Speech Debug window's header describes the engine running now: the mode is the saved one unless Accurate cannot run
/// for the saved model, and the startup GPU fallback gets a line of its own. The mode rule is Settings' own, shared through
/// <see cref="SttModelCardViewModel"/> so the two surfaces can never disagree.
/// </summary>
public class SpeechDebugViewModelEngineLineTests
{
    private const string NbestReason = "Accurate needs the n-best native; running Fast";

    private static readonly (bool Available, string? Reason) NbestAvailable = (true, null);
    private static readonly (bool Available, string? Reason) NbestUnavailable = (false, NbestReason);

    [Theory]
    [InlineData(SttEngine.RasrV2Fp32, true, SttMode.Accurate, SttMode.Accurate)]
    [InlineData(SttEngine.RasrV2Int8, true, SttMode.Accurate, SttMode.Accurate)]
    [InlineData(SttEngine.RasrV2Fp32, false, SttMode.Accurate, SttMode.Fast)]
    [InlineData(SttEngine.RasrV2Int8, false, SttMode.Accurate, SttMode.Fast)]
    [InlineData(SttEngine.Whisper, true, SttMode.Accurate, SttMode.Fast)]
    [InlineData(SttEngine.RasrV2Fp32, true, SttMode.Fast, SttMode.Fast)]
    [InlineData(SttEngine.RasrV2Fp32, false, SttMode.Fast, SttMode.Fast)]
    public void EffectiveMode_IsTheSavedModeUnlessAccurateCannotRun(SttEngine engine, bool nbestAvailable, SttMode saved, SttMode expected)
    {
        (bool Available, string? Reason) nbest = nbestAvailable ? NbestAvailable : NbestUnavailable;

        string? reason = SttModelCardViewModel.AccurateUnavailableReason(engine, nbest);

        Assert.Equal(expected, SttModelCardViewModel.EffectiveMode(saved, reason));
    }

    [Theory]
    [InlineData(SttEngine.RasrV2Fp32, SttMode.Accurate, true, SherpaNativeSet.Cpu, "Running: rasr-parakeet v2 · fp32 · Accurate · CPU")]
    [InlineData(SttEngine.RasrV2Fp32, SttMode.Accurate, false, SherpaNativeSet.Cpu, "Running: rasr-parakeet v2 · fp32 · Fast · CPU")]
    [InlineData(SttEngine.RasrV2Int8, SttMode.Fast, true, SherpaNativeSet.Cuda, "Running: rasr-parakeet v2 · int8 · Fast · GPU")]
    [InlineData(SttEngine.Whisper, SttMode.Accurate, true, SherpaNativeSet.Cuda, "Running: Whisper (LM-Kit) · Fast · CPU")]
    public void RunningEngineTextFor_NamesTheModelTheModeItRunsInAndTheDevice(
        SttEngine engine,
        SttMode savedMode,
        bool nbestAvailable,
        SherpaNativeSet loadedSet,
        string expected
    )
    {
        (bool Available, string? Reason) nbest = nbestAvailable ? NbestAvailable : NbestUnavailable;

        Assert.Equal(expected, SpeechDebugViewModel.RunningEngineTextFor(engine, savedMode, nbest, loadedSet));
    }

    [Fact]
    public void CpuFallbackText_IsNullWithoutAFallback_AndNamesTheReasonWithOne()
    {
        Assert.Null(SpeechDebugViewModel.CpuFallbackTextFor(null));
        Assert.Equal(
            "GPU unavailable: the GPU runtime is not installed",
            SpeechDebugViewModel.CpuFallbackTextFor("the GPU runtime is not installed")
        );
    }
}
