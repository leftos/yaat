using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The live engine swap behind the push-to-talk pipeline, and the factory that builds the engine the preferences name.
/// </summary>
public sealed class SwappableSttEngineTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-swappable-stt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Transcribe_DuringASwap_WaitsForTheSwapThenUsesTheNewEngine()
    {
        var old = new FakeEngine("old");
        var next = new FakeEngine("new") { WarmUpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var swappable = new SwappableSttEngine(old);

        Task swap = swappable.SwapAsync(next, CancellationToken.None);
        await next.WarmUpStarted.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Task<SttHypotheses> transcribe = swappable.TranscribeAsync([0.1f], null, CancellationToken.None);

        Assert.False(transcribe.IsCompleted);
        next.WarmUpGate.SetResult();
        await swap.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        SttHypotheses result = await transcribe.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal("new", result.Text);
        Assert.Equal(0, old.TranscribeCount);
    }

    [Fact]
    public async Task Swap_DisposesTheOldEngineOffTheCallersThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        var next = new FakeEngine("new");
        SwappableSttEngine? swappable = null;
        bool newEngineWasCurrentAtDispose = false;
        var old = new FakeEngine("old") { OnDispose = () => newEngineWasCurrentAtDispose = ReferenceEquals(swappable!.Current, next) };
        swappable = new SwappableSttEngine(old);

        await swappable.SwapAsync(next, CancellationToken.None).WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.True(old.Disposed);
        Assert.NotEqual(callerThread, old.DisposeThreadId);
        Assert.True(newEngineWasCurrentAtDispose);
        swappable.Dispose();
    }

    [Fact]
    public async Task Swap_WarmUpThrows_KeepsTheOldEngineAndDisposesTheNewOne()
    {
        var old = new FakeEngine("old");
        var next = new FakeEngine("new") { WarmUpFailure = new InvalidOperationException("model failed to load") };
        using var swappable = new SwappableSttEngine(old);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            swappable.SwapAsync(next, CancellationToken.None).WaitAsync(HangGuard, TestContext.Current.CancellationToken)
        );
        SttHypotheses result = await swappable
            .TranscribeAsync([0.1f], null, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal("old", result.Text);
        Assert.Same(old, swappable.Current);
        Assert.True(next.Disposed);
        Assert.False(old.Disposed);
    }

    [Fact]
    public void Factory_Whisper_ReturnsTheWhisperAdapter()
    {
        using var whisper = new WhisperSttEngine(new FixedWhisperConfig());

        using ISttEngine engine = SttEngineFactory.Create(SttEngine.Whisper, SttMode.Accurate, _root, () => whisper);

        Assert.IsType<WhisperClientSttEngine>(engine);
    }

    [Fact]
    public void Factory_FastMode_BuildsGreedySherpa()
    {
        using ISttEngine engine = SttEngineFactory.Create(SttEngine.RasrV2Int8, SttMode.Fast, _root, NoWhisper);

        SherpaClientSttEngine sherpa = Assert.IsType<SherpaClientSttEngine>(engine);
        Assert.Equal(SherpaDecoding.Greedy, sherpa.Engine.Decoding);
    }

    [Fact]
    public void Factory_AccurateMode_BuildsBeamFourSherpa()
    {
        using ISttEngine engine = SttEngineFactory.Create(SttEngine.RasrV2Fp32, SttMode.Accurate, _root, NoWhisper);

        SherpaClientSttEngine sherpa = Assert.IsType<SherpaClientSttEngine>(engine);
        Assert.Equal(SherpaDecodingMethod.Beam, sherpa.Engine.Decoding.Method);
        Assert.Equal(4, sherpa.Engine.Decoding.NumPaths);
    }

    [Fact]
    public async Task CreateChecked_AccurateWithANativeProblem_FallsBackToGreedy()
    {
        WriteModelFiles(SttEngine.RasrV2Fp32);
        SherpaSttEngine? probed = null;

        using ISttEngine engine = await SttEngineFactory
            .CreateCheckedAsync(
                SttEngine.RasrV2Fp32,
                SttMode.Accurate,
                _root,
                NoWhisper,
                e =>
                {
                    probed = e;
                    return "this native has no n-best";
                }
            )
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        SherpaClientSttEngine sherpa = Assert.IsType<SherpaClientSttEngine>(engine);
        Assert.NotNull(probed);
        Assert.Equal(SherpaDecodingMethod.Beam, probed.Decoding.Method);
        Assert.True(probed.IsDisposed);
        Assert.Equal(SherpaDecoding.Greedy, sherpa.Engine.Decoding);
    }

    [Fact]
    public async Task CreateChecked_AccurateWithTheModelFilesAbsent_ReturnsTheUnprobedBeamEngine()
    {
        bool probed = false;

        using ISttEngine engine = await SttEngineFactory
            .CreateCheckedAsync(
                SttEngine.RasrV2Fp32,
                SttMode.Accurate,
                _root,
                NoWhisper,
                _ =>
                {
                    probed = true;
                    return null;
                }
            )
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        SherpaClientSttEngine sherpa = Assert.IsType<SherpaClientSttEngine>(engine);
        Assert.False(probed);
        Assert.Equal(SttEngineFactory.AccurateDecoding, sherpa.Engine.Decoding);
    }

    [Fact]
    public async Task CreateChecked_AccurateWithNoNativeProblem_KeepsTheProbedBeamEngine()
    {
        WriteModelFiles(SttEngine.RasrV2Fp32);
        SherpaSttEngine? probed = null;

        using ISttEngine engine = await SttEngineFactory
            .CreateCheckedAsync(
                SttEngine.RasrV2Fp32,
                SttMode.Accurate,
                _root,
                NoWhisper,
                e =>
                {
                    probed = e;
                    return null;
                }
            )
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        SherpaClientSttEngine sherpa = Assert.IsType<SherpaClientSttEngine>(engine);
        Assert.Same(probed, sherpa.Engine);
        Assert.Equal(SherpaDecodingMethod.Beam, sherpa.Engine.Decoding.Method);
    }

    [Fact]
    public void IsModelReady_RasrModelNotInstalled_False()
    {
        Directory.CreateDirectory(_root);

        Assert.False(SttEngineFactory.IsModelReady(SttEngine.RasrV2Int8, _root, ManifestPath));
    }

    [Fact]
    public void IsModelReady_RasrModelInstalled_True()
    {
        string manifestPath = Path.Combine(_root, "stt-downloads.json");
        string installRoot = Path.Combine(_root, "models");
        string modelDir = SttModelInstaller.ModelDir(installRoot, SttEngine.RasrV2Int8.ManifestModelId()!);
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(
            manifestPath,
            """
            {
              "baseUrl": "https://example.invalid/",
              "models": [
                {
                  "id": "rasr-parakeet-v2-int8",
                  "files": [ { "name": "tokens.txt", "key": "stt/tokens.txt", "size": 3, "sha256": "abc123" } ]
                }
              ]
            }
            """
        );
        File.WriteAllText(Path.Combine(modelDir, "tokens.txt"), "a b");
        File.WriteAllText(Path.Combine(modelDir, ".installed"), "tokens.txt abc123");

        Assert.True(SttEngineFactory.IsModelReady(SttEngine.RasrV2Int8, installRoot, manifestPath));
    }

    [Fact]
    public void IsModelReady_Whisper_True() =>
        Assert.True(SttEngineFactory.IsModelReady(SttEngine.Whisper, _root, Path.Combine(_root, "no-manifest.json")));

    [Fact]
    public async Task Apply_TwoRequests_TheLaterOneWinsEvenIfTheEarlierFinishesLast()
    {
        var earlier = new FakeEngine("earlier");
        var later = new FakeEngine("later");
        var earlierBuild = new TaskCompletionSource<ISttEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterBuild = new TaskCompletionSource<ISttEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var swappable = new SwappableSttEngine(new FakeEngine("first"));

        Task<bool> earlierApply = swappable.SwapToLatestAsync(() => earlierBuild.Task, CancellationToken.None);
        Task<bool> laterApply = swappable.SwapToLatestAsync(() => laterBuild.Task, CancellationToken.None);
        laterBuild.SetResult(later);
        bool laterSwapped = await laterApply.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        earlierBuild.SetResult(earlier);
        bool earlierSwapped = await earlierApply.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.True(laterSwapped);
        Assert.False(earlierSwapped);
        Assert.Same(later, swappable.Current);
        Assert.True(earlier.Disposed);
        Assert.False(later.Disposed);
    }

    [Fact]
    public async Task Swap_SherpaEngineOnAnEmptyModelDir_KeepsTheOldEngine()
    {
        string emptyDir = Path.Combine(_root, "empty-model");
        Directory.CreateDirectory(emptyDir);
        var old = new FakeEngine("old");
        var sherpa = new SherpaSttEngine(emptyDir, "cpu", SherpaDecoding.Greedy, SherpaOfflineNativeApi.Instance);
        using var swappable = new SwappableSttEngine(old);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            swappable.SwapAsync(new SherpaClientSttEngine(sherpa), CancellationToken.None).WaitAsync(HangGuard, TestContext.Current.CancellationToken)
        );

        Assert.Same(old, swappable.Current);
        Assert.False(old.Disposed);
        Assert.True(sherpa.IsDisposed);
    }

    [Fact]
    public async Task Dispose_DuringATranscription_ReturnsItsResultAndDisposesTheEngine()
    {
        var engine = new FakeEngine("heard") { TranscribeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var swappable = new SwappableSttEngine(engine);
        Task<SttHypotheses> transcribe = swappable.TranscribeAsync([0.1f], null, CancellationToken.None);
        await engine.TranscribeStarted.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        var dispose = Task.Run(swappable.Dispose, TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);
        engine.TranscribeGate.SetResult();
        SttHypotheses result = await transcribe.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await dispose.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal("heard", result.Text);
        Assert.True(engine.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => swappable.TranscribeAsync([0.1f], null, CancellationToken.None));
    }

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    private static WhisperSttEngine NoWhisper() => throw new InvalidOperationException("A rasr engine must not build Whisper");

    /// <summary>Empty encoder and tokens files: enough for the engine to count as configured, with no native load.</summary>
    private void WriteModelFiles(SttEngine engine)
    {
        string dir = SttModelInstaller.ModelDir(_root, engine.ManifestModelId()!);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "encoder.onnx"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), string.Empty);
    }

    private sealed class FixedWhisperConfig : IWhisperRuntimeConfig
    {
        public string ModelSource => "whisper-base";

        public int? GpuLayerCount => 0;
    }

    private sealed class FakeEngine(string text) : ISttEngine
    {
        public TaskCompletionSource WarmUpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? WarmUpGate { get; init; }

        public Exception? WarmUpFailure { get; init; }

        public Action? OnDispose { get; init; }

        public int TranscribeCount { get; private set; }

        public bool Disposed { get; private set; }

        public int DisposeThreadId { get; private set; }

        public async Task WarmUpAsync(CancellationToken ct)
        {
            WarmUpStarted.TrySetResult();
            if (WarmUpGate is not null)
            {
                await WarmUpGate.Task.ConfigureAwait(false);
            }
            if (WarmUpFailure is not null)
            {
                throw WarmUpFailure;
            }
        }

        public TaskCompletionSource TranscribeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? TranscribeGate { get; init; }

        public async Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct)
        {
            TranscribeCount++;
            TranscribeStarted.TrySetResult();
            if (TranscribeGate is not null)
            {
                await TranscribeGate.Task.ConfigureAwait(false);
            }

            // Like the sherpa engine, a disposed engine hears nothing.
            return SttHypotheses.Single(Disposed ? null : text, null, false);
        }

        public void Dispose()
        {
            DisposeThreadId = Environment.CurrentManagedThreadId;
            OnDispose?.Invoke();
            Disposed = true;
        }
    }
}
