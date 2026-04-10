using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression coverage for the `$` taxi-spot prefix in TAXI commands.
/// Locks in the parser contract that the client-side command builder relies on.
/// </summary>
public class GroundCommandParserTaxiSpotTests
{
    [Fact]
    public void ParseTaxi_With_DollarPrefix_Sets_DestinationSpot()
    {
        var result = GroundCommandParser.ParseTaxi("T9 A F HS 01L $I8L");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal("I8L", taxi.DestinationSpot);
        Assert.Null(taxi.DestinationParking);
        Assert.Equal(["T9", "A", "F"], taxi.Path);
        Assert.Equal(["01L"], taxi.HoldShorts);
    }

    [Fact]
    public void ParseTaxi_With_AtPrefix_Sets_DestinationParking()
    {
        var result = GroundCommandParser.ParseTaxi("T9 A F HS 01L @A12");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal("A12", taxi.DestinationParking);
        Assert.Null(taxi.DestinationSpot);
    }

    [Fact]
    public void ParseTaxi_BareDollarSpot_Sets_DestinationSpot()
    {
        var result = GroundCommandParser.ParseTaxi("$I8L");

        Assert.True(result.IsSuccess);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal("I8L", taxi.DestinationSpot);
        Assert.Null(taxi.DestinationParking);
        Assert.Empty(taxi.Path);
    }
}
