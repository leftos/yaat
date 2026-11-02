using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Engine swaps never interrupt a push-to-talk: one requested mid-transmission waits for the next resting status (Idle,
/// ModelMissing or Error).
/// </summary>
public sealed class SttEngineSwapSchedulerTests
{
    /// <summary>A backstop for a hung swap; a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task RequestedWhileIdle_SwapsAtOnceUnderWarming()
    {
        using SpeechRecognitionService service = NewService();
        List<SpeechStatus> statuses = [];
        service.StatusChanged += statuses.Add;
        int swaps = 0;
        var scheduler = new SttEngineSwapScheduler(service, () => Task.FromResult(Interlocked.Increment(ref swaps)));

        await scheduler.RequestAsync().WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, swaps);
        Assert.Equal([SpeechStatus.Warming, SpeechStatus.Idle], statuses);
    }

    [Fact]
    public async Task RequestedWhileRecording_LeavesTheStatus_AndSwapsOnceTheMicIsIdle()
    {
        using SpeechRecognitionService service = NewService();
        int swaps = 0;
        var scheduler = new SttEngineSwapScheduler(service, () => Task.FromResult(Interlocked.Increment(ref swaps)));
        Assert.True(service.StartPtt());

        Task first = scheduler.RequestAsync();
        Task second = scheduler.RequestAsync();

        Assert.Equal(SpeechStatus.Recording, service.Status);
        Assert.Equal(0, Volatile.Read(ref swaps));
        Assert.Same(first, second);

        service.StopPtt();
        await first.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref swaps));
    }

    [Fact]
    public void PressThatBeatsTheDeferredSwap_KeepsRecording()
    {
        using SpeechRecognitionService service = NewService();
        bool pressed = false;
        service.StatusChanged += status =>
        {
            if ((status == SpeechStatus.Idle) && !pressed)
            {
                pressed = true;
                Assert.True(service.StartPtt());
            }
        };
        int swaps = 0;
        var scheduler = new SttEngineSwapScheduler(service, () => Task.FromResult(Interlocked.Increment(ref swaps)));
        Assert.True(service.StartPtt());
        Task request = scheduler.RequestAsync();

        service.StopPtt();

        Assert.Equal(SpeechStatus.Recording, service.Status);
        Assert.Equal(0, Volatile.Read(ref swaps));
        Assert.False(request.IsCompleted);
    }

    [Fact]
    public async Task SwapQueuedWhileRecording_RunsWhenThePipelineEndsInError()
    {
        using SpeechRecognitionService service = NewService(SpeechOn(), new RecordingAudio(), new FailingEngine());
        var reachedError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += status =>
        {
            if (status == SpeechStatus.Error)
            {
                reachedError.TrySetResult();
            }
        };
        int swaps = 0;
        var scheduler = new SttEngineSwapScheduler(service, () => Task.FromResult(Interlocked.Increment(ref swaps)));
        Assert.True(service.StartPtt());
        Task request = scheduler.RequestAsync();

        service.StopPtt();
        await reachedError.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await request.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref swaps));
    }

    [Fact]
    public async Task SwapQueuedWhileRecording_WithSpeechSwitchedOff_StillSwaps()
    {
        UserPreferences preferences = SpeechOn();
        using SpeechRecognitionService service = NewService(preferences, new StartingAudio(), new SilentEngine());
        int swaps = 0;
        var scheduler = new SttEngineSwapScheduler(service, () => Task.FromResult(Interlocked.Increment(ref swaps)));
        Assert.True(service.StartPtt());
        Task request = scheduler.RequestAsync();

        preferences.SetSpeechEnabled(false);
        service.StopPtt();
        await request.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref swaps));
    }

    [Fact]
    public async Task AWarmOnlyScheduler_WaitsForTheMicToRestAndWarms()
    {
        var engine = new WarmCountingEngine();
        using SpeechRecognitionService service = NewService(SpeechOn(), new StartingAudio(), engine);
        var scheduler = new SttEngineSwapScheduler(service, () => Task.CompletedTask);
        Assert.True(service.StartPtt());

        Task request = scheduler.RequestAsync();

        Assert.Equal(SpeechStatus.Recording, service.Status);
        Assert.Equal(0, Volatile.Read(ref engine.Warms));

        service.StopPtt();
        await request.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref engine.Warms));
    }

    [Fact]
    public async Task SwapQueuedWhileRecording_WithSpeechSwitchedOff_LoadsNoModel()
    {
        UserPreferences preferences = SpeechOn();
        using SpeechRecognitionService service = NewService(preferences, new StartingAudio(), new SilentEngine());
        int loadingSwaps = 0;
        int unloadedSwaps = 0;
        var scheduler = new SttEngineSwapScheduler(
            service,
            SttEngineSwapScheduler.ChooseBySpeechSetting(
                () => preferences.SpeechEnabled,
                () => Task.FromResult(Interlocked.Increment(ref loadingSwaps)),
                () => Task.FromResult(Interlocked.Increment(ref unloadedSwaps))
            )
        );
        Assert.True(service.StartPtt());
        Task request = scheduler.RequestAsync();

        preferences.SetSpeechEnabled(false);
        service.StopPtt();
        await request.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref loadingSwaps));
        Assert.Equal(1, Volatile.Read(ref unloadedSwaps));
    }

    [Fact]
    public async Task RequestedWithSpeechOn_RunsTheLoadingSwap()
    {
        UserPreferences preferences = SpeechOn();
        using SpeechRecognitionService service = NewService(preferences, new StartingAudio(), new SilentEngine());
        int loadingSwaps = 0;
        int unloadedSwaps = 0;
        var scheduler = new SttEngineSwapScheduler(
            service,
            SttEngineSwapScheduler.ChooseBySpeechSetting(
                () => preferences.SpeechEnabled,
                () => Task.FromResult(Interlocked.Increment(ref loadingSwaps)),
                () => Task.FromResult(Interlocked.Increment(ref unloadedSwaps))
            )
        );

        await scheduler.RequestAsync().WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref loadingSwaps));
        Assert.Equal(0, Volatile.Read(ref unloadedSwaps));
    }

    /// <summary>
    /// The engine blocks until the test releases it, which it can only do once the request has returned, so a request that runs
    /// the warm on the caller's thread holds its caller for the load. The pipeline logs a warm-up failure, so the engine records
    /// the hang rather than relying on its throw reaching the test.
    /// </summary>
    [Fact]
    public async Task ARequest_WithTheMicResting_ReturnsBeforeTheWarmLoads()
    {
        using var engine = new BlockingWarmEngine();
        using SpeechRecognitionService service = NewService(SpeechOn(), new StartingAudio(), engine);
        var scheduler = new SttEngineSwapScheduler(service, () => Task.CompletedTask);

        Task request = scheduler.RequestAsync();
        engine.Release();
        await request.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.False(engine.Hung);
    }

    private static UserPreferences SpeechOn()
    {
        var preferences = new UserPreferences();
        preferences.SetSpeechEnabled(true);
        return preferences;
    }

    private static SpeechRecognitionService NewService() => NewService(SpeechOn(), new StartingAudio(), new SilentEngine());

    private static SpeechRecognitionService NewService(UserPreferences preferences, IAudioCapture audio, ISttEngine engine)
    {
        return new SpeechRecognitionService(
            preferences,
            audio,
            engine,
            null,
            new PhraseologyCommandMapper(),
            null,
            null,
            () => new SpeechContext([], [], string.Empty),
            null,
            () => true
        );
    }

    /// <summary>A microphone that always opens and records nothing.</summary>
    private sealed class StartingAudio : IAudioCapture
    {
        public bool StartCapture() => true;

        public float[] StopCapture() => [];
    }

    /// <summary>A microphone that always opens and records a second of silence.</summary>
    private sealed class RecordingAudio : IAudioCapture
    {
        public bool StartCapture() => true;

        public float[] StopCapture() => new float[AudioCaptureService.SampleRate];
    }

    /// <summary>An engine whose every transcription fails, which ends the pipeline in Error.</summary>
    private sealed class FailingEngine : ISttEngine
    {
        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromException<SttHypotheses>(new InvalidOperationException("transcription failed"));

        public void Dispose() { }
    }

    /// <summary>An engine whose warm-up blocks its thread synchronously, as a native model load does, until it is released.</summary>
    private sealed class BlockingWarmEngine : ISttEngine
    {
        private readonly ManualResetEventSlim _released = new();

        public bool Hung { get; private set; }

        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public void Release() => _released.Set();

        public Task WarmUpAsync(CancellationToken ct)
        {
            if (!_released.Wait(HangGuard, ct))
            {
                Hung = true;
                throw new TimeoutException("hung: the warm-up was never released");
            }
            return Task.CompletedTask;
        }

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single(null, null, false));

        public void Dispose() => _released.Dispose();
    }

    /// <summary>An engine that counts its warm-ups.</summary>
    private sealed class WarmCountingEngine : ISttEngine
    {
        public int Warms;

        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Warms);
            return Task.CompletedTask;
        }

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single(null, null, false));

        public void Dispose() { }
    }

    private sealed class SilentEngine : ISttEngine
    {
        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single(null, null, false));

        public void Dispose() { }
    }
}
