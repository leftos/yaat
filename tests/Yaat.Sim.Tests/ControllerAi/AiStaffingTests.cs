using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>Who holds a position: by position for the cab roles (a shared TCP must not leak from tower to ground).</summary>
public class AiStaffingTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AiStaffingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void HeadlessStaffing_HumanHeldByPosition_IsTheSoloStudentsPositionOnly()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        AiPositionConfig tower = TestAiPositions.OakTower(_zoa);
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        var staffing = new HeadlessAiStaffing([ground, tower], scenario);

        Assert.False(staffing.IsHumanHeld(tower));
        Assert.False(staffing.IsHumanHeld(ground));

        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = tower.Identity;
        scenario.StudentPositionType = "TWR";
        staffing.Refresh();

        Assert.True(staffing.IsHumanHeld(tower));
        Assert.False(staffing.IsHumanHeld(ground));
        Assert.Equal([ground.PositionId], staffing.ActivePositions.Select(p => p.PositionId));

        // The track-owner form cannot tell the two apart (OAK_GND and OAK_TWR share TCP 3O) — which is why the cab
        // gates ask by position.
        Assert.True(staffing.IsHumanHeld(tower.Identity));
        Assert.True(staffing.IsHumanHeld(ground.Identity));
    }

    [Fact]
    public void HeadlessStaffing_StudentOnO90sOakTower_LeavesNctsOakTowerToTheAi()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        AiPositionConfig nctTower = TestAiPositions.OakTower(_zoa!);
        PositionConfig o90Position = _zoa!.FindPositionsByCallsign("OAK_TWR").First(p => _zoa.ResolvePosition(p.Id)!.FacilityId == "O90");
        AiPositionConfig o90Tower = nctTower with
        {
            Identity = _zoa.ResolvePosition(o90Position.Id)!,
            PositionId = o90Position.Id,
            FacilityId = "O90",
        };
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = o90Tower.Identity;
        scenario.StudentPositionType = "TWR";
        var staffing = new HeadlessAiStaffing([nctTower, o90Tower], scenario);

        Assert.Equal("NCT", nctTower.Identity.FacilityId);
        Assert.True(staffing.IsHumanHeld(o90Tower));
        Assert.False(staffing.IsHumanHeld(nctTower));
        Assert.Equal([nctTower.PositionId], staffing.ActivePositions.Select(p => p.PositionId));
    }
}
