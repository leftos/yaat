using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// <see cref="SpeechRecognitionService"/> and whether the chosen speech-recognition model is downloaded: push-to-talk is
/// refused and the prewarm leaves the engine alone while it is not, and both run once it is.
/// </summary>
public sealed class SpeechRecognitionServiceModelReadyTests
{
    [Fact]
    public void StartPtt_ModelNotDownloaded_RefusesWithModelMissing()
    {
        using var engine = new CountingEngine();
        using SpeechRecognitionService service = NewService(engine, () => false);
        List<SpeechStatus> statuses = [];
        service.StatusChanged += statuses.Add;

        bool started = service.StartPtt();

        Assert.False(started);
        Assert.Equal(SpeechStatus.ModelMissing, service.Status);
        Assert.Equal([SpeechStatus.ModelMissing], statuses);
    }

    [Fact]
    public void StartPtt_AfterTheModelIsDownloaded_Records()
    {
        bool ready = false;
        using var engine = new CountingEngine();
        using SpeechRecognitionService service = NewService(engine, () => ready);
        Assert.False(service.StartPtt());

        ready = true;
        bool started = service.StartPtt();

        Assert.True(started);
        Assert.Equal(SpeechStatus.Recording, service.Status);
    }

    [Fact]
    public async Task Prewarm_ModelNotDownloaded_SkipsTheSttWarmUp()
    {
        using var engine = new CountingEngine();
        using SpeechRecognitionService service = NewService(engine, () => false);

        await service.PrewarmAsync(() => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(0, engine.WarmUpCount);
        Assert.Equal(SpeechStatus.Idle, service.Status);
    }

    [Fact]
    public async Task Prewarm_ModelReady_WarmsTheEngineUp()
    {
        using var engine = new CountingEngine();
        using SpeechRecognitionService service = NewService(engine, () => true);

        await service.PrewarmAsync(() => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.WarmUpCount);
        Assert.Equal(SpeechStatus.Idle, service.Status);
    }

    private static SpeechRecognitionService NewService(ISttEngine engine, Func<bool> isModelReady)
    {
        var preferences = new UserPreferences();
        preferences.SetSpeechEnabled(true);
        return new SpeechRecognitionService(
            preferences,
            new StartingAudio(),
            engine,
            null,
            new PhraseologyCommandMapper(),
            null,
            null,
            () => new SpeechContext([], [], string.Empty),
            null,
            isModelReady
        );
    }

    /// <summary>A microphone that always opens and records nothing.</summary>
    private sealed class StartingAudio : IAudioCapture
    {
        public bool StartCapture() => true;

        public float[] StopCapture() => [];
    }

    private sealed class CountingEngine : ISttEngine
    {
        public int WarmUpCount { get; private set; }

        public Task WarmUpAsync(CancellationToken ct)
        {
            WarmUpCount++;
            return Task.CompletedTask;
        }

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single(null, null, false));

        public void Dispose() { }
    }
}
