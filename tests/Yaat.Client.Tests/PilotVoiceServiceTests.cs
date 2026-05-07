using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public sealed class PilotVoiceServiceTests
{
    [Fact]
    public async Task Enqueue_SpeaksTransmissionsInOrder()
    {
        var synth = new SpySynthesizer();
        await using var service = new PilotVoiceService(synth);

        service.Enqueue(new PilotTransmissionBroadcastDto("s1", "AAL1", "first", "Response", 12, 1, DateTime.UtcNow), 70, true);
        service.Enqueue(new PilotTransmissionBroadcastDto("s1", "AAL2", "second", "SayReadback", 34, 2, DateTime.UtcNow), 80, false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (synth.Requests.Count < 2)
        {
            await Task.Delay(10, cts.Token);
        }

        Assert.Collection(synth.Requests, first => Assert.Equal("first", first.Text), second => Assert.Equal("second", second.Text));
    }

    private sealed class SpySynthesizer : IPilotVoiceSynthesizer
    {
        public List<PilotVoiceRequest> Requests { get; } = [];
        public bool IsAvailable => true;

        public Task SpeakAsync(PilotVoiceRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
