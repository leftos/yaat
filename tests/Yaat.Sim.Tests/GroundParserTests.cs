using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class GroundParserTests
{
    // --- TAXI @parking ---

    [Fact]
    public void TaxiAtParking_DirectRoute()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI @29");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Empty(taxi.Path);
        Assert.Equal("29", taxi.DestinationParking);
        Assert.Null(taxi.DestinationRunway);
    }

    [Fact]
    public void TaxiPathPlusParking()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI TE T @29");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["TE", "T"], taxi.Path);
        Assert.Equal("29", taxi.DestinationParking);
    }

    [Fact]
    public void TaxiParkingWithHoldShort()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI TE @B3 HS 30");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["TE"], taxi.Path);
        Assert.Equal("B3", taxi.DestinationParking);
        Assert.Equal(["30"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void TaxiNormalPath_NoParkingSet()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI TE T U W");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["TE", "T", "U", "W"], taxi.Path);
        Assert.Null(taxi.DestinationParking);
    }

    // --- LAND @ prefix ---

    [Fact]
    public void LandAtSpot_HasAtPrefix()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LAND @H1");
        LandCommand land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("H1", land.SpotName);
        Assert.False(land.IsTaxiway);
    }

    [Fact]
    public void LandOnTaxiway_NoAtPrefix()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LAND TE");
        LandCommand land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("TE", land.SpotName);
        Assert.True(land.IsTaxiway);
    }

    [Fact]
    public void LandAtSpot_WithNoDel()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LAND @H1 NODEL");
        LandCommand land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("H1", land.SpotName);
        Assert.False(land.IsTaxiway);
        Assert.True(land.NoDelete);
    }

    [Fact]
    public void LandOnTaxiway_WithNoDel()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LAND TE NODEL");
        LandCommand land = Assert.IsType<LandCommand>(cmd.Value);
        Assert.Equal("TE", land.SpotName);
        Assert.True(land.IsTaxiway);
        Assert.True(land.NoDelete);
    }

    // --- LAND / EXIT strict NODEL validation ---
    // A mistyped or extra trailing token must be rejected, not silently dropped — otherwise
    // a fat-fingered NODEL leaves the aircraft auto-deleting when the controller meant to keep it.

    [Fact]
    public void Land_BadSecondToken_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LAND TE FOO");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Land_ExtraTokens_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LAND TE NODEL EXTRA");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Exit_WithNoDel_SetsNoDelete()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("EXIT B NODEL");
        ExitTaxiwayCommand exit = Assert.IsType<ExitTaxiwayCommand>(cmd.Value);
        Assert.Equal("B", exit.Taxiway);
        Assert.True(exit.NoDelete);
    }

    [Fact]
    public void Exit_BadSecondToken_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("EXIT B FOO");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Exit_ExtraTokens_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("EXIT B NODEL EXTRA");
        Assert.False(cmd.IsSuccess);
    }

    // --- RWY standalone ---

    [Fact]
    public void RwyStandalone_ReturnsAssignRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RWY 30");
        AssignRunwayCommand assign = Assert.IsType<AssignRunwayCommand>(cmd.Value);
        Assert.Equal("30", assign.RunwayId);
    }

    [Fact]
    public void RwyStandalone_WithSuffix_ReturnsAssignRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RWY 28L");
        AssignRunwayCommand assign = Assert.IsType<AssignRunwayCommand>(cmd.Value);
        Assert.Equal("28L", assign.RunwayId);
    }

    [Fact]
    public void RwyWithTaxiKeyword_NoPath_ReturnsAssignRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RWY 30 TAXI");
        AssignRunwayCommand assign = Assert.IsType<AssignRunwayCommand>(cmd.Value);
        Assert.Equal("30", assign.RunwayId);
    }

    [Fact]
    public void RwyWithPath_ReturnsTaxiCommand()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RWY 30 T U W");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["T", "U", "W"], taxi.Path);
        Assert.Equal("30", taxi.DestinationRunway);
    }

    [Fact]
    public void RwyWithTaxiKeywordAndPath_ReturnsTaxiCommand()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RWY 30 TAXI D C B");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["D", "C", "B"], taxi.Path);
        Assert.Equal("30", taxi.DestinationRunway);
    }

    // --- TAXI with CROSS keyword ---

    [Fact]
    public void TaxiWithCross_SingleRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI C B T U W CROSS 28R");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
        Assert.Equal(["28R"], taxi.CrossRunways);
    }

    [Fact]
    public void TaxiWithCross_TwoRunways()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI C B T U W CROSS 28R 28L");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
        Assert.Equal(["28R", "28L"], taxi.CrossRunways);
    }

    [Fact]
    public void TaxiWithCrossAndHoldShort()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI C B T U W CROSS 28R HS 28L");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Equal(["28R"], taxi.CrossRunways);
        Assert.Equal(["28L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void TaxiWithCross_NoCrossRunways_WhenKeywordAbsent()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI C B T U W");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Null(taxi.CrossRunways);
    }

    [Fact]
    public void RwyTaxiWithCross()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("RWY 30 TAXI C B T U W CROSS 28R");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["C", "B", "T", "U", "W"], taxi.Path);
        Assert.Equal("30", taxi.DestinationRunway);
        Assert.Equal(["28R"], taxi.CrossRunways);
    }

    /// <summary>
    /// The form COMMANDS.md gives scenario authors for pre-clearing crossings the file always expects
    /// (issue #314's closed 1L/1R at SFO): several runways after <c>CROSS</c>, then an explicit
    /// <c>RWY</c> for the destination. The <c>RWY</c> keyword is load-bearing — trailing-runway
    /// detection only inspects the path, and <c>CROSS</c> diverts tokens away from it, so without
    /// <c>RWY</c> the 28L joins the crossing list instead of becoming the destination.
    /// </summary>
    [Fact]
    public void TaxiWithCross_MultipleRunways_ThenExplicitDestinationRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI T6A A F CROSS 1L 1R RWY 28L");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["T6A", "A", "F"], taxi.Path);
        Assert.Equal(["1L", "1R"], taxi.CrossRunways);
        Assert.Equal("28L", taxi.DestinationRunway);
    }

    // --- TAXI with !nodeId tokens ---

    [Fact]
    public void TaxiNodeRef_InPath()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI #42");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42"], taxi.Path);
    }

    [Fact]
    public void TaxiNodeRef_MultipleMixed()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI A #42 B");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["A", "#42", "B"], taxi.Path);
    }

    [Fact]
    public void TaxiNodeRef_WithHoldShort()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI #42 #18 HS 28L");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42", "#18"], taxi.Path);
        Assert.Equal(["28L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void TaxiNodeRef_TrailingNotMistakenForRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI #42 #30");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42", "#30"], taxi.Path);
        Assert.Null(taxi.DestinationRunway);
    }

    [Fact]
    public void TaxiNodeRef_WithCross()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXI #42 #18 CROSS 28R");
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(cmd.Value);
        Assert.Equal(["#42", "#18"], taxi.Path);
        Assert.Equal(["28R"], taxi.CrossRunways);
    }

    // --- PUSH cardinal direction syntax ---

    [Fact]
    public void Push_Bare()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Null(push.MagneticHeading);
        Assert.Null(push.Taxiway);
        Assert.Null(push.FacingTaxiway);
        Assert.Null(push.DestinationParking);
        Assert.Null(push.DestinationSpot);
    }

    [Fact]
    public void Push_TaxiwayOnly()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH TE");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Null(push.MagneticHeading);
        Assert.Null(push.FacingTaxiway);
    }

    [Fact]
    public void Push_TaxiwayFacingTaxiway_KeepsLegacyForm()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH TE T");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
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
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
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
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
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
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.NotNull(push.MagneticHeading);
        Assert.Equal(expectedDeg, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_TaxiwayPlusFaceCardinal()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH TE FACE E");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal(90, push.MagneticHeading!.Value.ToDisplayInt());
        Assert.Null(push.FacingTaxiway);
    }

    [Fact]
    public void Push_TaxiwayPlusTailCardinal()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH TE TAIL W");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal(90, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_TaxiwayPlusArrow()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH TE <E");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("TE", push.Taxiway);
        Assert.Equal(270, push.MagneticHeading!.Value.ToDisplayInt());
    }

    /// <summary>A push to a stand parks on the stand's own heading, so a facing with it is refused.</summary>
    [Theory]
    [InlineData("PUSH @A10 FACE NE")]
    [InlineData("PUSH @A10 >W")]
    public void Push_ParkingPlusFacing_Refused(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);

        Assert.False(cmd.IsSuccess, $"'{input}' parsed as {cmd.Value}");
        Assert.Contains("PUSH @A10 does not take a facing — the aircraft parks on the stand's own heading", cmd.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// PUSH reads a <c>@gate</c>/<c>$spot</c> destination off its first argument alone, so a sigil token
    /// anywhere later is refused instead of being silently read as a facing taxiway.
    /// </summary>
    [Theory]
    [InlineData("PUSH TE @B27")]
    [InlineData("PUSH TE $7A")]
    [InlineData("PUSH $6A @B27")]
    [InlineData("PUSH FACE N @B27")]
    public void Push_SigilPastTheFirstToken_Refused(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);

        Assert.False(cmd.IsSuccess, $"'{input}' parsed as {cmd.Value}");
        Assert.Contains("must be the first PUSH argument", cmd.Reason, StringComparison.Ordinal);
    }

    /// <summary>A sigil with no name behind it names nothing, and says so rather than reporting misplacement.</summary>
    [Theory]
    [InlineData("PUSH @", "@ needs a gate or helipad name")]
    [InlineData("PUSH $", "$ needs a spot name")]
    [InlineData("PUSH TE @", "@ needs a gate or helipad name")]
    public void Push_BareSigil_Refused(string input, string expectedReason)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);

        Assert.False(cmd.IsSuccess, $"'{input}' parsed as {cmd.Value}");
        Assert.Contains(expectedReason, cmd.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_SpotPlusTail()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH $7A TAIL W");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("7A", push.DestinationSpot);
        Assert.Equal(90, push.MagneticHeading!.Value.ToDisplayInt());
    }

    [Fact]
    public void Push_BareTaxiwayN_StillParsesAsTaxiway()
    {
        // 'N' without a marker is treated as a taxiway name (regression guard).
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH N");
        PushbackCommand push = Assert.IsType<PushbackCommand>(cmd.Value);
        Assert.Equal("N", push.Taxiway);
        Assert.Null(push.MagneticHeading);
    }

    [Fact]
    public void Push_NumericHeading_Rejected()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH 180");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Push_TaxiwayPlusNumeric_Rejected()
    {
        // After the cardinal rewrite, two-token form with numeric second token is not valid.
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("PUSH TE 180");
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
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        Assert.False(cmd.IsSuccess);
    }

    // --- CROSS ---

    [Fact]
    public void Cross_BareNoArgument()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS");
        CrossRunwayCommand cross = Assert.IsType<CrossRunwayCommand>(cmd.Value);
        Assert.Empty(cross.RunwayIds);
        Assert.Empty(cross.HoldShorts);
    }

    [Fact]
    public void Cross_NamedRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS 28R");
        CrossRunwayCommand cross = Assert.IsType<CrossRunwayCommand>(cmd.Value);
        Assert.Equal("28R", Assert.Single(cross.RunwayIds));
        Assert.Empty(cross.HoldShorts);
    }

    [Fact]
    public void Cross_NamedRunwayLowercase_UppercasesArgument()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS 28r");
        CrossRunwayCommand cross = Assert.IsType<CrossRunwayCommand>(cmd.Value);
        Assert.Equal("28R", Assert.Single(cross.RunwayIds));
    }

    [Fact]
    public void Cross_TwoRunways_ParsesBothInOrder()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS 28R 28L");
        CrossRunwayCommand cross = Assert.IsType<CrossRunwayCommand>(cmd.Value);
        Assert.Equal(["28R", "28L"], cross.RunwayIds);
        Assert.Empty(cross.HoldShorts);
    }

    [Fact]
    public void Cross_RunwayWithHoldShort_SplitsAtHsKeyword()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS 28R HS 20");
        CrossRunwayCommand cross = Assert.IsType<CrossRunwayCommand>(cmd.Value);
        Assert.Equal(["28R"], cross.RunwayIds);
        Assert.Equal(["20"], cross.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void Cross_MultipleRunwaysAndHoldShorts()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS 28R 28L HS 20 B");
        CrossRunwayCommand cross = Assert.IsType<CrossRunwayCommand>(cmd.Value);
        Assert.Equal(["28R", "28L"], cross.RunwayIds);
        Assert.Equal(["20", "B"], cross.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void Cross_HsKeywordWithNoTarget_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CROSS 28R HS");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Cross_CanonicalRoundTrip_MultiRunwayAndHoldShort()
    {
        var cmd = new CrossRunwayCommand(["28R", "28L"], [HoldShortTarget.Parse("20")]);
        Assert.Equal("CROSS 28R 28L HS 20", CommandDescriber.DescribeCommand(cmd));

        CrossRunwayCommand reparsed = Assert.IsType<CrossRunwayCommand>(CommandParser.Parse("CROSS 28R 28L HS 20").Value);
        Assert.Equal(["28R", "28L"], reparsed.RunwayIds);
        Assert.Equal(["20"], reparsed.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void Cross_NaturalDescription_PluralizesRunways()
    {
        Assert.Equal("Cross runway 28R", CommandDescriber.DescribeNatural(new CrossRunwayCommand(["28R"], [])));
        Assert.Equal("Cross runways 28R and 28L", CommandDescriber.DescribeNatural(new CrossRunwayCommand(["28R", "28L"], [])));
    }
}
