using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public sealed class PilotVoiceServiceTests
{
    /// <summary>A backstop for a worker that never speaks; a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Enqueue_SpeaksTransmissionsInOrder()
    {
        var synth = new SpySynthesizer { SpeechesExpected = 2 };
        await using var service = new PilotVoiceService(synth);

        service.Enqueue(new PilotTransmissionBroadcastDto("s1", "AAL1", "first", "Response", 12, 1, DateTime.UtcNow, 1.0), 70, true, 1.1);
        service.Enqueue(new PilotTransmissionBroadcastDto("s1", "AAL2", "second", "SayReadback", 34, 2, DateTime.UtcNow, 1.0), 80, false, 1.1);

        await synth.Spoke.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Collection(synth.Requests, first => Assert.Equal("first", first.Text), second => Assert.Equal("second", second.Text));
    }

    [Fact]
    public async Task Enqueue_PassesSpeechRateToSynthesizer()
    {
        var synth = new SpySynthesizer();
        await using var service = new PilotVoiceService(synth);

        service.Enqueue(new PilotTransmissionBroadcastDto("s1", "AAL1", "hello", "Response", 12, 1, DateTime.UtcNow, 1.0), 70, true, 1.25);

        await synth.Spoke.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1.25, Assert.Single(synth.Requests).SpeechRate);
    }

    [Fact]
    public async Task WarmAsync_LoadsTheVoiceOnce()
    {
        var synth = new SpySynthesizer();
        await using var service = new PilotVoiceService(synth);

        await service.WarmAsync(TestContext.Current.CancellationToken);
        await service.WarmAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, synth.Loads);
    }

    /// <summary>A missing pack and a failed load both leave nothing cached, so the warm neither throws nor stops a later one loading.</summary>
    [Fact]
    public async Task WarmAsync_WithNoPack_DoesNotThrowAndALaterCallRetries()
    {
        var synth = new SpySynthesizer { PackInstalled = false };
        await using var service = new PilotVoiceService(synth);

        await service.WarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, synth.Loads);

        synth.PackInstalled = true;
        synth.LoadFailure = new InvalidOperationException("the voice would not load");
        await service.WarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, synth.Loads);

        synth.LoadFailure = null;
        await service.WarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, synth.Loads);
    }

    [Fact]
    public async Task WarmAsync_ThenSpeak_DoesNotLoadAgain()
    {
        var synth = new SpySynthesizer();
        await using var service = new PilotVoiceService(synth);

        await service.WarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, synth.Loads);

        service.Enqueue(new PilotTransmissionBroadcastDto("s1", "AAL1", "hello", "Response", 12, 1, DateTime.UtcNow, 1.0), 70, true, 1.0);
        await synth.Spoke.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(1, synth.Loads);
    }

    /// <summary>Loads on the first warm or speak, as the real synthesizer does, and caches nothing without a pack or after a failure.</summary>
    private sealed class SpySynthesizer : IPilotVoiceSynthesizer
    {
        private bool _loaded;

        public List<PilotVoiceRequest> Requests { get; } = [];
        public bool IsAvailable => true;
        public bool PackInstalled { get; set; } = true;
        public Exception? LoadFailure { get; set; }
        public int Loads { get; private set; }
        public int SpeechesExpected { get; init; } = 1;

        /// <summary>Completes once <see cref="SpeechesExpected"/> transmissions have been spoken.</summary>
        public TaskCompletionSource Spoke { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WarmAsync(CancellationToken ct)
        {
            EnsureLoaded();
            return Task.CompletedTask;
        }

        public Task SpeakAsync(PilotVoiceRequest request, CancellationToken ct)
        {
            EnsureLoaded();
            Requests.Add(request);
            if (Requests.Count >= SpeechesExpected)
            {
                Spoke.TrySetResult();
            }
            return Task.CompletedTask;
        }

        private void EnsureLoaded()
        {
            if (_loaded || !PackInstalled)
            {
                return;
            }

            if (LoadFailure is not null)
            {
                throw LoadFailure;
            }

            _loaded = true;
            Loads++;
        }
    }
}
