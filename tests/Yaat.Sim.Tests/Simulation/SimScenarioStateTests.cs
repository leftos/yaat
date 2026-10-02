using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

public class SimScenarioStateTests
{
    [Theory]
    [InlineData(true, 2, 5)]
    [InlineData(true, 8, 8)]
    [InlineData(true, 0, 5)]
    [InlineData(true, -1, 5)]
    [InlineData(false, 2, 2)]
    public void EffectiveAutoAcceptDelay_SoloFloorsAtFive_NonSoloIsRaw(bool solo, int configuredSeconds, double expectedSeconds)
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = solo,
            AutoAcceptDelay = TimeSpan.FromSeconds(configuredSeconds),
        };

        Assert.Equal(expectedSeconds, scenario.EffectiveAutoAcceptDelaySeconds);
    }
}
