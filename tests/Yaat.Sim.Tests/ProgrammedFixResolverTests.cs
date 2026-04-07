using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class ProgrammedFixResolverTests
{
    public ProgrammedFixResolverTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Resolve_PlainRoute_ExtractsAllFixes()
    {
        var result = ProgrammedFixResolver.Resolve("SUNOL MODESTO OXNARD", null, null, null, null, null, null);

        Assert.Contains("SUNOL", result);
        Assert.Contains("MODESTO", result);
        Assert.Contains("OXNARD", result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Resolve_DotAirwaySuffix_ExtractsFixBeforeDot()
    {
        var result = ProgrammedFixResolver.Resolve("PORTE.V25 CNDEL", null, null, null, null, null, null);

        Assert.Contains("PORTE", result);
        Assert.Contains("CNDEL", result);
        Assert.DoesNotContain("V25", result);
        Assert.DoesNotContain("PORTE.V25", result);
    }

    [Fact]
    public void Resolve_EmptyRoute_ReturnsEmpty()
    {
        var result = ProgrammedFixResolver.Resolve("", null, null, null, null, null, null);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_NullRoute_ReturnsEmpty()
    {
        var result = ProgrammedFixResolver.Resolve(null, null, null, null, null, null, null);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var result = ProgrammedFixResolver.Resolve("sunol MODESTO", null, null, null, null, null, null);

        Assert.Contains("sunol", result);
        Assert.Contains("SUNOL", result); // HashSet with OrdinalIgnoreCase
    }

    [Fact]
    public void Resolve_ActiveApproachFixes_Included()
    {
        var approachFixes = new List<string> { "GROVE", "FITKI", "BERYL" };

        var result = ProgrammedFixResolver.Resolve("SUNOL", null, null, null, approachFixes, null, null);

        Assert.Contains("SUNOL", result);
        Assert.Contains("GROVE", result);
        Assert.Contains("FITKI", result);
        Assert.Contains("BERYL", result);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Resolve_MultipleDotTokens_ExtractsAllFixes()
    {
        // PORTE.V25 CNDEL.V244 SFO — two dot-format tokens + one plain
        var result = ProgrammedFixResolver.Resolve("PORTE.V25 CNDEL.V244 SFO", null, null, null, null, null, null);

        Assert.Contains("PORTE", result);
        Assert.Contains("CNDEL", result);
        Assert.Contains("SFO", result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Resolve_DotWithNoFixName_Skips()
    {
        // Edge case: ".V25 CNDEL" — empty fix name before dot
        var result = ProgrammedFixResolver.Resolve(".V25 CNDEL", null, null, null, null, null, null);

        Assert.Contains("CNDEL", result);
        Assert.DoesNotContain("V25", result);
    }

    [Fact]
    public void Resolve_RouteWithDuplicates_DeduplicatesViaCaseInsensitiveSet()
    {
        var result = ProgrammedFixResolver.Resolve("SUNOL MODESTO SUNOL", null, null, null, null, null, null);

        Assert.Equal(2, result.Count);
        Assert.Contains("SUNOL", result);
        Assert.Contains("MODESTO", result);
    }
}
