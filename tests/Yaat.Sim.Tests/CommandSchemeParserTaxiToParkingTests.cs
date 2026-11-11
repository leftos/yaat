using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// The canonical text the client sends, echoes and logs expands a <c>TTP</c> route to the <c>TAXI … @GATE</c> the server runs,
/// alone and inside a compound; a route the server refuses keeps its <c>TTP</c> text so the refusal names the typed command.
/// </summary>
public class CommandSchemeParserTaxiToParkingTests
{
    [Theory]
    [InlineData("TTP A B HS 28L", "TAXI A B @GATE HS 28L")]
    [InlineData("ttp a b", "TAXI A B @GATE")]
    [InlineData("TTP A B CROSS 28L EXP", "TAXI A B @GATE CROSS 28L EXP")]
    public void CanonicalText_ExpandsTheRouteToTaxiAtGate(string input, string expected)
    {
        CompoundParseResult parsed = CommandSchemeParser.ParseCompound(input, CommandScheme.Default(), out ParseFailure? failure)!;

        Assert.Null(failure);
        Assert.Equal(expected, parsed.CanonicalString);
    }

    [Fact]
    public void CanonicalText_Compound_ExpandsTheTtpBlock()
    {
        CompoundParseResult parsed = CommandSchemeParser.ParseCompound("TTP A B; HOLD", CommandScheme.Default(), out _)!;

        Assert.Equal("TAXI A B @GATE; HOLD", parsed.CanonicalString);
    }

    [Fact]
    public void CanonicalText_RefusedRoute_KeepsTheTypedTtp()
    {
        CompoundParseResult parsed = CommandSchemeParser.ParseCompound("TTP A @B12", CommandScheme.Default(), out _)!;

        Assert.Equal("TTP A @B12", parsed.CanonicalString);
    }
}
