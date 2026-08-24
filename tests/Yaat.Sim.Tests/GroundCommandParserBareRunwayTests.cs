using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// GitHub issue #393: a TAXI whose only token is a runway (<c>TAXI 1L</c>) parses as a
/// <em>destination</em> with no route, the way ATCTrainer scenarios author it for a departure already
/// at its bar. (What the handler then does with it — hold at the bar the aircraft is at, refuse it
/// anywhere else — is <c>GroundCommandHandler.TryTaxi</c>'s business, covered by the issue's E2E tests.)
/// The trailing-runway detector used to require two or more path tokens, so a lone runway stayed a path
/// token ("taxi ALONG runway 1L"): the pathfinder walked the full centerline to the far threshold, the
/// auto-detected departure runway became the reciprocal (19R), and a following <c>POS</c> had no
/// destination hold-short to arm against. A runway named ahead of taxiways (<c>TAXI 28R G D</c>) is
/// still taxied along.
/// </summary>
public class GroundCommandParserBareRunwayTests
{
    [Fact]
    public void RunwayBeforeParkingDestination_IsTaxiedAlong()
    {
        // The ramp is the destination; the runway is a path segment, not a takeoff assignment.
        var result = GroundCommandParser.ParseTaxi("G 28R @B12");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["G", "28R"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
        Assert.Equal("B12", taxi.DestinationParking);
    }

    [Theory]
    [InlineData("A RWY 28R @B12")]
    [InlineData("A RWY 28R $7A")]
    public void RunwayAssignmentWithParkingDestination_IsRejected(string arg)
    {
        Assert.False(GroundCommandParser.ParseTaxi(arg).IsSuccess);
        Assert.False(GroundCommandParser.ParseRwyTaxi($"28R TAXI {arg.Replace("RWY 28R ", "")}").IsSuccess);
    }

    [Theory]
    [InlineData("1L")]
    [InlineData("28L")]
    [InlineData("30")]
    public void ParseTaxi_LoneRunwayToken_IsDestination(string runway)
    {
        var result = GroundCommandParser.ParseTaxi(runway);

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Empty(taxi.Path);
        Assert.Equal(runway, taxi.DestinationRunway);
    }

    [Fact]
    public void ParseTaxi_ExplicitRwyKeywordWithoutPath_IsDestination()
    {
        var result = GroundCommandParser.ParseTaxi("RWY 1L");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Empty(taxi.Path);
        Assert.Equal("1L", taxi.DestinationRunway);
    }

    [Fact]
    public void ParseTaxi_RunwayAheadOfTaxiways_StaysTaxiAlong()
    {
        var result = GroundCommandParser.ParseTaxi("28R G D");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["28R", "G", "D"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
    }

    [Fact]
    public void ParseTaxi_LoneTaxiwayToken_IsPath()
    {
        var result = GroundCommandParser.ParseTaxi("B");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["B"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
    }

    [Fact]
    public void ParseTaxi_Empty_StillFails()
    {
        Assert.False(GroundCommandParser.ParseTaxi("").IsSuccess);
        Assert.False(GroundCommandParser.ParseTaxi("NODEL").IsSuccess);
    }
}
