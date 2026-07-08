using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The pilot readback for RES must voice the CROSS / HS modifiers, not just "resume taxi".
/// <c>RES CROSS 28L</c> parses into one <see cref="ResumeCommand"/> whose <c>CrossRunways</c>
/// were silently dropped by the verbalizer (no <c>ResumeCommand</c> arm in <c>ExtractArgs</c>,
/// only zero-capture "resume taxi" rules). Mirrors the TAXI cross/hold-short readback forms
/// (see <see cref="Issue172TaxiReadbackTests"/>).
/// </summary>
public class ResumeReadbackTests
{
    public ResumeReadbackTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Res_Bare_VoicesResumeTaxi()
    {
        Assert.Equal("resume taxi", PhraseologyVerbalizer.Verbalize(new ResumeCommand([], [])));
        Assert.Equal("resume taxi", PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand([], [])));
    }

    [Fact]
    public void Res_CrossRunway_Spoken_VoicesCrossing()
    {
        var result = PhraseologyVerbalizer.Verbalize(new ResumeCommand(["28L"], []));
        Assert.Equal("resume taxi, cross runway two eight left", result);
    }

    [Fact]
    public void Res_CrossRunway_Terminal_VoicesCrossing()
    {
        var result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand(["28L"], []));
        Assert.Equal("resume taxi, cross runway 28L", result);
    }

    [Fact]
    public void Res_MultipleCrossings_JoinedWithAnd()
    {
        var result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand(["28R", "28L"], []));
        Assert.Equal("resume taxi, cross runway 28R and 28L", result);
    }

    [Fact]
    public void Res_HoldShortRunway_VoicesHoldShort()
    {
        var result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand([], ["28R"]));
        Assert.Equal("resume taxi, hold short of runway 28R", result);
    }

    [Fact]
    public void Res_CrossAndHoldShort_VoicesBothClauses()
    {
        var result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand(["28L"], ["28R"]));
        Assert.Equal("resume taxi, cross runway 28L, hold short of runway 28R", result);
    }
}
