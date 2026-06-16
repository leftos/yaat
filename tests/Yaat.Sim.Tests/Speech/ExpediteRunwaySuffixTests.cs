using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

/// <summary>
/// Guards against the STT normalization bug where a long word sharing a runway-suffix rime
/// ("expedite" ends in "ite", "tonight" ends in "ight") was fuzzy-matched as a runway suffix
/// after an altitude digit, deleting the word and corrupting the altitude into an impossible
/// runway ("climb and maintain five thousand expedite" → "...50R"). The fuzzy
/// <see cref="PhraseologyMapper"/> suffix matcher is bounded to short (3-6 char) tokens; genuine
/// one-syllable mishears (kite/rate/right) still collapse.
/// </summary>
public class ExpediteRunwaySuffixTests
{
    [Theory]
    [InlineData("climb and maintain five thousand expedite", "climb and maintain 5000 expedite")]
    [InlineData("descend and maintain four thousand expedite descent", "descend and maintain 4000 expedite descent")]
    [InlineData("maintain one zero thousand expedite", "maintain 10000 expedite")]
    public void NormalizeDigits_DoesNotEatExpedite(string input, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.NormalizeDigits(input));
    }

    [Theory]
    [InlineData("runway 2 8 rate", "runway 28R")]
    [InlineData("enter right downwind runway one zero kite", "enter right downwind runway 10R")]
    [InlineData("cleared for takeoff runway 28 right", "cleared for takeoff runway 28R")]
    [InlineData("runway one eight left", "runway 18L")]
    public void NormalizeDigits_StillCollapsesRealSuffixMishears(string input, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.NormalizeDigits(input));
    }
}
