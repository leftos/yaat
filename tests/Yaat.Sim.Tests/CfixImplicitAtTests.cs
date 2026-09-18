using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// When CFIX is the first command in a compound, subsequent blocks without an explicit
/// condition should get an implicit AT {fixname} prefix.
/// </summary>
public class CfixImplicitAtTests : IDisposable
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private readonly IDisposable _navDbScope;

    public CfixImplicitAtTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(NavigationDatabase.Instance!);
    }

    public void Dispose() => _navDbScope.Dispose();

    [Fact]
    public void CfixWithSubsequentCommand_InjectsAtCondition()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CFIX CEPIN 3000 210; CAPP 28R", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R", result.CanonicalString);
    }

    [Fact]
    public void CfixWithMultipleSubsequentBlocks_InjectsAtOnAll()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CFIX CEPIN 3000 210; CAPP 28R; SPD 180", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R; AT CEPIN SPD 180", result.CanonicalString);
    }

    [Fact]
    public void CfixWithParallelSubsequentCommands_InjectsAtOnBlock()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CFIX CEPIN 3000 210; CAPP 28R, SPD 180", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R, SPD 180", result.CanonicalString);
    }

    [Fact]
    public void CfixWithExplicitAtCondition_DoesNotDoubleInject()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R", result.CanonicalString);
    }

    [Fact]
    public void CfixWithMixedConditionAndBare_InjectsOnlyOnBare()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R; SPD 180", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN 3000 210; AT CEPIN CAPP 28R; AT CEPIN SPD 180", result.CanonicalString);
    }

    [Fact]
    public void CfixAlone_NoChange()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CFIX CEPIN 3000 210", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN 3000 210", result.CanonicalString);
    }

    [Fact]
    public void NonCfixCompound_NoInjection()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("FH 270; SPD 210", Scheme);

        Assert.NotNull(result);
        Assert.Equal("FH 270; SPD 210", result.CanonicalString);
    }
}
