using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
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
        Assert.Equal(
            "cross runway 28L, hold short of runway 28R",
            PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28L"], [HoldShortTarget.Parse("28R")]))
        );
    }

    // "Cross taxiway bravo" is not codified phraseology — crossing clearances are a runway
    // construct (7110.65 §3-7-2.a.3). CROSS <taxiway> stays a valid input alias, but the pilot
    // reads back the §3-7 release for a taxiway hold-short: "continue taxiing".

    [Fact]
    public void Cross_TaxiwayTarget_Spoken_ReadsContinueTaxiing()
    {
        Assert.Equal("continue taxiing", PhraseologyVerbalizer.Verbalize(new CrossRunwayCommand(["B"], [])));
    }

    [Fact]
    public void Cross_TaxiwayTarget_Terminal_ReadsContinueTaxiing()
    {
        Assert.Equal("continue taxiing", PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["B"], [])));
    }

    [Fact]
    public void Cross_MixedRunwayAndTaxiway_VoicesRunwayThenContinue()
    {
        Assert.Equal("cross runway 28L, continue taxiing", PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28L", "B"], [])));
    }

    [Fact]
    public void Cross_TaxiwayWithHoldShort_VoicesBothClauses()
    {
        Assert.Equal(
            "continue taxiing, hold short of runway 28R",
            PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["B"], [HoldShortTarget.Parse("28R")]))
        );
    }
}
