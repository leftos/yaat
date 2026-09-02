using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
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

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
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
}
