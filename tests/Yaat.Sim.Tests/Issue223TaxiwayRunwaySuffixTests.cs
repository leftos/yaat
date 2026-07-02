using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression coverage for GitHub issue #223: at OAK, <c>TAXI W B T TC @10</c> was rejected
/// with "Taxiway T does not reach runway TC" because <see cref="CommandParser.IsRunwayArg"/>
/// treated any 2+ character token ending in L/C/R as a runway. Taxiway <c>TC</c> ends in <c>C</c>,
/// so the trailing-runway detector peeled it off the path as a phantom destination runway.
/// A real runway is always 1-2 digits with an optional L/C/R suffix (e.g. 28R, 9L, 30).
/// </summary>
public class Issue223TaxiwayRunwaySuffixTests
{
    [Theory]
    [InlineData("TC")] // OAK ramp connector — the issue
    [InlineData("TE")] // sanity: ends in E, never was a runway
    [InlineData("AC")]
    [InlineData("BR")]
    [InlineData("SL")]
    public void IsRunwayArg_LetterPrefixedSuffix_IsNotRunway(string token)
    {
        Assert.False(CommandParser.IsRunwayArg(token));
    }

    [Theory]
    [InlineData("28R")]
    [InlineData("10L")]
    [InlineData("16C")]
    [InlineData("9L")] // single-digit runway with suffix
    [InlineData("1R")]
    [InlineData("30")] // bare 2-digit runway
    [InlineData("28L")]
    public void IsRunwayArg_RealRunway_IsRunway(string token)
    {
        Assert.True(CommandParser.IsRunwayArg(token));
    }

    [Fact]
    public void ParseTaxi_KeepsTaxiwayEndingInC_InPath()
    {
        var result = GroundCommandParser.ParseTaxi("W B T TC @10");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        // TC must remain a path taxiway, not be peeled off as a destination runway.
        Assert.Equal(["W", "B", "T", "TC"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
        Assert.Equal("10", taxi.DestinationParking);
    }

    [Fact]
    public void ParseTaxi_StillDetectsTrailingRealRunway()
    {
        // The trailing-runway convenience must keep working for genuine runways.
        var result = GroundCommandParser.ParseTaxi("C B 28R");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["C", "B"], taxi.Path);
        Assert.Equal("28R", taxi.DestinationRunway);
    }
}
