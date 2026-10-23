using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public sealed class PilotSpeechRateTests
{
    [Theory]
    [InlineData(FrequencyLoadBand.Busy, 1.1)]
    [InlineData(FrequencyLoadBand.Normal, 1.0)]
    [InlineData(FrequencyLoadBand.Quiet, 1.0)]
    public void LoadFactor_FollowsTheBand(FrequencyLoadBand band, double expected) => Assert.Equal(expected, PilotSpeechRate.LoadFactor(band));
}
