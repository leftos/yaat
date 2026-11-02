using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class CtoParserTests : IDisposable
{
    private readonly IDisposable _scope;

    public CtoParserTests()
    {
        _scope = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void BareCto_ParsesAsDefaultDeparture()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<DefaultDeparture>(cto.Departure);
        Assert.Null(cto.AssignedAltitude);
        Assert.False(cto.CautionWakeTurbulence);
    }

    [Fact]
    public void Cto_CwtSuffix_ParsesAsDefaultDepartureWithWakeAdvisory()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO CWT");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<DefaultDeparture>(cto.Departure);
        Assert.True(cto.CautionWakeTurbulence);
        Assert.Equal("CTO CWT", CommandDescriber.DescribeCommand(cto));
        Assert.Equal("Cleared for takeoff, caution wake turbulence", CommandDescriber.DescribeNatural(cto));
    }

    [Fact]
    public void Cto_ModifierWithCwtSuffix_PreservesModifierAndAltitude()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRH 050 CWT");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
        Assert.True(cto.CautionWakeTurbulence);
        Assert.Equal("CTO MRH 5000 CWT", CommandDescriber.DescribeCommand(cto));
    }

    [Fact]
    public void Cto_DctWithCwtSuffix_PreservesFix()
    {
        _scope.Dispose();
        using IDisposable _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );

        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO DCT SUNOL CWT");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(cto.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.True(cto.CautionWakeTurbulence);
    }

    [Fact]
    public void Cto_BareNumber_IsHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO 050");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(50, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_BareNumber_WithAltitude()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO 060 250");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(60, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
        Assert.Equal(25000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_360_IsHeading360()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO 360");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        // MagneticHeading normalizes [0, 360) so 360 → 0 degrees; display int is 360
        Assert.Equal(360, fh.MagneticHeading.ToDisplayInt());
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void CtoRegistry_HasNoLiteral360Overload()
    {
        CommandDefinition cto = CommandRegistry.All[CanonicalCommandType.ClearedForTakeoff];

        Assert.DoesNotContain(cto.Overloads, o => o.Parameters.Any(p => p.IsLiteral && (p.Name == "360")));
    }

    [Fact]
    public void Cto_Mrc_RightCrosswind()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRC");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(PatternEntryLeg.Crosswind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Right, ped.Direction);
    }

    [Fact]
    public void Cto_Mrc_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRC 014");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(PatternEntryLeg.Crosswind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Right, ped.Direction);
        Assert.Equal(1400, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mrd_RightDownwind()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRD");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(PatternEntryLeg.Downwind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Right, ped.Direction);
    }

    [Fact]
    public void Cto_Mr270_ArbitraryRightTurn()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MR270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        RelativeTurnDeparture rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(270, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
    }

    [Fact]
    public void Cto_Mr45_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MR45 050");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        RelativeTurnDeparture rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(45, rel.Degrees);
        Assert.Equal(TurnDirection.Right, rel.Direction);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mlc_LeftCrosswind()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MLC");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(PatternEntryLeg.Crosswind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Left, ped.Direction);
    }

    [Fact]
    public void Cto_Mld_LeftDownwind()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MLD");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(PatternEntryLeg.Downwind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Left, ped.Direction);
    }

    [Fact]
    public void Cto_Ml270_ArbitraryLeftTurn()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO ML270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        RelativeTurnDeparture rel = Assert.IsType<RelativeTurnDeparture>(cto.Departure);
        Assert.Equal(270, rel.Degrees);
        Assert.Equal(TurnDirection.Left, rel.Direction);
    }

    [Fact]
    public void Cto_Mrh_RunwayHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRH");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Mso_RunwayHeadingAlias()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MSO");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Rh_RunwayHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO RH");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Rh_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO RH 050");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_H270_FlyHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO H270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Null(fh.Direction);
    }

    [Fact]
    public void Cto_Rh270_TurnRightHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO RH270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
    }

    [Fact]
    public void Cto_Lh270_TurnLeftHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO LH270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Cto_Lh270_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO LH270 014");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
        Assert.Equal(1400, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Rt270_TurnRightHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO RT270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
    }

    [Fact]
    public void Cto_Lt270_TurnLeftHeading()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO LT270");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(270, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Left, fh.Direction);
    }

    [Fact]
    public void Cto_Oc_OnCourse()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO OC");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_Oc_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO OC 050");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<OnCourseDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Dct_DirectFix()
    {
        _scope.Dispose();
        using IDisposable _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO DCT SUNOL");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(cto.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(37.5, dfd.Lat, 1);
        Assert.Equal(-121.8, dfd.Lon, 1);
    }

    [Fact]
    public void Cto_Dct_WithAlt()
    {
        _scope.Dispose();
        using IDisposable _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO DCT SUNOL 050");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(cto.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mrt_RightClosedTraffic()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRT");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        ClosedTrafficDeparture ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Right, ct.Direction);
    }

    [Fact]
    public void Cto_Mlt_LeftClosedTraffic()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MLT");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        ClosedTrafficDeparture ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Left, ct.Direction);
    }

    [Fact]
    public void Cto_Mrh_WithAlt()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRH 050");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
        Assert.Equal(5000, cto.AssignedAltitude);
    }

    // Cross-runway closed traffic

    [Fact]
    public void Cto_Mrt_WithRunway_ParsesRunwayId()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRT 28R");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        ClosedTrafficDeparture ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Right, ct.Direction);
        Assert.Equal("28R", ct.RunwayId);
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_Mlt_WithRunway_ParsesRunwayId()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MLT 28L");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        ClosedTrafficDeparture ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal(PatternDirection.Left, ct.Direction);
        Assert.Equal("28L", ct.RunwayId);
    }

    [Fact]
    public void Cto_Mrt_NoRunway_RunwayIdIsNull()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRT");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        ClosedTrafficDeparture ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Null(ct.RunwayId);
    }

    // Strict argument validation — unrecognized/unconsumed input must be rejected,
    // not silently downgraded to a bare "cleared for takeoff" (runway-heading) departure.

    [Fact]
    public void Cto_UnknownModifier_Fails()
    {
        // The reported bug: "TRD" is not a CTO modifier (the valid token is "TRDCT").
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO TRD OAK30NUM");
        Assert.False(cmd.IsSuccess);
        Assert.Contains("TRD", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cto_Trdct_KnownFix_TurnsRight()
    {
        _scope.Dispose();
        using IDisposable _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO TRDCT SUNOL");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        DirectFixDeparture dfd = Assert.IsType<DirectFixDeparture>(cto.Departure);
        Assert.Equal("SUNOL", dfd.FixName);
        Assert.Equal(TurnDirection.Right, dfd.Direction);
    }

    [Fact]
    public void Cto_Trdct_UnknownFix_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO TRDCT BADFIX");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Cto_Dct_NoFix_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO DCT");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Cto_Modifier_TrailingJunk_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO RH JUNK");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Cto_BareHeading_ExtraToken_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO 270 050 EXTRA");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Cto_Mrt_TrailingJunk_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO MRT JUNK");
        Assert.False(cmd.IsSuccess);
    }

    // ---- Immediate takeoff modifier (IMM / WD / ND aliases) ----

    [Fact]
    public void BareCto_IsNotImmediate()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.False(cto.Immediate);
    }

    [Theory]
    [InlineData("CTO IMM")]
    [InlineData("CTO WD")]
    [InlineData("CTO ND")]
    public void Cto_ImmediateAliases_SetImmediate(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.IsType<DefaultDeparture>(cto.Departure);
        Assert.True(cto.Immediate);
        Assert.Equal("CTO IMM", CommandDescriber.DescribeCommand(cto));
        Assert.Equal("Cleared for immediate takeoff", CommandDescriber.DescribeNatural(cto));
    }

    [Fact]
    public void Cto_ImmediateWithTurnAndAltitude_PreservesEverything()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTO RT280 050 IMM");
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(280, fh.MagneticHeading.Degrees);
        Assert.Equal(TurnDirection.Right, fh.Direction);
        Assert.Equal(5000, cto.AssignedAltitude);
        Assert.True(cto.Immediate);
        Assert.Equal("CTO RH280 5000 IMM", CommandDescriber.DescribeCommand(cto));
    }

    [Theory]
    [InlineData("CTO IMM CWT")]
    [InlineData("CTO CWT IMM")]
    public void Cto_ImmediateAndWakeTurbulence_AnyOrder_SetsBothFlags(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(cmd.Value);
        Assert.True(cto.Immediate);
        Assert.True(cto.CautionWakeTurbulence);
        Assert.Equal("CTO CWT IMM", CommandDescriber.DescribeCommand(cto));
    }

    [Fact]
    public void Cto_Immediate_CanonicalRoundTrips()
    {
        string canonical = CommandDescriber.DescribeCommand(Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO MRC 014 IMM").Value));
        ClearedForTakeoffCommand reparsed = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse(canonical).Value);
        Assert.True(reparsed.Immediate);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(reparsed.Departure);
        Assert.Equal(PatternEntryLeg.Crosswind, ped.ExitLeg);
        Assert.Equal(1400, reparsed.AssignedAltitude);
    }

    // ---- Named runway: CTO {runway}, the first argument ----

    [Fact]
    public void Cto_Runway_CarriesTheRunway()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 28L").Value);
        Assert.Equal("28L", cto.Runway);
        Assert.IsType<DefaultDeparture>(cto.Departure);
        Assert.Equal("CTO 28L", CommandDescriber.DescribeCommand(cto));
        Assert.Equal("Runway 28L, cleared for takeoff", CommandDescriber.DescribeNatural(cto));
    }

    [Fact]
    public void Cto_RunwayThenHeading_CarriesBoth()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 28 330").Value);
        Assert.Equal("28", cto.Runway);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(330, fh.MagneticHeading.Degrees);
        Assert.Null(cto.AssignedAltitude);
    }

    [Fact]
    public void Cto_RunwayThenImmediate_CarriesBoth()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 28L IMM").Value);
        Assert.Equal("28L", cto.Runway);
        Assert.True(cto.Immediate);
        Assert.IsType<DefaultDeparture>(cto.Departure);
        Assert.Equal("CTO 28L IMM", CommandDescriber.DescribeCommand(cto));
    }

    [Fact]
    public void Cto_RunwayThenModifier_CarriesBoth()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 28L MRD").Value);
        Assert.Equal("28L", cto.Runway);
        PatternExitDeparture ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(PatternEntryLeg.Downwind, ped.ExitLeg);
        Assert.Equal(PatternDirection.Right, ped.Direction);
        Assert.Equal("CTO 28L MRD", CommandDescriber.DescribeCommand(cto));
    }

    [Fact]
    public void Cto_RunwayThenBareNumber_IsTheHeading()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 28L 050").Value);
        Assert.Equal("28L", cto.Runway);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(50, fh.MagneticHeading.Degrees);
        Assert.Equal("CTO 28L H050", CommandDescriber.DescribeCommand(cto));
        Assert.Equal("Runway 28L, cleared for takeoff, fly heading 050", CommandDescriber.DescribeNatural(cto));
    }

    [Fact]
    public void Cto_ThreeDigitNumber_IsAHeading()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 330").Value);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(330, fh.MagneticHeading.Degrees);
        Assert.Null(cto.Runway);
    }

    [Fact]
    public void Cto_TwoDigitNumber_IsARunway()
    {
        // Owner ruling 2026-11-02: CTO with 1-2 digits (+ optional L/C/R) names a runway; a heading is exactly 3 digits.
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 33").Value);
        Assert.Equal("33", cto.Runway);
        Assert.IsType<DefaultDeparture>(cto.Departure);
    }

    [Fact]
    public void Cto_OneDigitRunway_IsTheSameRunwayAsItsZeroPaddedForm()
    {
        ClearedForTakeoffCommand oneDigit = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 4").Value);
        ClearedForTakeoffCommand padded = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 04").Value);
        Assert.Equal("04", oneDigit.Runway);
        Assert.Equal(padded, oneDigit);
    }

    [Fact]
    public void Cto_OneDigitRunwayWithSideThenHeading_CarriesBoth()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 4L 090").Value);
        Assert.Equal("04L", cto.Runway);
        FlyHeadingDeparture fh = Assert.IsType<FlyHeadingDeparture>(cto.Departure);
        Assert.Equal(90, fh.MagneticHeading.Degrees);
    }

    [Theory]
    [InlineData("CTO 45")]
    [InlineData("CTO 00")]
    [InlineData("CTO RWY 28L")]
    [InlineData("CTO 28L 28R")]
    [InlineData("CTO 28L 33")]
    [InlineData("CTO IMM 28L")]
    [InlineData("CTO 28L 361")]
    public void Cto_RunwayNotLeadingOrNotARunway_IsRefused(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        Assert.False(cmd.IsSuccess, $"{input} parsed as {cmd.Value}");
        Assert.Contains("CTO does not understand", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CTO 28L 330")]
    [InlineData("CTO 4L 090 050")]
    [InlineData("CTO 33 IMM")]
    [InlineData("CTO 28R MRC 014")]
    public void Cto_NamedRunway_CanonicalRoundTrips(string input)
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse(input).Value);
        string canonical = CommandDescriber.DescribeCommand(cto);
        Assert.Equal(cto, CommandParser.Parse(canonical).Value);
    }

    [Fact]
    public void Cto_ModifierThenTwoDigitNumber_IsStillTheAltitude()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO RH 28").Value);
        Assert.IsType<RunwayHeadingDeparture>(cto.Departure);
        Assert.Equal(2800, cto.AssignedAltitude);
        Assert.Null(cto.Runway);
    }

    [Fact]
    public void Cto_RunwayThenClosedTraffic_KeepsThePatternRunway()
    {
        ClearedForTakeoffCommand cto = Assert.IsType<ClearedForTakeoffCommand>(CommandParser.Parse("CTO 28L MLT 28R").Value);
        Assert.Equal("28L", cto.Runway);
        ClosedTrafficDeparture ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal("28R", ct.RunwayId);
    }

    [Theory]
    [InlineData("CTO 28L", "CTO 28L")]
    [InlineData("CTO 28L 330", "CTO 28L 330")]
    public void CtoRunway_IsNotSplitIntoTwoCommands(string input, string canonical)
    {
        Assert.DoesNotContain(",", CommandSchemeParser.ExpandMultiCommand(input));
        CompoundParseResult? scheme = CommandSchemeParser.ParseCompound(input, CommandScheme.Default());
        Assert.NotNull(scheme);
        Assert.Equal(canonical, scheme.CanonicalString);
    }
}
