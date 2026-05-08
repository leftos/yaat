using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class CustomFixCollapseTests
{
    public CustomFixCollapseTests()
    {
        // PhraseologyMapper now validates rule outputs via CommandParser.Parse, which resolves
        // DCT fix args through NavigationDatabase. Load real nav data (including the OAK30NUM
        // custom fix from Data/CustomFixes/ZOA) so the validator accepts the canonical.
        TestVnasData.EnsureInitialized();
    }

    // Custom-fix patterns. OAK30NUM exercises the canonical motivating case; the second alias
    // (TOLLPLAZA) is local-only — kept so longest-match disambiguation across multiple
    // canonicals stays exercised — and is NOT round-tripped through PhraseologyMapper.Map
    // (which validates against the live NavigationDatabase) since TOLLPLAZA isn't a registered
    // fix.
    private static readonly IReadOnlyList<CustomFixSpeechPattern> Patterns =
    [
        new(["the", "oakland", "runway", "30", "numbers"], "OAK30NUM"),
        new(["the", "runway", "30", "numbers"], "OAK30NUM"),
        new(["runway", "30", "numbers"], "OAK30NUM"),
        new(["oakland", "runway", "30", "numbers"], "OAK30NUM"),
        new(["30", "numbers"], "OAK30NUM"),
        new(["san", "mateo", "bridge", "toll", "plaza"], "TOLLPLAZA"),
        new(["the", "toll", "plaza"], "TOLLPLAZA"),
        new(["toll", "plaza"], "TOLLPLAZA"),
    ];

    private static MapContext ContextWithPatterns => new([], []) { CustomFixPatterns = Patterns };

    [Theory]
    [InlineData("direct to the runway three zero numbers", "DCT OAK30NUM")]
    [InlineData("direct to runway three zero numbers", "DCT OAK30NUM")]
    [InlineData("proceed direct the oakland runway three zero numbers", "DCT OAK30NUM")]
    [InlineData("direct three zero numbers", "DCT OAK30NUM")]
    public void DirectTo_CustomFixPhrase_MapsToCanonical(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, ContextWithPatterns);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void CustomFixCollapse_LongestPatternWins()
    {
        // "the runway 30 numbers" should match the 4-token pattern, not two separate matches of
        // the shorter "runway 30 numbers" + stray "the".
        var tokens = new List<string> { "the", "runway", "30", "numbers" };
        var collapsed = PhraseologyMapper.CollapseCustomFixNames(tokens, Patterns);
        Assert.Equal(new[] { "OAK30NUM" }, collapsed);
    }

    [Fact]
    public void CustomFixCollapse_NoMatch_PassesThrough()
    {
        var tokens = new List<string> { "climb", "and", "maintain", "5000" };
        var collapsed = PhraseologyMapper.CollapseCustomFixNames(tokens, Patterns);
        Assert.Equal(new[] { "climb", "and", "maintain", "5000" }, collapsed);
    }

    [Fact]
    public void CustomFixCollapse_EmptyPatterns_IsNoop()
    {
        var tokens = new List<string> { "direct", "to", "cepin" };
        var collapsed = PhraseologyMapper.CollapseCustomFixNames(tokens, []);
        Assert.Equal(new[] { "direct", "to", "cepin" }, collapsed);
    }

    [Fact]
    public void CustomFixCollapse_MultipleMatches_InOneTranscript()
    {
        // Compound command: "direct to the runway 30 numbers, then direct to the toll plaza"
        var tokens = new List<string> { "direct", "to", "the", "runway", "30", "numbers", "then", "direct", "to", "the", "toll", "plaza" };
        var collapsed = PhraseologyMapper.CollapseCustomFixNames(tokens, Patterns);
        Assert.Equal(new[] { "direct", "to", "OAK30NUM", "then", "direct", "to", "TOLLPLAZA" }, collapsed);
    }

    [Fact]
    public void CustomFixCollapse_PatternAdjacentToOtherTokens_PreservesSurroundings()
    {
        // Real compound: "climb and maintain 5000 direct to the runway 30 numbers"
        var tokens = new List<string> { "climb", "and", "maintain", "5000", "direct", "to", "the", "runway", "30", "numbers" };
        var collapsed = PhraseologyMapper.CollapseCustomFixNames(tokens, Patterns);
        Assert.Equal(new[] { "climb", "and", "maintain", "5000", "direct", "to", "OAK30NUM" }, collapsed);
    }

    [Fact]
    public void DirectTo_CustomFixCompoundWithAltitude_MapsBothClauses()
    {
        var result = PhraseologyMapper.Map("climb and maintain five thousand direct to the runway three zero numbers", ContextWithPatterns);
        Assert.NotNull(result);
        Assert.Equal("CM 5000, DCT OAK30NUM", result!.CanonicalCommand);
    }

    [Fact]
    public void DirectTo_WithoutPatterns_TreatsPhraseAsRegularFixToken()
    {
        // When custom fix patterns aren't provided, "direct to the runway 30 numbers" should
        // NOT collapse — the rule engine instead sees "direct to runway" as "DCT runway" (since
        // {fix} captures one token). This sanity-checks that custom fix handling is purely additive.
        var result = PhraseologyMapper.Map("direct to runway three zero numbers", MapContext.Empty);
        Assert.NotNull(result);
        Assert.NotEqual("DCT OAK30NUM", result!.CanonicalCommand);
    }
}
