using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Client.ViewModels;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The Speech Debug window's header describes the engine running now: the mode is the saved one unless Accurate cannot run
/// for the saved model, and the startup GPU fallback gets a line of its own. The mode rule is Settings' own, shared through
/// <see cref="SttModelCardViewModel"/> so the two surfaces can never disagree.
/// </summary>
[Collection(CpuFallbackReasonCollection.Name)]
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

    [AvaloniaFact(Timeout = 60_000)]
    public void CpuFallbackText_FollowsTheSharedReason_UntilTheWindowIsDisposed()
    {
        string? savedReason = SherpaNativeLoader.CpuFallbackReason;
        string root = Directory.CreateTempSubdirectory("speech-debug-fallback-").FullName;
        try
        {
            SherpaNativeLoader.ResetCpuFallbackForTests(null);
            var preferences = new UserPreferences();
            using var engine = new SilentEngine();
            using var service = new SpeechRecognitionService(
                preferences,
                new SilentAudio(),
                engine,
                null,
                new PhraseologyCommandMapper(),
                null,
                null,
                () => new SpeechContext([], [], string.Empty),
                null,
                () => true
            );
            var vm = new SpeechDebugViewModel(service, new SpeechSampleStore(preferences, root, a => a()), preferences);
            try
            {
                Assert.Null(vm.CpuFallbackText);

                SherpaNativeLoader.RecordCpuFallback(SherpaSttEngine.NoGpuMemoryRiseReason);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal($"GPU unavailable: {SherpaSttEngine.NoGpuMemoryRiseReason}", vm.CpuFallbackText);

                SherpaNativeLoader.RecordGpuLoad();
                Dispatcher.UIThread.RunJobs();
                Assert.Null(vm.CpuFallbackText);

                vm.Dispose();
                SherpaNativeLoader.RecordCpuFallback("after the window closed");
                Dispatcher.UIThread.RunJobs();
                Assert.Null(vm.CpuFallbackText);
            }
            finally
            {
                // A failed assert above still unsubscribes the view model from the process-wide event.
                vm.Dispose();
            }
        }
        finally
        {
            SherpaNativeLoader.ResetCpuFallbackForTests(savedReason);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A microphone that always opens and records nothing.</summary>
    private sealed class SilentAudio : IAudioCapture
    {
        public bool StartCapture() => true;

        public float[] StopCapture() => [];
    }

    /// <summary>An engine that hears nothing.</summary>
    private sealed class SilentEngine : ISttEngine
    {
        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single(null, null, false));

        public void Dispose() { }
    }
}
