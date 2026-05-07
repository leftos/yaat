using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class SpeedParserTests
{
    // --- SPD with modifiers ---

    [Fact]
    public void SpeedFloor_ParsedCorrectly()
    {
        var cmd = CommandParser.Parse("SPD 210+");
        var spd = Assert.IsType<SpeedCommand>(cmd.Value);
        Assert.Equal(210, spd.Speed);
        Assert.Equal(SpeedModifier.Floor, spd.Modifier);
    }

    [Fact]
    public void SpeedCeiling_ParsedCorrectly()
    {
        var cmd = CommandParser.Parse("SPD 210-");
        var spd = Assert.IsType<SpeedCommand>(cmd.Value);
        Assert.Equal(210, spd.Speed);
        Assert.Equal(SpeedModifier.Ceiling, spd.Modifier);
    }

    [Fact]
    public void SpeedExact_DefaultModifier()
    {
        var cmd = CommandParser.Parse("SPD 210");
        var spd = Assert.IsType<SpeedCommand>(cmd.Value);
        Assert.Equal(210, spd.Speed);
        Assert.Equal(SpeedModifier.None, spd.Modifier);
    }

    [Fact]
    public void SpeedZero_ResumesNormalSpeed()
    {
        var cmd = CommandParser.Parse("SPD 0");
        Assert.IsType<ResumeNormalSpeedCommand>(cmd.Value);
    }

    // --- RNS ---

    [Fact]
    public void Rns_ParsedCorrectly()
    {
        var cmd = CommandParser.Parse("RNS");
        Assert.IsType<ResumeNormalSpeedCommand>(cmd.Value);
    }

    [Fact]
    public void Ns_ParsedCorrectly()
    {
        var cmd = CommandParser.Parse("NS");
        Assert.IsType<ResumeNormalSpeedCommand>(cmd.Value);
    }

    // --- DSR ---

    [Fact]
    public void Dsr_ParsedCorrectly()
    {
        var cmd = CommandParser.Parse("DSR");
        Assert.IsType<DeleteSpeedRestrictionsCommand>(cmd.Value);
    }

    // --- ATFN condition ---

    [Fact]
    public void AtfnCondition_ParsedInCompound()
    {
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var compound = CommandParser.ParseCompound("ATFN 10 SPD 180");
        Assert.True(compound.IsSuccess);
        Assert.Single(compound.Value!.Blocks);

        var block = compound.Value!.Blocks[0];
        var condition = Assert.IsType<DistanceFinalCondition>(block.Condition);
        Assert.Equal(10, condition.DistanceNm);

        var cmd = Assert.IsType<SpeedCommand>(block.Commands[0]);
        Assert.Equal(180, cmd.Speed);
    }

    [Fact]
    public void AtfnChained_ParsedCorrectly()
    {
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var compound = CommandParser.ParseCompound("SPD 210; ATFN 10 SPD 180");
        Assert.True(compound.IsSuccess);
        Assert.Equal(2, compound.Value!.Blocks.Count);

        // First block: unconditional SPD 210
        Assert.Null(compound.Value!.Blocks[0].Condition);
        var spd1 = Assert.IsType<SpeedCommand>(compound.Value!.Blocks[0].Commands[0]);
        Assert.Equal(210, spd1.Speed);

        // Second block: ATFN 10 SPD 180
        var cond = Assert.IsType<DistanceFinalCondition>(compound.Value!.Blocks[1].Condition);
        Assert.Equal(10, cond.DistanceNm);
        var spd2 = Assert.IsType<SpeedCommand>(compound.Value!.Blocks[1].Commands[0]);
        Assert.Equal(180, spd2.Speed);
    }

    [Fact]
    public void SpeedUntil_LongAlias_ParsedInCompound()
    {
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var compound = CommandParser.ParseCompound("SPEED 210 UNTIL 10; SPEED 180 UNTIL 5");

        Assert.True(compound.IsSuccess, compound.Reason);
        Assert.Equal(3, compound.Value!.Blocks.Count);

        var spd1 = Assert.IsType<SpeedCommand>(compound.Value.Blocks[0].Commands[0]);
        Assert.Equal(210, spd1.Speed);

        var dist10 = Assert.IsType<DistanceFinalCondition>(compound.Value.Blocks[1].Condition);
        Assert.Equal(10, dist10.DistanceNm);
        var spd2 = Assert.IsType<SpeedCommand>(compound.Value.Blocks[1].Commands[0]);
        Assert.Equal(180, spd2.Speed);

        var dist5 = Assert.IsType<DistanceFinalCondition>(compound.Value.Blocks[2].Condition);
        Assert.Equal(5, dist5.DistanceNm);
        Assert.IsType<ResumeNormalSpeedCommand>(compound.Value.Blocks[2].Commands[0]);
    }
}
