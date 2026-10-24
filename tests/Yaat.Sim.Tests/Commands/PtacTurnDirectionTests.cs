using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A PTAC's heading can carry the turn direction the controller gives (7110.65 5-6-2.a.1 "TURN LEFT/RIGHT HEADING"), glued
/// before the heading: <c>PTAC L240 030 ILS28R</c>. With no direction the heading is flown the shorter way; a direction has no
/// meaning with <c>PH</c>, which issues no new vector, so that form is refused.
/// </summary>
public class PtacTurnDirectionTests
{
    private static PositionTurnAltitudeClearanceCommand Parse(string text)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(text);
        Assert.True(result.IsSuccess, result.Reason);
        return Assert.IsType<PositionTurnAltitudeClearanceCommand>(result.Value);
    }

    [Fact]
    public void Ptac_WithLeftPrefix_ParsesLeftTurn()
    {
        PositionTurnAltitudeClearanceCommand cmd = Parse("PTAC L240 030 ILS28R");

        Assert.Equal(TurnDirection.Left, cmd.Direction);
        Assert.Equal(240, cmd.MagneticHeading?.Degrees);
        Assert.Equal(3000, cmd.Altitude);
        Assert.Equal("ILS28R", cmd.ApproachId);
        Assert.False(cmd.Forced);
    }

    [Fact]
    public void Ptacf_WithRightPrefix_ParsesRightTurnForced()
    {
        PositionTurnAltitudeClearanceCommand cmd = Parse("PTACF R180 040 RNAVZ28R");

        Assert.Equal(TurnDirection.Right, cmd.Direction);
        Assert.Equal(180, cmd.MagneticHeading?.Degrees);
        Assert.Equal(4000, cmd.Altitude);
        Assert.Equal("RNAVZ28R", cmd.ApproachId);
        Assert.True(cmd.Forced);
    }

    [Fact]
    public void Ptac_LowerCasePrefix_ParsesTheTurn()
    {
        PositionTurnAltitudeClearanceCommand cmd = Parse("PTAC r090 030 ILS28R");

        Assert.Equal(TurnDirection.Right, cmd.Direction);
        Assert.Equal(90, cmd.MagneticHeading?.Degrees);
    }

    [Fact]
    public void Ptac_WithoutPrefix_HasNoDirection()
    {
        PositionTurnAltitudeClearanceCommand cmd = Parse("PTAC 240 030 ILS28R");

        Assert.Null(cmd.Direction);
        Assert.Equal(240, cmd.MagneticHeading?.Degrees);
    }

    [Fact]
    public void Ptac_PresentHeading_HasNoDirection()
    {
        PositionTurnAltitudeClearanceCommand cmd = Parse("PTAC PH 030 ILS28R");

        Assert.Null(cmd.Direction);
        Assert.Null(cmd.MagneticHeading);
    }

    [Fact]
    public void Ptac_DirectionWithPresentHeading_IsRefused()
    {
        ParseResult<ParsedCommand> result = ApproachCommandParser.ParsePtac("LPH 030 ILS28R", forced: false);

        Assert.False(result.IsSuccess);
        Assert.Equal("PTAC turn direction needs a heading, not PH", result.Reason);
    }

    /// <summary>
    /// The canonical text describes queued blocks and matches re-issued ones, so it must keep the turn direction through a
    /// re-parse.
    /// </summary>
    [Theory]
    [InlineData("PTAC L240 030 ILS28R", "PTAC L240 3000 ILS28R")]
    [InlineData("PTAC L360 030 ILS28R", "PTAC L360 3000 ILS28R")]
    [InlineData("PTAC 360 030 ILS28R", "PTAC 360 3000 ILS28R")]
    [InlineData("PTACF R180 040 RNAVZ28R", "PTACF R180 4000 RNAVZ28R")]
    [InlineData("PTAC 240 030 ILS28R", "PTAC 240 3000 ILS28R")]
    public void Canonical_ReParsesToTheSameCommand(string input, string expectedCanonical)
    {
        PositionTurnAltitudeClearanceCommand first = Parse(input);
        string canonical = CommandDescriber.DescribeCommand(first);

        Assert.Equal(expectedCanonical, canonical);
        Assert.Equal(first, Parse(canonical));
    }

    [Theory]
    [InlineData("PTAC L240 030 ILS28R", "Turn left heading 240, maintain 3,000, cleared ILS28R approach")]
    [InlineData("PTAC R240 030 ILS28R", "Turn right heading 240, maintain 3,000, cleared ILS28R approach")]
    [InlineData("PTAC 240 030 ILS28R", "Fly heading 240, maintain 3,000, cleared ILS28R approach")]
    public void Natural_SaysTheTurnDirectionWhenGiven(string input, string expected) =>
        Assert.Equal(expected, CommandDescriber.DescribeNatural(Parse(input)));

    [Theory]
    [InlineData("L0")]
    [InlineData("R361")]
    [InlineData("L")]
    [InlineData("X240")]
    [InlineData("LL240")]
    [InlineData("L+240")]
    [InlineData("+240")]
    public void Ptac_InvalidHeading_NamesTheAcceptedForms(string token)
    {
        ParseResult<ParsedCommand> result = ApproachCommandParser.ParsePtac($"{token} 030 ILS28R", forced: false);

        Assert.False(result.IsSuccess);
        Assert.Equal($"invalid PTAC heading '{token}' (expected 1-360, L or R plus 1-360, or PH)", result.Reason);
    }
}
