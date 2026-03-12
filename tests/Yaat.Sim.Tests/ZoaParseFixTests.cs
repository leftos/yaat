using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the ZOA scenario parse fixes: ExpandMultiCommand, ExpandWait,
/// alias additions, SAY comma handling, bare CAPP/JFAC, $ taxi prefix,
/// TRACK/ACCEPT/TG optional args, SP alias, HOLD flexibility, AT bare, WAIT fix.
/// </summary>
public class ZoaParseFixTests
{
    private static readonly IFixLookup Fixes = new PermissiveFixes();

    // --- ExpandMultiCommand ---

    [Theory]
    [InlineData("FH 270 CM 5000", "FH 270, CM 5000")]
    [InlineData("TL 180 DM 3000", "TL 180, DM 3000")]
    [InlineData("TR 090 CM 100", "TR 090, CM 100")]
    [InlineData("CM 5000 FH 270", "CM 5000, FH 270")]
    [InlineData("DM 040 TL 180", "DM 040, TL 180")]
    [InlineData("SPD 210 DM 040", "SPD 210, DM 040")]
    public void ExpandMultiCommand_SplitsHeadingAltitudeCombos(string input, string expected)
    {
        Assert.Equal(expected, CommandSchemeParser.ExpandMultiCommand(input));
    }

    [Theory]
    [InlineData("FH 270")]
    [InlineData("CM 5000")]
    [InlineData("TAXI Y M1 HS A RWY 01L")]
    [InlineData("FH 270 CM 5000 SPD 250")]
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

        var result = CommandParser.ParseCompound("FH 270 CM 5000", Fixes);

        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.Equal(2, result.Blocks[0].Commands.Count);
        Assert.IsType<FlyHeadingCommand>(result.Blocks[0].Commands[0]);
        Assert.IsType<ClimbMaintainCommand>(result.Blocks[0].Commands[1]);
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
        var result = CommandParser.ParseCompound("WAIT 5 FH 270", Fixes);

