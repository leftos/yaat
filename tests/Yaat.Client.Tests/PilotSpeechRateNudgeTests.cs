using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public sealed class PilotSpeechRateNudgeTests
{
    [Fact]
    public void Apply_MultipliesTheSpeedByTheLoadFactor() => Assert.Equal(1.21, PilotSpeechRateNudge.Apply(1.1, 1.1), precision: 10);

    [Fact]
    public void Apply_FactorBelowOneSlowsTheSpeed() => Assert.Equal(0.9, PilotSpeechRateNudge.Apply(1.0, 0.9), precision: 10);

    [Fact]
    public void Apply_ClampsToTheEngineMaximum() => Assert.Equal(1.5, PilotSpeechRateNudge.Apply(1.5, 1.1));

    [Fact]
    public void Apply_ClampsToTheEngineMinimum() => Assert.Equal(0.75, PilotSpeechRateNudge.Apply(0.75, 0.9));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void Apply_InvalidFactorLeavesTheSpeedAlone(double loadFactor) => Assert.Equal(1.2, PilotSpeechRateNudge.Apply(1.2, loadFactor));
}
