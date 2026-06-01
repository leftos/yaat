using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class ScratchpadParserTests
{
    [Fact]
    public void BareSp1_ParsesToClearCommand()
    {
        var result = CommandParser.Parse("SP1");

        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<Scratchpad1Command>(result.Value);
        Assert.Equal("", cmd.Text);
    }

    [Fact]
    public void BareSp2_ParsesToClearCommand()
    {
        var result = CommandParser.Parse("SP2");

        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<Scratchpad2Command>(result.Value);
        Assert.Equal("", cmd.Text);
    }

    [Fact]
    public void Sp1WithArg_StillWorks()
    {
        var result = CommandParser.Parse("SP1 ABC");

        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<Scratchpad1Command>(result.Value);
        Assert.Equal("ABC", cmd.Text);
    }

    [Fact]
    public void Sp2WithArg_StillWorks()
    {
        var result = CommandParser.Parse("SP2 XYZ");

        Assert.True(result.IsSuccess);
        var cmd = Assert.IsType<Scratchpad2Command>(result.Value);
        Assert.Equal("XYZ", cmd.Text);
    }
}

public class ScratchpadUndoTests
{
    private const int MaxLen = 3;

    private static AircraftState MakeAircraft() => new() { Callsign = "TEST1", AircraftType = "B738" };

    [Fact]
    public void Sp1_SetThenClearThenClearAgain_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad1(ac, "ABC", MaxLen);
        TrackEngine.HandleScratchpad1(ac, "", MaxLen);
        TrackEngine.HandleScratchpad1(ac, "", MaxLen);

        Assert.Equal("ABC", ac.Stars.Scratchpad1);
        Assert.False(ac.Stars.WasScratchpad1Cleared);
    }

    [Fact]
    public void Sp1_SetThenSetSameValue_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad1(ac, "ABC", MaxLen);
        TrackEngine.HandleScratchpad1(ac, "ABC", MaxLen);

        Assert.Null(ac.Stars.Scratchpad1);
    }

    [Fact]
    public void Sp1_SetTwoValues_ToggleSecondRestoresFirst()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad1(ac, "ABC", MaxLen);
        TrackEngine.HandleScratchpad1(ac, "XYZ", MaxLen);
        TrackEngine.HandleScratchpad1(ac, "XYZ", MaxLen);

        Assert.Equal("ABC", ac.Stars.Scratchpad1);
    }

    [Fact]
    public void Sp1_ClearFromNull_NoopThenUndoRestoresNull()
    {
        var ac = MakeAircraft();

        // Initial state: null, not cleared
        TrackEngine.HandleScratchpad1(ac, "", MaxLen);
        Assert.True(ac.Stars.WasScratchpad1Cleared);

        // Clear again — undo restores null (previous was null)
        TrackEngine.HandleScratchpad1(ac, "", MaxLen);
        Assert.Null(ac.Stars.Scratchpad1);
    }

    [Fact]
    public void Sp2_SetThenClearThenClearAgain_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad2(ac, "XYZ", MaxLen);
        TrackEngine.HandleScratchpad2(ac, "", MaxLen);
        TrackEngine.HandleScratchpad2(ac, "", MaxLen);

        Assert.Equal("XYZ", ac.Stars.Scratchpad2);
    }

    [Fact]
    public void Sp2_SetThenSetSameValue_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad2(ac, "XYZ", MaxLen);
        TrackEngine.HandleScratchpad2(ac, "XYZ", MaxLen);

        Assert.Null(ac.Stars.Scratchpad2);
    }

    [Fact]
    public void Sp2_SetTwoValues_ToggleSecondRestoresFirst()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad2(ac, "ABC", MaxLen);
        TrackEngine.HandleScratchpad2(ac, "XYZ", MaxLen);
        TrackEngine.HandleScratchpad2(ac, "XYZ", MaxLen);

        Assert.Equal("ABC", ac.Stars.Scratchpad2);
    }
}

public class ScratchpadLengthLimitTests
{
    private static AircraftState MakeAircraft() => new() { Callsign = "TEST1", AircraftType = "B738" };

    [Fact]
    public void Sp1_AtLimit_Accepted()
    {
        var ac = MakeAircraft();

        var result = TrackEngine.HandleScratchpad1(ac, "ABC", 3);

        Assert.True(result.Success);
        Assert.Equal("ABC", ac.Stars.Scratchpad1);
    }

    [Fact]
    public void Sp1_OverLimit_RejectedAndUnchanged()
    {
        var ac = MakeAircraft();
        TrackEngine.HandleScratchpad1(ac, "ABC", 3);

        var result = TrackEngine.HandleScratchpad1(ac, "N346G", 3);

        Assert.False(result.Success);
        Assert.Equal("FORMAT", result.Message);
        Assert.Equal("ABC", ac.Stars.Scratchpad1); // prior value preserved
    }

    [Fact]
    public void Sp1_FourChars_RejectedAtLimit3_AcceptedAtLimit4()
    {
        var rejected = MakeAircraft();
        var rejResult = TrackEngine.HandleScratchpad1(rejected, "OAK1", 3);
        Assert.False(rejResult.Success);
        Assert.Null(rejected.Stars.Scratchpad1);

        var allowed = MakeAircraft();
        var okResult = TrackEngine.HandleScratchpad1(allowed, "OAK1", 4);
        Assert.True(okResult.Success);
        Assert.Equal("OAK1", allowed.Stars.Scratchpad1);
    }

    [Fact]
    public void Sp1_FiveChars_RejectedEvenWhenFourAllowed()
    {
        var ac = MakeAircraft();

        var result = TrackEngine.HandleScratchpad1(ac, "N346G", 4);

        Assert.False(result.Success);
        Assert.Null(ac.Stars.Scratchpad1);
    }

    [Fact]
    public void Sp2_OverLimit_RejectedAndUnchanged()
    {
        var ac = MakeAircraft();
        TrackEngine.HandleScratchpad2(ac, "XYZ", 3);

        var result = TrackEngine.HandleScratchpad2(ac, "ABCD", 3);

        Assert.False(result.Success);
        Assert.Equal("FORMAT", result.Message);
        Assert.Equal("XYZ", ac.Stars.Scratchpad2);
    }
}
