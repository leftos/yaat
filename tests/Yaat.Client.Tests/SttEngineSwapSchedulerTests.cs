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

    private sealed class SilentEngine : ISttEngine
    {
        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single(null, null, false));

        public void Dispose() { }
    }
}
