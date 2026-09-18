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
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("SPD 210+");
        SpeedCommand spd = Assert.IsType<SpeedCommand>(cmd.Value);
        Assert.Equal(210, spd.Speed);
        Assert.Equal(SpeedModifier.Floor, spd.Modifier);
    }

    [Fact]
    public void SpeedCeiling_ParsedCorrectly()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("SPD 210-");
        SpeedCommand spd = Assert.IsType<SpeedCommand>(cmd.Value);
        Assert.Equal(210, spd.Speed);
        Assert.Equal(SpeedModifier.Ceiling, spd.Modifier);
    }

    [Fact]
    public void SpeedExact_DefaultModifier()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("SPD 210");
        SpeedCommand spd = Assert.IsType<SpeedCommand>(cmd.Value);
        Assert.Equal(210, spd.Speed);
        Assert.Equal(SpeedModifier.None, spd.Modifier);
    }

    [Fact]
    public void SpeedZero_ResumesNormalSpeed()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("SPD 0");
        Assert.IsType<ResumeNormalSpeedCommand>(cmd.Value);
    }

    // --- RNS ---

    [Fact]
    public void Rns_ParsedCorrectly()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RNS");
        Assert.IsType<ResumeNormalSpeedCommand>(cmd.Value);
    }

    [Fact]
    public void Ns_ParsedCorrectly()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("NS");
        Assert.IsType<ResumeNormalSpeedCommand>(cmd.Value);
    }

    // --- DSR ---

    [Fact]
    public void Dsr_ParsedCorrectly()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("DSR");
        Assert.IsType<DeleteSpeedRestrictionsCommand>(cmd.Value);
    }

    // --- ATFN condition ---

    [Fact]
    public void AtfnCondition_ParsedInCompound()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ParseResult<CompoundCommand> compound = CommandParser.ParseCompound("ATFN 10 SPD 180");
        Assert.True(compound.IsSuccess);
        Assert.Single(compound.Value!.Blocks);

        ParsedBlock block = compound.Value!.Blocks[0];
        DistanceFinalCondition condition = Assert.IsType<DistanceFinalCondition>(block.Condition);
        Assert.Equal(10, condition.DistanceNm);

        SpeedCommand cmd = Assert.IsType<SpeedCommand>(block.Commands[0]);
        Assert.Equal(180, cmd.Speed);
    }

    [Fact]
    public void AtfnChained_ParsedCorrectly()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ParseResult<CompoundCommand> compound = CommandParser.ParseCompound("SPD 210; ATFN 10 SPD 180");
        Assert.True(compound.IsSuccess);
        Assert.Equal(2, compound.Value!.Blocks.Count);

        // First block: unconditional SPD 210
        Assert.Null(compound.Value!.Blocks[0].Condition);
        SpeedCommand spd1 = Assert.IsType<SpeedCommand>(compound.Value!.Blocks[0].Commands[0]);
        Assert.Equal(210, spd1.Speed);

        // Second block: ATFN 10 SPD 180
        DistanceFinalCondition cond = Assert.IsType<DistanceFinalCondition>(compound.Value!.Blocks[1].Condition);
        Assert.Equal(10, cond.DistanceNm);
        SpeedCommand spd2 = Assert.IsType<SpeedCommand>(compound.Value!.Blocks[1].Commands[0]);
        Assert.Equal(180, spd2.Speed);
    }

    [Fact]
    public void SpeedUntil_LongAlias_ParsedInCompound()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ParseResult<CompoundCommand> compound = CommandParser.ParseCompound("SPEED 210 UNTIL 10; SPEED 180 UNTIL 5");

        Assert.True(compound.IsSuccess, compound.Reason);
        Assert.Equal(3, compound.Value!.Blocks.Count);

        SpeedCommand spd1 = Assert.IsType<SpeedCommand>(compound.Value.Blocks[0].Commands[0]);
        Assert.Equal(210, spd1.Speed);

        DistanceFinalCondition dist10 = Assert.IsType<DistanceFinalCondition>(compound.Value.Blocks[1].Condition);
        Assert.Equal(10, dist10.DistanceNm);
        SpeedCommand spd2 = Assert.IsType<SpeedCommand>(compound.Value.Blocks[1].Commands[0]);
        Assert.Equal(180, spd2.Speed);

        DistanceFinalCondition dist5 = Assert.IsType<DistanceFinalCondition>(compound.Value.Blocks[2].Condition);
        Assert.Equal(5, dist5.DistanceNm);
        Assert.IsType<ResumeNormalSpeedCommand>(compound.Value.Blocks[2].Commands[0]);
    }
}
