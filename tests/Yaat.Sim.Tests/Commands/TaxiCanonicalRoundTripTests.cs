using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="CommandDescriber.DescribeCommand"/> for a TAXI must carry the full clearance —
/// destination runway, CROSS pre-clearances, HS targets (including located <c>C@J</c> forms),
/// and NODEL — in an order that round-trips through <c>ParseTaxiTokens</c>. It used to emit only
/// the path and parking/spot, silently dropping the rest.
/// </summary>
public class TaxiCanonicalRoundTripTests
{
    private static TaxiCommand Parse(string input)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, $"'{input}' failed to parse: {parsed.Reason}");
        return Assert.IsType<TaxiCommand>(parsed.Value);
    }

    [Theory]
    [InlineData("TAXI A B", "TAXI A B")]
    [InlineData("TAXI A B RWY 28R", "TAXI A B RWY 28R")]
    [InlineData("TAXI T U W 30", "TAXI T U W RWY 30")]
    [InlineData("TAXI 1L", "TAXI RWY 1L")]
    [InlineData("TAXI RWY 1L", "TAXI RWY 1L")]
    [InlineData("TAXI A B CROSS 10L RWY 28R", "TAXI A B RWY 28R CROSS 10L")]
    [InlineData("TAXI T6A A F CROSS 1L 1R RWY 28L", "TAXI T6A A F RWY 28L CROSS 1L 1R")]
    [InlineData("TAXI A B HS C", "TAXI A B HS C")]
    [InlineData("TAXI C D J HS C@J", "TAXI C D J HS C@J")]
    [InlineData("TAXI S T U HS 28L RWY 30", "TAXI S T U RWY 30 HS 28L")]
    [InlineData("TAXI S T U @B12 NODEL", "TAXI S T U @B12 NODEL")]
    [InlineData("TAXI TE $7A", "TAXI TE $7A")]
    [InlineData("TAXI T421 C Z B M1 1L HS $17", "TAXI T421 C Z B M1 RWY 1L HS $17")]
    [InlineData("TAXI K $8 HS $8", "TAXI K $8 HS $8")]
    [InlineData("TAXI >A B <C D", "TAXI >A B <C D")]
    public void Canonical_CarriesFullClearance(string input, string expectedCanonical)
    {
        Assert.Equal(expectedCanonical, CommandDescriber.DescribeCommand(Parse(input)));
    }

    [Theory]
    [InlineData("TAXI A B RWY 28R")]
    [InlineData("TAXI RWY 1L")]
    [InlineData("TAXI A B CROSS 10L RWY 28R")]
    [InlineData("TAXI C D J HS C@J")]
    [InlineData("TAXI S T U HS 28L RWY 30")]
    [InlineData("TAXI S T U @B12 NODEL")]
    [InlineData("TAXI >A B <C D RWY 28R CROSS 10L HS E")]
    [InlineData("TAXI T421 C Z B M1 1L HS $17")]
    [InlineData("TAXI K $8 HS $8")]
    public void Canonical_RoundTripsThroughParser(string input)
    {
        string canonical = CommandDescriber.DescribeCommand(Parse(input));
        var reparsed = Parse(canonical);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(reparsed));

        var original = Parse(input);
        Assert.Equal(original.Path, reparsed.Path);
        Assert.Equal(original.HoldShorts, reparsed.HoldShorts);
        Assert.Equal(original.DestinationRunway, reparsed.DestinationRunway);
        Assert.Equal(original.CrossRunways ?? [], reparsed.CrossRunways ?? []);
        Assert.Equal(original.DestinationParking, reparsed.DestinationParking);
        Assert.Equal(original.DestinationSpot, reparsed.DestinationSpot);
        Assert.Equal(original.NoDelete, reparsed.NoDelete);
        Assert.Equal(original.PathTurnHints ?? [], reparsed.PathTurnHints ?? []);
    }

    [Theory]
    [InlineData("TAXI A B RWY 28R", "Taxi via A B to runway 28R")]
    [InlineData("TAXI 1L", "Taxi to runway 1L")]
    [InlineData("TAXI A B CROSS 10L RWY 28R", "Taxi via A B to runway 28R, cross 10L")]
    [InlineData("TAXI C D J HS C@J", "Taxi via C D J, hold short of C at J")]
    [InlineData("TAXI S T U @B12", "Taxi via S T U to parking B12")]
    [InlineData("TAXI C Z HS $17", "Taxi via C Z, hold short of spot 17")]
    public void Natural_CarriesFullClearance(string input, string expectedNatural)
    {
        Assert.Equal(expectedNatural, CommandDescriber.DescribeNatural(Parse(input)));
    }
}
