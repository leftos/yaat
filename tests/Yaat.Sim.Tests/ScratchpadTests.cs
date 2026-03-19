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
    private static AircraftState MakeAircraft() => new() { Callsign = "TEST1", AircraftType = "B738" };

    [Fact]
    public void Sp1_SetThenClearThenClearAgain_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad1(ac, "ABC");
        TrackEngine.HandleScratchpad1(ac, "");
        TrackEngine.HandleScratchpad1(ac, "");

        Assert.Equal("ABC", ac.Scratchpad1);
        Assert.False(ac.WasScratchpad1Cleared);
    }

    [Fact]
    public void Sp1_SetThenSetSameValue_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad1(ac, "ABC");
        TrackEngine.HandleScratchpad1(ac, "ABC");

        Assert.Null(ac.Scratchpad1);
    }

    [Fact]
    public void Sp1_SetTwoValues_ToggleSecondRestoresFirst()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad1(ac, "ABC");
        TrackEngine.HandleScratchpad1(ac, "XYZ");
        TrackEngine.HandleScratchpad1(ac, "XYZ");

        Assert.Equal("ABC", ac.Scratchpad1);
    }

    [Fact]
    public void Sp1_ClearFromNull_NoopThenUndoRestoresNull()
    {
        var ac = MakeAircraft();

        // Initial state: null, not cleared
        TrackEngine.HandleScratchpad1(ac, "");
        Assert.True(ac.WasScratchpad1Cleared);

        // Clear again — undo restores null (previous was null)
        TrackEngine.HandleScratchpad1(ac, "");
        Assert.Null(ac.Scratchpad1);
    }

    [Fact]
    public void Sp2_SetThenClearThenClearAgain_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad2(ac, "XYZ");
        TrackEngine.HandleScratchpad2(ac, "");
        TrackEngine.HandleScratchpad2(ac, "");

        Assert.Equal("XYZ", ac.Scratchpad2);
    }

    [Fact]
    public void Sp2_SetThenSetSameValue_RestoresPrevious()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad2(ac, "XYZ");
        TrackEngine.HandleScratchpad2(ac, "XYZ");

        Assert.Null(ac.Scratchpad2);
    }

    [Fact]
    public void Sp2_SetTwoValues_ToggleSecondRestoresFirst()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleScratchpad2(ac, "ABC");
        TrackEngine.HandleScratchpad2(ac, "XYZ");
        TrackEngine.HandleScratchpad2(ac, "XYZ");

        Assert.Equal("ABC", ac.Scratchpad2);
    }
}
