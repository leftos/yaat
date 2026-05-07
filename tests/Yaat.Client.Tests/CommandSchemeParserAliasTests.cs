using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandSchemeParserAliasTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    [Fact]
    public void SayForceAlias_WithComma_PreservesLiteralText()
    {
        var result = CommandSchemeParser.ParseCompound("SAYF HELLO, WORLD", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SAY HELLO, WORLD", result.CanonicalString);
    }
}
