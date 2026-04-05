using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class GroundParserTests
{
    // --- TAXI @parking ---

    [Fact]
    public void TaxiAtParking_DirectRoute()
    {
        var cmd = CommandParser.Parse("TAXI @29");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Empty(taxi.Path);
        Assert.Equal("29", taxi.DestinationParking);
        Assert.Null(taxi.DestinationRunway);
    }

    [Fact]
    public void TaxiPathPlusParking()
    {
        var cmd = CommandParser.Parse("TAXI TE T @29");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["TE", "T"], taxi.Path);
        Assert.Equal("29", taxi.DestinationParking);
    }

    [Fact]
    public void TaxiParkingWithHoldShort()
    {
        var cmd = CommandParser.Parse("TAXI TE @B3 HS 30");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["TE"], taxi.Path);
        Assert.Equal("B3", taxi.DestinationParking);
        Assert.Equal(["30"], taxi.HoldShorts);
    }

    [Fact]
    public void TaxiNormalPath_NoParkingSet()
    {
        var cmd = CommandParser.Parse("TAXI TE T U W");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["TE", "T", "U", "W"], taxi.Path);
        Assert.Null(taxi.DestinationParking);
    }

    // --- LAND @ prefix ---

    [Fact]
    public void LandAtSpot_HasAtPrefix()
    {
        var cmd = CommandParser.Parse("LAND @H1");
        var land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("H1", land.SpotName);
        Assert.False(land.IsTaxiway);
    }

    [Fact]
    public void LandOnTaxiway_NoAtPrefix()
    {
        var cmd = CommandParser.Parse("LAND TE");
        var land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("TE", land.SpotName);
        Assert.True(land.IsTaxiway);
    }

    [Fact]
    public void LandAtSpot_WithNoDel()
    {
        var cmd = CommandParser.Parse("LAND @H1 NODEL");
        var land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("H1", land.SpotName);
        Assert.False(land.IsTaxiway);
        Assert.True(land.NoDelete);
    }

    [Fact]
    public void LandOnTaxiway_WithNoDel()
    {
        var cmd = CommandParser.Parse("LAND TE NODEL");
        var land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("TE", land.SpotName);
        Assert.True(land.IsTaxiway);
        Assert.True(land.NoDelete);
    }

    // --- RWY standalone ---

    [Fact]
    public void RwyStandalone_ReturnsAssignRunway()
    {
        var cmd = CommandParser.Parse("RWY 30");
        var assign = Assert.IsType<AssignRunwayCommand>(cmd.Value);
        Assert.Equal("30", assign.RunwayId);
    }

    [Fact]
    public void RwyStandalone_WithSuffix_ReturnsAssignRunway()
    {
        var cmd = CommandParser.Parse("RWY 28L");
        var assign = Assert.IsType<AssignRunwayCommand>(cmd.Value);
        Assert.Equal("28L", assign.RunwayId);
    }

    [Fact]
    public void RwyWithTaxiKeyword_NoPath_ReturnsAssignRunway()
    {
        var cmd = CommandParser.Parse("RWY 30 TAXI");
        var assign = Assert.IsType<AssignRunwayCommand>(cmd.Value);
        Assert.Equal("30", assign.RunwayId);
    }

    [Fact]
    public void RwyWithPath_ReturnsTaxiCommand()
    {
        var cmd = CommandParser.Parse("RWY 30 T U W");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["T", "U", "W"], taxi.Path);
        Assert.Equal("30", taxi.DestinationRunway);
    }

    [Fact]
    public void RwyWithTaxiKeywordAndPath_ReturnsTaxiCommand()
    {
        var cmd = CommandParser.Parse("RWY 30 TAXI D C B");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["D", "C", "B"], taxi.Path);
        Assert.Equal("30", taxi.DestinationRunway);
    }

    // --- TAXI with CROSS keyword ---

    [Fact]
    public void TaxiWithCross_SingleRunway()
    {
        var cmd = CommandParser.Parse("TAXI C B T U W CROSS 28R");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
        Assert.Equal(["28R"], taxi.CrossRunways);
    }

    [Fact]
    public void TaxiWithCross_TwoRunways()
    {
        var cmd = CommandParser.Parse("TAXI C B T U W CROSS 28R 28L");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
        Assert.Equal(["28R", "28L"], taxi.CrossRunways);
    }

    [Fact]
    public void TaxiWithCrossAndHoldShort()
    {
        var cmd = CommandParser.Parse("TAXI C B T U W CROSS 28R HS 28L");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Equal(["28R"], taxi.CrossRunways);
        Assert.Equal(["28L"], taxi.HoldShorts);
    }

    [Fact]
    public void TaxiWithCross_NoCrossRunways_WhenKeywordAbsent()
    {
        var cmd = CommandParser.Parse("TAXI C B T U W");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Null(taxi.CrossRunways);
    }

    [Fact]
    public void RwyTaxiWithCross()
    {
        var cmd = CommandParser.Parse("RWY 30 TAXI C B T U W CROSS 28R");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Equal("30", taxi.DestinationRunway);
        Assert.Equal(["28R"], taxi.CrossRunways);
    }

    // --- TAXI with !nodeId tokens ---

    [Fact]
    public void TaxiNodeRef_InPath()
    {
        var cmd = CommandParser.Parse("TAXI #42");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42"], taxi.Path);
    }

    [Fact]
    public void TaxiNodeRef_MultipleMixed()
    {
        var cmd = CommandParser.Parse("TAXI A #42 B");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["A", "#42", "B"], taxi.Path);
    }

    [Fact]
    public void TaxiNodeRef_WithHoldShort()
    {
        var cmd = CommandParser.Parse("TAXI #42 #18 HS 28L");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42", "#18"], taxi.Path);
        Assert.Equal(["28L"], taxi.HoldShorts);
    }

    [Fact]
    public void TaxiNodeRef_TrailingNotMistakenForRunway()
    {
        var cmd = CommandParser.Parse("TAXI #42 #30");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42", "#30"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
    }

    [Fact]
    public void TaxiNodeRef_WithCross()
    {
        var cmd = CommandParser.Parse("TAXI #42 #18 CROSS 28R");
        var taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42", "#18"], taxi.Path);
        Assert.Equal(["28R"], taxi.CrossRunways);
    }
}
