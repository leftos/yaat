using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The pilot readback for a multi-runway or HS-modified CROSS must voice each crossing (joined
/// with "and") plus any hold-short clause, mirroring the RES readback forms
/// (see <see cref="ResumeReadbackTests"/>). A single-runway, no-HS CROSS keeps the rule-driven
/// "cross runway {rwy}" readback so STT stays unchanged (issue #291).
/// </summary>
public class CrossReadbackTests
{
    public CrossReadbackTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Cross_SingleRunway_Spoken_UsesRulePath()
    {
        Assert.Equal("cross runway two eight left", PhraseologyVerbalizer.Verbalize(new CrossRunwayCommand(["28L"], [])));
    }

    [Fact]
    public void Cross_SingleRunway_Terminal_UsesRulePath()
    {
        Assert.Equal("cross runway 28L", PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28L"], [])));
    }

    [Fact]
    public void Cross_MultipleRunways_JoinedWithAnd()
    {
        Assert.Equal("cross runway 28R and 28L", PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28R", "28L"], [])));
    }

    [Fact]
    public void Cross_MultipleRunways_Spoken_JoinedWithAnd()
    {
        Assert.Equal("cross runway two eight right and two eight left", PhraseologyVerbalizer.Verbalize(new CrossRunwayCommand(["28R", "28L"], [])));
    }

    [Fact]
    public void Cross_RunwayAndHoldShort_VoicesBothClauses()
    {
        Assert.Equal("cross runway 28L, hold short of runway 28R", PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28L"], ["28R"])));
    }
}
