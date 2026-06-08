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
    public void Relative_NoContext_MapsToCanonical(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, MapContext.Empty);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    [InlineData("traffic on a two mile right base for runway two eight right a cessna", "RTIS BASE R 2 28R cessna")]
    [InlineData("traffic on a three mile left downwind for runway two eight right a piper", "RTIS DW L 3 28R piper")]
    [InlineData("traffic on a two mile final for runway two eight right a cessna", "RTIS FINAL 2 28R cessna")]
    public void Pattern_NoContext_MapsToCanonical(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, MapContext.Empty);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void Landmark_ResolvesFixViaPronunciationPrePass()
    {
        var context = new MapContext([], []) { CustomFixPatterns = NavigationDatabase.Instance.CustomFixSpeechPatterns };

        var result = PhraseologyMapper.Map("traffic over the oakland coliseum a cessna", context);

        Assert.NotNull(result);
        Assert.Equal("RTIS OVER VPCOL cessna", result!.CanonicalCommand);
    }
}
