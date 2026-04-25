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

    // --- PUSH cardinal direction syntax ---

    [Fact]
    public void Push_Bare()
    {
        var cmd = CommandParser.Parse("PUSH");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Null(push.MagneticHeading);
        Assert.Null(push.Taxiway);
        Assert.Null(push.FacingTaxiway);
        Assert.Null(push.DestinationParking);
        Assert.Null(push.DestinationSpot);
    }

    [Fact]
    public void Push_TaxiwayOnly()
    {
        var cmd = CommandParser.Parse("PUSH TE");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Null(push.MagneticHeading);
        Assert.Null(push.FacingTaxiway);
    }

    [Fact]
    public void Push_TaxiwayFacingTaxiway_KeepsLegacyForm()
    {
        var cmd = CommandParser.Parse("PUSH TE T");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal("T", push.FacingTaxiway);
        Assert.Null(push.MagneticHeading);
    }

    [Theory]
    [InlineData("PUSH FACE N", 360)]
    [InlineData("PUSH FACE NE", 45)]
    [InlineData("PUSH FACE E", 90)]
    [InlineData("PUSH FACE SE", 135)]
    [InlineData("PUSH FACE S", 180)]
    [InlineData("PUSH FACE SW", 225)]
    [InlineData("PUSH FACE W", 270)]
    [InlineData("PUSH FACE NW", 315)]
    public void Push_FaceCardinal(string input, int expectedDeg)
    {
        var cmd = CommandParser.Parse(input);
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.NotNull(push.MagneticHeading);
        Assert.Equal(expectedDeg, push.MagneticHeading!.Value.ToDisplayInt());
        Assert.Null(push.Taxiway);
    }

    [Theory]
    [InlineData("PUSH TAIL N", 180)]
    [InlineData("PUSH TAIL E", 270)]
    [InlineData("PUSH TAIL S", 360)]
    [InlineData("PUSH TAIL W", 90)]
    [InlineData("PUSH TAIL NE", 225)]
    [InlineData("PUSH TAIL SW", 45)]
    public void Push_TailCardinal_StoresReciprocalAsFacing(string input, int expectedDeg)
    {
        var cmd = CommandParser.Parse(input);
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.NotNull(push.MagneticHeading);
        Assert.Equal(expectedDeg, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Theory]
    [InlineData("PUSH >E", 90)]
    [InlineData("PUSH >W", 270)]
    [InlineData("PUSH >NE", 45)]
    [InlineData("PUSH <E", 270)]
    [InlineData("PUSH <W", 90)]
    [InlineData("PUSH <N", 180)]
    [InlineData("PUSH <NE", 225)]
    public void Push_ArrowCardinal(string input, int expectedDeg)
    {
        var cmd = CommandParser.Parse(input);
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.NotNull(push.MagneticHeading);
        Assert.Equal(expectedDeg, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_TaxiwayPlusFaceCardinal()
    {
        var cmd = CommandParser.Parse("PUSH TE FACE E");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal(90, push.MagneticHeading!.Value.ToDisplayInt());
        Assert.Null(push.FacingTaxiway);
    }

    [Fact]
    public void Push_TaxiwayPlusTailCardinal()
    {
        var cmd = CommandParser.Parse("PUSH TE TAIL W");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal(90, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_TaxiwayPlusArrow()
    {
        var cmd = CommandParser.Parse("PUSH TE <E");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal(270, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_ParkingPlusFace()
    {
        var cmd = CommandParser.Parse("PUSH @A10 FACE NE");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("A10", push.DestinationParking);
        Assert.Equal(45, push.MagneticHeading!.Value.ToDisplayInt());
        Assert.Null(push.Taxiway);
    }

    [Fact]
    public void Push_ParkingPlusArrow()
    {
        var cmd = CommandParser.Parse("PUSH @A10 >W");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("A10", push.DestinationParking);
        Assert.Equal(270, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_SpotPlusTail()
    {
        var cmd = CommandParser.Parse("PUSH $7A TAIL W");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("7A", push.DestinationSpot);
        Assert.Equal(90, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_BareTaxiwayN_StillParsesAsTaxiway()
    {
        // 'N' without a marker is treated as a taxiway name (regression guard).
        var cmd = CommandParser.Parse("PUSH N");
        var push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("N", push.Taxiway);
        Assert.Null(push.MagneticHeading);
    }

    [Fact]
    public void Push_NumericHeading_Rejected()
    {
        var cmd = CommandParser.Parse("PUSH 180");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Push_TaxiwayPlusNumeric_Rejected()
    {
        // After the cardinal rewrite, two-token form with numeric second token is not valid.
        var cmd = CommandParser.Parse("PUSH TE 180");
        Assert.False(cmd.IsSuccess);
    }

    [Theory]
    [InlineData("PUSH FACE")]
    [InlineData("PUSH TAIL")]
    [InlineData("PUSH FACE XY")]
    [InlineData("PUSH <ZZ")]
    [InlineData("PUSH TE FACE")]
    [InlineData("PUSH TE FACE XY")]
    public void Push_MalformedOrientation_Fails(string input)
    {
        var cmd = CommandParser.Parse(input);
        Assert.False(cmd.IsSuccess);
    }
}
