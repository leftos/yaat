using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
/// <summary>
/// Tests for the ZOA scenario parse fixes: ExpandMultiCommand, ExpandWait,
/// alias additions, SAY comma handling, bare CAPP/JFAC, $ taxi prefix,
/// TRACK/ACCEPT/TG optional args, SP alias, HOLD flexibility, AT bare, WAIT fix.
/// </summary>
public class ZoaParseFixTests : IDisposable
{
    // NavigationDatabase that resolves any of the fix names used in this test file.
    private static readonly NavigationDatabase Fixes = TestNavDbFactory.WithFixNames("SUNOL", "OAK", "ARCHI", "BRIXX", "BESSA", "VPBCK", "RBL");

    private readonly IDisposable _navDbScope;

    public ZoaParseFixTests()
    {
        _navDbScope = NavigationDatabase.ScopedOverride(Fixes);
    }

    public void Dispose() => _navDbScope.Dispose();

    // --- ExpandMultiCommand ---

    [Theory]
    [InlineData("FH 270 CM 5000", "FH 270, CM 5000")]
    [InlineData("TL 180 DM 3000", "TL 180, DM 3000")]
    [InlineData("TR 090 CM 100", "TR 090, CM 100")]
    [InlineData("CM 5000 FH 270", "CM 5000, FH 270")]
    [InlineData("DM 040 TL 180", "DM 040, TL 180")]
    [InlineData("SPD 210 DM 040", "SPD 210, DM 040")]
    [InlineData("FH 270 CM 5000 SPD 250", "FH 270, CM 5000, SPD 250")]
    [InlineData("DM 6000 FH 270 SPD 190", "DM 6000, FH 270, SPD 190")]
    public void ExpandMultiCommand_SplitsHeadingAltitudeCombos(string input, string expected)
    {
        Assert.Equal(expected, CommandSchemeParser.ExpandMultiCommand(input));
    }

    [Theory]
    [InlineData("FH 270")]
    [InlineData("CM 5000")]
    [InlineData("TAXI Y M1 HS A RWY 01L")]
    public void ExpandMultiCommand_LeavesNonMatchingInputUnchanged(string input)
    {
        Assert.Equal(input, CommandSchemeParser.ExpandMultiCommand(input));
    }

    [Fact]
    public void ParseCompound_FH_CM_ReturnsTwoCommands()
    {
        // First verify expansion works
        var expanded = CommandSchemeParser.ExpandMultiCommand("FH 270 CM 5000");
        Assert.Equal("FH 270, CM 5000", expanded);

        var result = CommandParser.ParseCompound("FH 270 CM 5000");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.Equal(2, result.Value!.Blocks[0].Commands.Count);
        Assert.IsType<FlyHeadingCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.IsType<ClimbMaintainCommand>(result.Value!.Blocks[0].Commands[1]);
    }

    // --- ExpandWait ---

    [Theory]
    [InlineData("WAIT 5 FH 270", "WAIT 5; FH 270")]
    [InlineData("WAIT 5 WAIT 10 FH 270", "WAIT 5; WAIT 10; FH 270")]
    [InlineData("DELAY 5 CM 3000", "WAIT 5; CM 3000")]
    public void ExpandWait_SplitsWaitFromFollowingCommand(string input, string expected)
    {
        Assert.Equal(expected, CommandSchemeParser.ExpandWait(input));
    }

    [Theory]
    [InlineData("WAIT 5")]
    [InlineData("FH 270")]
    [InlineData("CM 5000; SPD 250")]
    public void ExpandWait_LeavesNonWaitInputUnchanged(string input)
    {
        Assert.Equal(input, CommandSchemeParser.ExpandWait(input));
    }