        Assert.NotNull(result);
        Assert.Equal(2, result.Blocks.Count);
        Assert.IsType<WaitCommand>(result.Blocks[0].Commands[0]);
        Assert.IsType<FlyHeadingCommand>(result.Blocks[1].Commands[0]);
    }

    // --- CF alias for CFIX ---

    [Fact]
    public void Parse_CF_ReturnsCrossFixCommand()
    {
        var result = CommandParser.Parse("CF SUNOL 050", Fixes);
        Assert.NotNull(result);
        Assert.IsType<CrossFixCommand>(result);
    }

    // --- HOLD direction aliases ---

    [Fact]
    public void Parse_Hold_RightAlias()
    {
        var result = CommandParser.Parse("HOLD SUNOL 090 10 RIGHT", Fixes);
        Assert.NotNull(result);
        Assert.IsType<HoldingPatternCommand>(result);
    }

    [Fact]
    public void Parse_Hold_LeftAlias()
    {
        var result = CommandParser.Parse("HOLD SUNOL 090 10 LEFT", Fixes);
        Assert.NotNull(result);
        Assert.IsType<HoldingPatternCommand>(result);
    }

    // --- SAY with commas ---

    [Fact]
    public void ParseCompound_SayWithComma_DoesNotSplit()
    {
        var result = CommandParser.ParseCompound("SAY hello, world", Fixes);

        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.Single(result.Blocks[0].Commands);
        var say = Assert.IsType<SayCommand>(result.Blocks[0].Commands[0]);
        Assert.Equal("hello, world", say.Text);
    }

    // --- APREQ → SAY ---

    [Fact]
    public void Parse_Apreq_ReturnsSayCommand()
    {
        var result = CommandParser.Parse("APREQ RUNWAY 33");
        Assert.NotNull(result);
        var say = Assert.IsType<SayCommand>(result);
        Assert.Equal("APREQ RUNWAY 33", say.Text);
    }

    // --- Bare CAPP/JFAC ---

    [Fact]
    public void Parse_BareCapp_ReturnsNullApproachId()
    {
        var result = CommandParser.Parse("CAPP", Fixes);
        Assert.NotNull(result);
        var capp = Assert.IsType<ClearedApproachCommand>(result);
        Assert.Null(capp.ApproachId);
    }

    [Fact]
    public void Parse_BareJfac_ReturnsNullApproachId()
    {
        var result = CommandParser.Parse("JFAC", Fixes);
        Assert.NotNull(result);
        var jfac = Assert.IsType<JoinFinalApproachCourseCommand>(result);
        Assert.Null(jfac.ApproachId);
    }

    // --- TAXI $ prefix ---

    [Fact]
    public void Parse_TaxiDollarGate_ParsesAsParking()
    {
        var result = CommandParser.Parse("TAXI Y $10");
        Assert.NotNull(result);
        var taxi = Assert.IsType<TaxiCommand>(result);
        Assert.Equal("10", taxi.DestinationParking);
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
        var result = CommandParser.Parse(input, Fixes);
        Assert.NotNull(result);
        Assert.IsType(expectedType, result);
    }

    // --- GW as compound condition ---

    [Fact]
    public void ParseCompound_GW_AsCondition()
    {
        var result = CommandParser.ParseCompound("GW UAL123 SPD 210", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.NotNull(result.Blocks[0].Condition);
    }

    // --- CTOMRT/CTOMLT ---

    [Fact]
    public void Parse_Ctomrt()
    {
        var result = CommandParser.Parse("CTOMRT", Fixes);
        Assert.NotNull(result);
        Assert.IsType<ClearedForTakeoffCommand>(result);
    }

    [Fact]
    public void Parse_Ctomlt()
    {
        var result = CommandParser.Parse("CTOMLT", Fixes);
        Assert.NotNull(result);
        Assert.IsType<ClearedForTakeoffCommand>(result);
    }

    // --- TRACK with optional TCP ---

    [Fact]
    public void Parse_TrackWithTcp_ReturnsTcpCode()
    {
        var result = CommandParser.Parse("TRACK OAK_41_CTR", Fixes);
        Assert.NotNull(result);
        var track = Assert.IsType<TrackAircraftCommand>(result);
        Assert.Equal("OAK_41_CTR", track.TcpCode);
    }

    [Fact]
    public void Parse_TrackBare_ReturnsNullTcpCode()
    {
        var result = CommandParser.Parse("TRACK", Fixes);
        Assert.NotNull(result);
        var track = Assert.IsType<TrackAircraftCommand>(result);
        Assert.Null(track.TcpCode);
    }

    // --- ACCEPT with optional callsign ---

    [Fact]
    public void Parse_AcceptWithCallsign_ReturnsCallsign()
    {
        var result = CommandParser.Parse("ACCEPT JBU33", Fixes);
        Assert.NotNull(result);
        var accept = Assert.IsType<AcceptHandoffCommand>(result);
        Assert.Equal("JBU33", accept.Callsign);
    }

    // --- SP alias for SP1 ---

    [Fact]
    public void Parse_SP_Alias_ReturnsScratchpad1()
    {
        var result = CommandParser.Parse("SP OA1", Fixes);
        Assert.NotNull(result);
        var sp = Assert.IsType<Scratchpad1Command>(result);
        Assert.Equal("OA1", sp.Text);
    }

    // --- AT + track/scratchpad commands ---

    [Fact]
    public void ParseCompound_AT_Track_WithTcp()
    {
        var result = CommandParser.ParseCompound("AT OAK TRACK OAK_41_CTR", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.IsType<AtFixCondition>(result.Blocks[0].Condition);
        Assert.Single(result.Blocks[0].Commands);
        var track = Assert.IsType<TrackAircraftCommand>(result.Blocks[0].Commands[0]);
        Assert.Equal("OAK_41_CTR", track.TcpCode);
    }

    [Fact]
    public void ParseCompound_AT_SP_Scratchpad()
    {
        var result = CommandParser.ParseCompound("AT ARCHI SP +RGT", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.IsType<AtFixCondition>(result.Blocks[0].Condition);
        Assert.Single(result.Blocks[0].Commands);
        Assert.IsType<Scratchpad1Command>(result.Blocks[0].Commands[0]);
    }

    // --- TG with optional runway ---

    [Fact]
    public void Parse_TG_WithRunway()
    {
        var result = CommandParser.Parse("TG 31", Fixes);
        Assert.NotNull(result);
        var tg = Assert.IsType<TouchAndGoCommand>(result);
        Assert.Equal("31", tg.RunwayId);
    }

    [Fact]
    public void Parse_TG_Bare()
    {
        var result = CommandParser.Parse("TG", Fixes);
        Assert.NotNull(result);
        var tg = Assert.IsType<TouchAndGoCommand>(result);
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
        var result = CommandParser.Parse("HOLD VPBCK 080 10", Fixes);
        Assert.NotNull(result);
        var hold = Assert.IsType<HoldingPatternCommand>(result);
        Assert.Equal("VPBCK", hold.FixName);
        Assert.Equal(80, hold.InboundCourse);
        Assert.Equal(10, hold.LegLength);
        Assert.Equal(TurnDirection.Right, hold.Direction);
    }

    [Fact]
    public void Parse_Hold_3Tokens_Direction_DefaultsLeg1M()
    {
        // HOLD RBL 341 RIGHT → fix=RBL, course=341, direction=Right, leg=1M (default)
        var result = CommandParser.Parse("HOLD RBL 341 RIGHT", Fixes);
        Assert.NotNull(result);
        var hold = Assert.IsType<HoldingPatternCommand>(result);
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
        var result = CommandParser.Parse("HOLD SUNOL 090 10 RIGHT", Fixes);
        Assert.NotNull(result);
        var hold = Assert.IsType<HoldingPatternCommand>(result);
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
        var result = CommandParser.ParseCompound("AT BRIXX", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.IsType<AtFixCondition>(result.Blocks[0].Condition);
        Assert.Empty(result.Blocks[0].Commands);
    }

    // --- WAIT N HOLD (combination of WAIT expansion + HOLD 3-token) ---

    [Fact]
    public void ParseCompound_Wait_Hold_3Tokens()
    {
        // WAIT 150 HOLD RBL 341 RIGHT → WAIT 150; HOLD RBL 341 RIGHT
        var result = CommandParser.ParseCompound("WAIT 150 HOLD RBL 341 RIGHT", Fixes);
        Assert.NotNull(result);
        Assert.Equal(2, result.Blocks.Count);
        Assert.IsType<WaitCommand>(result.Blocks[0].Commands[0]);
        Assert.IsType<HoldingPatternCommand>(result.Blocks[1].Commands[0]);
    }

    // --- PO bare (point out with no TCP) ---

    [Fact]
    public void Parse_PO_Bare_ReturnsNullTcpCode()
    {
        var result = CommandParser.Parse("PO", Fixes);
        Assert.NotNull(result);
        var po = Assert.IsType<PointOutCommand>(result);
        Assert.Null(po.TcpCode);
    }

    [Fact]
    public void ParseCompound_AT_PO_Bare()
    {
        var result = CommandParser.ParseCompound("AT BESSA PO", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.IsType<AtFixCondition>(result.Blocks[0].Condition);
        Assert.Single(result.Blocks[0].Commands);
        Assert.IsType<PointOutCommand>(result.Blocks[0].Commands[0]);
    }

    /// <summary>
    /// Permissive fix lookup that returns a dummy position for any fix name.
    /// </summary>
    private sealed class PermissiveFixes : IFixLookup
    {
        public (double Lat, double Lon)? GetFixPosition(string fixName) => (37.0, -122.0);

        public double? GetAirportElevation(string code) => null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}
