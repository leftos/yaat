using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class SpeedParserTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    // --- SPD modifier passthrough ---

    [Fact]
    public void SpeedFloor_ParsedAsSpeedWithPlusSuffix()
    {
        var result = CommandSchemeParser.Parse("SPD 210+", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.Speed, result.Type);
        Assert.Equal("210+", result.Argument);
    }

    [Fact]
    public void SpeedCeiling_ParsedAsSpeedWithMinusSuffix()
    {
        var result = CommandSchemeParser.Parse("SPD 210-", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.Speed, result.Type);
        Assert.Equal("210-", result.Argument);
    }

    // --- RNS / NS ---

    [Fact]
    public void Rns_Parsed()
    {
        var result = CommandSchemeParser.Parse("RNS", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.ResumeNormalSpeed, result.Type);
        Assert.Null(result.Argument);
    }

    [Fact]
    public void Ns_Parsed()
    {
        var result = CommandSchemeParser.Parse("NS", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.ResumeNormalSpeed, result.Type);
    }

    // --- DSR ---

    [Fact]
    public void Dsr_Parsed()
    {
        var result = CommandSchemeParser.Parse("DSR", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.DeleteSpeedRestrictions, result.Type);
        Assert.Null(result.Argument);
    }

    // --- ATFN compound ---

    [Fact]
    public void AtfnCompound_ParsedCorrectly()
    {
        var result = CommandSchemeParser.ParseCompound("ATFN 10 SPD 180", Scheme);

        Assert.NotNull(result);
        Assert.Equal("ATFN 10 SPD 180", result.CanonicalString);
    }

    [Fact]
    public void SpeedWithAtfnChain_ParsedCorrectly()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 210; ATFN 10 SPD 180", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 210; ATFN 10 SPD 180", result.CanonicalString);
    }

    // --- SPD X UNTIL Y ---

    [Fact]
    public void SpeedUntil_ExpandedToCompound()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 210 UNTIL 10", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 210; ATFN 10 RNS", result.CanonicalString);
    }

    [Fact]
    public void SpeedUntilChained_ExpandedCorrectly()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 210 UNTIL 10; SPD 180 UNTIL 5", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 210; ATFN 10 SPD 180; ATFN 5 RNS", result.CanonicalString);
    }

    [Fact]
    public void SpeedFloorUntil_ExpandedCorrectly()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 210+ UNTIL 10", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 210+; ATFN 10 RNS", result.CanonicalString);
    }

    // --- SPD X UNTIL <fix> ---

    [Fact]
    public void SpeedUntilFix_ExpandedToAtBlock()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 180 UNTIL AXMUL", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 180; AT AXMUL RNS", result.CanonicalString);
    }

    [Fact]
    public void SpeedFixAlias_ExpandedToAtBlock()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 180 AXMUL", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 180; AT AXMUL RNS", result.CanonicalString);
    }

    [Fact]
    public void SpeedUntilDistance_StillUsesAtfn()
    {
        var result = CommandSchemeParser.ParseCompound("SPD 210 UNTIL 10", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPD 210; ATFN 10 RNS", result.CanonicalString);
    }

    [Fact]
    public void ExpandSpeedUntil_FixBased()
    {
        var expanded = CommandSchemeParser.ExpandSpeedUntil("SPD 180 UNTIL AXMUL");
        Assert.Equal("SPD 180; AT AXMUL RNS", expanded);
    }

    [Fact]
    public void ExpandSpeedUntil_FixAlias()
    {
        var expanded = CommandSchemeParser.ExpandSpeedUntil("SPD 180 AXMUL");
        Assert.Equal("SPD 180; AT AXMUL RNS", expanded);
    }

    [Fact]
    public void ExpandSpeedUntil_DistanceBased_Preserved()
    {
        var expanded = CommandSchemeParser.ExpandSpeedUntil("SPD 210 UNTIL 10");
        Assert.Equal("SPD 210; ATFN 10 RNS", expanded);
    }

    // --- AT block with SPD UNTIL fix remainder ---

    [Fact]
    public void AtCondition_SpeedFixAlias_ParsesAsCompound()
    {
        var result = CommandSchemeParser.ParseCompound("AT CEPIN SPD 180 AXMUL", Scheme);

        Assert.NotNull(result);
        Assert.Equal("AT CEPIN SPD 180; AT AXMUL RNS", result.CanonicalString);
    }

    // --- Canonical output ---

    [Fact]
    public void ToCanonical_ResumeNormalSpeed()
    {
        var canonical = CommandSchemeParser.ToCanonical(CanonicalCommandType.ResumeNormalSpeed, null);
        Assert.Equal("RNS", canonical);
    }

    [Fact]
    public void ToCanonical_DeleteSpeedRestrictions()
    {
        var canonical = CommandSchemeParser.ToCanonical(CanonicalCommandType.DeleteSpeedRestrictions, null);
        Assert.Equal("DSR", canonical);
    }
}
