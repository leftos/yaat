using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public class PhraseologyVerbalizerProcedureNameTests
{
    [Fact]
    public void Nimi5_SpokenByRegisteredPronunciation()
    {
        (string terminal, string tts) = PhraseologyVerbalizer.ProcedureName("NIMI5");

        Assert.Equal("nimitz five", tts);
        Assert.Equal("NIMI5", terminal);
    }

    [Fact]
    public void Razrr5_WithoutPronunciation_SpelledLikeAFix()
    {
        (string _, string tts) = PhraseologyVerbalizer.ProcedureName("RAZRR5");

        Assert.Equal($"{PhraseologyVerbalizer.SpellFix("RAZRR")} five", tts);
    }
}