    [Fact]
    public void ParseCompound_WaitThenHeading_ReturnsTwoBlocks()
    {
        var result = CommandParser.ParseCompound("WAIT 5 FH 270");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Blocks.Count);
        Assert.IsType<WaitCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.IsType<FlyHeadingCommand>(result.Value!.Blocks[1].Commands[0]);
    }

    // --- CF alias for CFIX ---

    [Fact]
    public void Parse_CF_ReturnsCrossFixCommand()
    {
        var result = CommandParser.Parse("CF SUNOL 050");
        Assert.NotNull(result.Value);
        Assert.IsType<CrossFixCommand>(result.Value);
    }

    // --- HOLD direction aliases ---

    [Fact]
    public void Parse_Hold_RightAlias()
    {
        var result = CommandParser.Parse("HOLD SUNOL 090 10 RIGHT");
        Assert.NotNull(result.Value);
        Assert.IsType<HoldingPatternCommand>(result.Value);
    }

    [Fact]
    public void Parse_Hold_LeftAlias()
    {
        var result = CommandParser.Parse("HOLD SUNOL 090 10 LEFT");
        Assert.NotNull(result.Value);
        Assert.IsType<HoldingPatternCommand>(result.Value);
    }

    // --- SAY with commas ---

    [Fact]
    public void ParseCompound_SayWithComma_DoesNotSplit()
    {
        var result = CommandParser.ParseCompound("SAY hello, world");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.Single(result.Value!.Blocks[0].Commands);
        var say = Assert.IsType<SayCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("hello, world", say.Text);
    }

    // --- Bare CAPP/JFAC ---

    [Fact]
    public void Parse_BareCapp_ReturnsNullApproachId()
    {
        var result = CommandParser.Parse("CAPP");
        Assert.NotNull(result.Value);
        var capp = Assert.IsType<ClearedApproachCommand>(result.Value);
        Assert.Null(capp.ApproachId);
    }

    [Fact]
    public void Parse_BareJfac_ReturnsNullApproachId()
    {
        var result = CommandParser.Parse("JFAC");
        Assert.NotNull(result.Value);
        var jfac = Assert.IsType<JoinFinalApproachCourseCommand>(result.Value);
        Assert.Null(jfac.ApproachId);
    }

    // --- TAXI @ and $ prefix ---

    [Fact]
    public void Parse_TaxiDollarPrefix_ParsesAsSpot()
    {
        var result = CommandParser.Parse("TAXI Y $10");
        Assert.NotNull(result.Value);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal("10", taxi.DestinationSpot);
        Assert.Null(taxi.DestinationParking);
    }

    [Fact]
    public void Parse_TaxiAtPrefix_ParsesAsParking()
    {
        var result = CommandParser.Parse("TAXI Y @A10");
        Assert.NotNull(result.Value);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal("A10", taxi.DestinationParking);
        Assert.Null(taxi.DestinationSpot);
    }

    // --- Alias additions ---

    [Theory]
    [InlineData("GW UAL123", typeof(GiveWayCommand))]
    [InlineData("SLN 250", typeof(ForceSpeedCommand))]
    [InlineData("SCRATCHPAD ABC", typeof(Scratchpad1Command))]
    [InlineData("SPAWNDELAY 5", typeof(SpawnDelayCommand))]
    [InlineData("DELAY 5", typeof(WaitCommand))]
    [InlineData("SQV", typeof(SquawkVfrCommand))]
    [InlineData("SN", typeof(SquawkNormalCommand))]
    [InlineData("SQS", typeof(SquawkStandbyCommand))]
    [InlineData("ID", typeof(IdentCommand))]
    [InlineData("POS", typeof(LineUpAndWaitCommand))]
    [InlineData("LU", typeof(LineUpAndWaitCommand))]
    [InlineData("PH", typeof(LineUpAndWaitCommand))]
    public void Parse_NewAliases(string input, Type expectedType)
    {
        var result = CommandParser.Parse(input);
        Assert.NotNull(result.Value);
        Assert.IsType(expectedType, result.Value);
    }

    // --- GW as compound condition (only with TAXI/RWY) ---

    [Fact]
    public void ParseCompound_GW_WithTaxi_AsCondition()
    {
        var result = CommandParser.ParseCompound("GW UAL123 TAXI T U W");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<GiveWayCondition>(result.Value!.Blocks[0].Condition);
        Assert.Single(result.Value!.Blocks[0].Commands);
        Assert.IsType<TaxiCommand>(result.Value!.Blocks[0].Commands[0]);
    }

    [Fact]
    public void ParseCompound_GW_WithRwyTaxi_AsCondition()
    {
        var result = CommandParser.ParseCompound("GW UAL123 RWY 17L TAXI T U W");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<GiveWayCondition>(result.Value!.Blocks[0].Condition);
    }

    [Fact]
    public void ParseCompound_GW_WithLocation()
    {
        var result = CommandParser.ParseCompound("GW AAL1944 G");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.Null(result.Value!.Blocks[0].Condition);
        var gw = Assert.IsType<GiveWayCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("AAL1944", gw.TargetCallsign);
        Assert.Equal("G", gw.Location);
    }

    // --- CTOMRT/CTOMLT ---

    [Fact]
    public void Parse_Ctomrt()
    {
        var result = CommandParser.Parse("CTOMRT");
        Assert.NotNull(result.Value);
        Assert.IsType<ClearedForTakeoffCommand>(result.Value);
    }

    [Fact]
    public void Parse_Ctomlt()
    {
        var result = CommandParser.Parse("CTOMLT");
        Assert.NotNull(result.Value);
        Assert.IsType<ClearedForTakeoffCommand>(result.Value);
    }

    // --- TRACK with optional TCP ---

    [Fact]
    public void Parse_TrackWithTcp_ReturnsTcpCode()
    {
        var result = CommandParser.Parse("TRACK OAK_41_CTR");
        Assert.NotNull(result.Value);
        var track = Assert.IsType<TrackAircraftCommand>(result.Value);
        Assert.Equal("OAK_41_CTR", track.TcpCode);
    }

    [Fact]
    public void Parse_TrackBare_ReturnsNullTcpCode()
    {
        var result = CommandParser.Parse("TRACK");
        Assert.NotNull(result.Value);
        var track = Assert.IsType<TrackAircraftCommand>(result.Value);
        Assert.Null(track.TcpCode);
    }

    // --- ACCEPT with optional callsign ---

    [Fact]
    public void Parse_AcceptWithCallsign_ReturnsCallsign()
    {
        var result = CommandParser.Parse("ACCEPT JBU33");
        Assert.NotNull(result.Value);
        var accept = Assert.IsType<AcceptHandoffCommand>(result.Value);
        Assert.Equal("JBU33", accept.Callsign);
    }

    // --- SP alias for SP1 ---

    [Fact]
    public void Parse_SP_Alias_ReturnsScratchpad1()
    {
        var result = CommandParser.Parse("SP OA1");
        Assert.NotNull(result.Value);
        var sp = Assert.IsType<Scratchpad1Command>(result.Value);
        Assert.Equal("OA1", sp.Text);
    }

    // --- AT + track/scratchpad commands ---

    [Fact]
    public void ParseCompound_AT_Track_WithTcp()
    {
        var result = CommandParser.ParseCompound("AT OAK TRACK OAK_41_CTR");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<AtFixCondition>(result.Value!.Blocks[0].Condition);
        Assert.Single(result.Value!.Blocks[0].Commands);
        var track = Assert.IsType<TrackAircraftCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.Equal("OAK_41_CTR", track.TcpCode);
    }

    [Fact]
    public void ParseCompound_AT_SP_Scratchpad()
    {
        var result = CommandParser.ParseCompound("AT ARCHI SP +RGT");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<AtFixCondition>(result.Value!.Blocks[0].Condition);
        Assert.Single(result.Value!.Blocks[0].Commands);
        Assert.IsType<Scratchpad1Command>(result.Value!.Blocks[0].Commands[0]);
    }

    // --- TG with optional runway ---

    [Fact]
    public void Parse_TG_WithRunway()
    {
        var result = CommandParser.Parse("TG 31");
        Assert.NotNull(result.Value);
        var tg = Assert.IsType<TouchAndGoCommand>(result.Value);
        Assert.Equal("31", tg.RunwayId);
    }

    [Fact]
    public void Parse_TG_Bare()
    {
        var result = CommandParser.Parse("TG");
        Assert.NotNull(result.Value);
        var tg = Assert.IsType<TouchAndGoCommand>(result.Value);
        Assert.Null(tg.RunwayId);
    }

    // --- SPD in ExpandMultiCommand ---

    [Fact]
    public void ExpandMultiCommand_SPD_DM()
    {
        Assert.Equal("SPD 210, DM 040", CommandSchemeParser.ExpandMultiCommand("SPD 210 DM 040"));
    }

    // --- HOLD flexibility ---

    [Fact]
    public void Parse_Hold_4Tokens_NoDirection_DefaultsRight()
    {
        // HOLD VPBCK 080 10 → fix=VPBCK, course=080, leg=10nm, direction=Right (default)
        var result = CommandParser.Parse("HOLD VPBCK 080 10");
        Assert.NotNull(result.Value);
        var hold = Assert.IsType<HoldingPatternCommand>(result.Value);
        Assert.Equal("VPBCK", hold.FixName);
        Assert.Equal(80, hold.InboundCourse);
        Assert.Equal(10, hold.LegLength);
        Assert.Equal(TurnDirection.Right, hold.Direction);
    }

    [Fact]
    public void Parse_Hold_3Tokens_Direction_DefaultsLeg1M()
    {
        // HOLD RBL 341 RIGHT → fix=RBL, course=341, direction=Right, leg=1M (default)
        var result = CommandParser.Parse("HOLD RBL 341 RIGHT");
        Assert.NotNull(result.Value);
        var hold = Assert.IsType<HoldingPatternCommand>(result.Value);
        Assert.Equal("RBL", hold.FixName);
        Assert.Equal(341, hold.InboundCourse);
        Assert.Equal(TurnDirection.Right, hold.Direction);
        Assert.Equal(1, hold.LegLength);
        Assert.True(hold.IsMinuteBased);
    }

    [Fact]
    public void Parse_Hold_4Tokens_Standard_StillWorks()
    {
        // Regression: HOLD SUNOL 090 10 RIGHT → standard 4-token form
        var result = CommandParser.Parse("HOLD SUNOL 090 10 RIGHT");
        Assert.NotNull(result.Value);
        var hold = Assert.IsType<HoldingPatternCommand>(result.Value);
        Assert.Equal("SUNOL", hold.FixName);
        Assert.Equal(90, hold.InboundCourse);
        Assert.Equal(10, hold.LegLength);
        Assert.False(hold.IsMinuteBased);
        Assert.Equal(TurnDirection.Right, hold.Direction);
    }

    // --- WAIT with fix name ---

    [Fact]
    public void ExpandWait_FixName_RewritesToAT()
    {
        // WAIT OAK WAIT 100 DM 5000 → AT OAK WAIT 100 DM 5000
        // (AT prefix is not a WAIT verb, so ExpandWait passes it through;
        //  the AT condition extraction + second ExpandWait in ParseBlockToCanonical does the rest)
        var expanded = CommandSchemeParser.ExpandWait("WAIT OAK WAIT 100 DM 5000");
        Assert.Equal("AT OAK WAIT 100 DM 5000", expanded);
    }

    [Fact]
    public void ParseCompound_WaitFixName_ParsesAsATCondition()
    {
        // End-to-end: WAIT OAK WAIT 100 DM 5000 → AT OAK condition + WAIT 100 + DM 5000
        var scheme = CommandScheme.Default();
        var result = CommandSchemeParser.ParseCompound("WAIT OAK WAIT 100 DM 5000", scheme);
        Assert.NotNull(result);
        Assert.Contains("AT OAK", result.CanonicalString);
    }

    // --- AT bare ---

    [Fact]
    public void ParseCompound_AT_Bare_ConditionOnly()
    {
        var result = CommandParser.ParseCompound("AT BRIXX");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<AtFixCondition>(result.Value!.Blocks[0].Condition);
        Assert.Empty(result.Value!.Blocks[0].Commands);
    }

    // --- WAIT N HOLD (combination of WAIT expansion + HOLD 3-token) ---

    [Fact]
    public void ParseCompound_Wait_Hold_3Tokens()
    {
        // WAIT 150 HOLD RBL 341 RIGHT → WAIT 150; HOLD RBL 341 RIGHT
        var result = CommandParser.ParseCompound("WAIT 150 HOLD RBL 341 RIGHT");
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Blocks.Count);
        Assert.IsType<WaitCommand>(result.Value!.Blocks[0].Commands[0]);
        Assert.IsType<HoldingPatternCommand>(result.Value!.Blocks[1].Commands[0]);
    }

    // --- PO bare (point out with no TCP) ---

    [Fact]
    public void Parse_PO_Bare_ReturnsNullTcpCode()
    {
        var result = CommandParser.Parse("PO");
        Assert.NotNull(result.Value);
        var po = Assert.IsType<PointOutCommand>(result.Value);
        Assert.Null(po.TcpCode);
    }

    [Fact]
    public void ParseCompound_AT_PO_Bare()
    {
        var result = CommandParser.ParseCompound("AT BESSA PO");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<AtFixCondition>(result.Value!.Blocks[0].Condition);
        Assert.Single(result.Value!.Blocks[0].Commands);
        Assert.IsType<PointOutCommand>(result.Value!.Blocks[0].Commands[0]);
    }
}
