using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Speech;

/// <summary>
/// Spoken VFR-style traffic advisories map to the canonical RTIS descriptive forms. Controller
/// phraseology only (the pilot AI never speaks these — the rules are SttOnly). Landmark references
/// collapse to a fix identifier via the FixPronunciations pre-pass before rule matching.
/// </summary>
public sealed class VfrTrafficAdvisorySttTests
{
    public VfrTrafficAdvisorySttTests() => TestVnasData.EnsureInitialized();

    [Theory]
    [InlineData("traffic off your nose and to the right two miles a cessna", "RTIS NR 2 cessna")]
    [InlineData("traffic off your nose and to the left two miles a cessna", "RTIS NL 2 cessna")]
    [InlineData("traffic off your nose one mile a skyhawk", "RTIS NOSE 1 skyhawk")]
    [InlineData("traffic off your right three miles a mooney", "RTIS R 3 mooney")]
    [InlineData("traffic off your left three miles a skyhawk", "RTIS L 3 skyhawk")]
    [InlineData("traffic off your right and behind you four miles a cessna", "RTIS RR 4 cessna")]
    [InlineData("traffic off your right slightly behind four miles a cessna", "RTIS RR 4 cessna")]
    [InlineData("traffic off your left slightly behind two miles a piper", "RTIS LR 2 piper")]
    [InlineData("traffic off your tail one mile a mooney", "RTIS TAIL 1 mooney")]
    [InlineData("traffic behind you two miles a cessna", "RTIS TAIL 2 cessna")]
    // Whisper hears "…miles a Boeing" as "…miles of boeing" / "to boeing"; the stray word is absorbed.
    [InlineData("traffic off your left four miles of boeing", "RTIS L 4 boeing")]
    [InlineData("traffic off your nose and to the right two miles to cessna", "RTIS NR 2 cessna")]
    [InlineData("traffic off your right and behind you three miles of cirrus", "RTIS RR 3 cirrus")]
    [InlineData("traffic off your tail one mile to airbus", "RTIS TAIL 1 airbus")]
    public void Relative_NoContext_MapsToCanonical(string transcript, string expected)
    {
        MapResult? result = PhraseologyMapper.Map(transcript, MapContext.Empty);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    [InlineData("traffic on a two mile right base for runway two eight right a cessna", "RTIS BASE R 2 28R cessna")]
    [InlineData("traffic on a three mile left downwind for runway two eight right a piper", "RTIS DW L 3 28R piper")]
    [InlineData("traffic on a two mile final for runway two eight right a cessna", "RTIS FINAL 2 28R cessna")]
    // synth-20260928-061-rtis-dw: Whisper heard "…for runway one five to boeing".
    [InlineData("traffic on a two mile left downwind for runway one five to boeing", "RTIS DW L 2 15 boeing")]
    [InlineData("traffic on a three mile final for runway two eight right of cessna", "RTIS FINAL 3 28R cessna")]
    public void Pattern_NoContext_MapsToCanonical(string transcript, string expected)
    {
        MapResult? result = PhraseologyMapper.Map(transcript, MapContext.Empty);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void Type_Absorb_NonExamples()
    {
        // No type spoken: nothing to report, and never a type of OF / TO.
        string? noType = PhraseologyMapper.Map("traffic off your left four miles", MapContext.Empty)?.CanonicalCommand;
        Assert.DoesNotMatch(@"\b(of|to|OF|TO)$", noType ?? string.Empty);

        // A stray word with no type after it is still taken as the type: {type} accepts any token.
        // Pinned as today's behaviour, not as the desired one.
        Assert.Equal("RTIS L 4 of", PhraseologyMapper.Map("traffic off your left four miles of", MapContext.Empty)?.CanonicalCommand);
        Assert.Equal("RTIS L 4 to", PhraseologyMapper.Map("traffic off your left four miles to", MapContext.Empty)?.CanonicalCommand);

        // "traffic behind" already matched its own TAIL rule (as "RTIS TAIL 3 of"); the absorbed "of"
        // gives it the type, and no other TAIL rule takes it.
        (MapResult? behind, RuleMapperTrace trace) = PhraseologyMapper.MapWithTrace("traffic behind three miles of cessna", MapContext.Empty);
        Assert.Equal("RTIS TAIL 3 cessna", behind?.CanonicalCommand);
        Assert.StartsWith("traffic behind {miles}", Assert.Single(trace.MatchedRulePatterns));
    }

    [Fact]
    public void Landmark_ResolvesFixViaPronunciationPrePass()
    {
        var context = new MapContext([], []) { CustomFixPatterns = NavigationDatabase.Instance.CustomFixSpeechPatterns };

        MapResult? result = PhraseologyMapper.Map("traffic over the oakland coliseum a cessna", context);

        Assert.NotNull(result);
        Assert.Equal("RTIS OVER VPCOL cessna", result!.CanonicalCommand);
    }
}
