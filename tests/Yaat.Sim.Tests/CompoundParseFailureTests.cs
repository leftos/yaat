using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class CompoundParseFailureTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private static readonly NavigationDatabase NavDb = TestNavDbFactory.WithFixNames("KLIDE", "BRIXX");

    public CompoundParseFailureTests()
    {
        NavigationDatabase.SetInstance(NavDb);
    }

    [Theory]
    [InlineData("AT KLIDE CMD", "CMD")]
    [InlineData("AT 5000 CMD", "CMD")]
    [InlineData("LV 5000 CMD", "CMD")]
    [InlineData("ATFN 5.0 CMD", "CMD")]
    [InlineData("AT BRIXX XYZ", "XYZ")]
    public void ParseCompound_InvalidCommandAfterCondition_ReportsCorrectVerb(string input, string expectedVerb)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme, out var failure);

        Assert.Null(result);
        Assert.NotNull(failure);
        Assert.Equal(expectedVerb, failure.Verb);
    }

    [Theory]
    [InlineData("AT KLIDE CM 280", "AT KLIDE CM 280")]
    [InlineData("AT 5000 CM 280", "AT 5000 CM 280")]
    [InlineData("LV 5000 FH 090", "LV 5000 FH 090")]
    public void ParseCompound_ValidCommandAfterCondition_StillParses(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal(expected, result.CanonicalString);
    }
}
