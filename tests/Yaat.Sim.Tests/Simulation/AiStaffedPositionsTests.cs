using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="SimScenarioState.SetAiStaffedPositions"/> bumps the staffing version on any configuration change — not
/// only membership — so the memoized pilot-contact roster never serves a stale radio name or airport list.
/// </summary>
public class AiStaffedPositionsTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AiStaffedPositionsTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SameConfiguration_DoesNotBumpTheVersion()
    {
        if (_zoa is null)
        {
            return;
        }

        var scenario = NewScenario();
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa)]);
        var version = scenario.AiStaffingVersion;

        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa)]);

        Assert.Equal(version, scenario.AiStaffingVersion);
    }

    [Fact]
    public void RadioNameChange_ForTheSameId_BumpsTheVersion_AndTheRosterSeesIt()
    {
        if (_zoa is null)
        {
            return;
        }

        var scenario = NewScenario();
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa)]);
        var version = scenario.AiStaffingVersion;
        Assert.Equal("Oakland Ground", scenario.PilotContacts.Positions[0].RadioName);

        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa) with { RadioName = "Metro Ground" }]);

        Assert.Equal(version + 1, scenario.AiStaffingVersion);
        Assert.Equal("Metro Ground", scenario.PilotContacts.Positions[0].RadioName);
    }

    [Fact]
    public void AirportListChange_ForTheSameId_BumpsTheVersion()
    {
        if (_zoa is null)
        {
            return;
        }

        var scenario = NewScenario();
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa)]);
        var version = scenario.AiStaffingVersion;

        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa) with { AirportIds = ["OAK", "HWD"] }]);

        Assert.Equal(version + 1, scenario.AiStaffingVersion);
    }

    private static SimScenarioState NewScenario() =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
        };
}
