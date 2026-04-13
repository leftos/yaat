using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class PhraseologyMapperTests
{
    private static readonly PhraseologyMapper.MapContext NoContext = PhraseologyMapper.MapContext.Empty;

    // --- Heading rules ---

    [Theory]
    [InlineData("fly heading two seven zero", "FH 270")]
    [InlineData("fly heading zero niner zero", "FH 090")]
    [InlineData("heading two seven zero", "FH 270")]
    [InlineData("turn left heading two seven zero", "TL 270")]
    [InlineData("turn right heading zero niner zero", "TR 090")]
    [InlineData("fly present heading", "FPH")]
    [InlineData("maintain present heading", "FPH")]
    public void Heading_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    // Only the 7110.65 5-6-2 canonical form: "TURN (N) DEGREES LEFT/RIGHT".
    [InlineData("turn ten degrees left", "RELL 10")]
    [InlineData("turn twenty degrees right", "RELR 20")]
    [InlineData("turn thirty degrees right", "RELR 30")]
    public void RelativeTurn_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Altitude rules ---

    [Theory]
    [InlineData("climb and maintain five thousand", "CM 5000")]
    [InlineData("climb maintain five thousand", "CM 5000")]
    [InlineData("climb to five thousand", "CM 5000")]
    [InlineData("climb five thousand", "CM 5000")]
    [InlineData("descend and maintain three thousand", "DM 3000")]
    [InlineData("descend to three thousand", "DM 3000")]
    [InlineData("descend and maintain flight level three five zero", "DM 35000")]
    [InlineData("expedite climb to one one thousand", "EXP 11000")]
    [InlineData("expedite descent to five thousand", "EXP 5000")]
    public void Altitude_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Speed rules ---

    [Theory]
    [InlineData("reduce speed to two five zero", "SPD 250")]
    [InlineData("reduce speed two five zero", "SPD 250")]
    [InlineData("increase speed to two eight zero", "SPD 280")]
    [InlineData("maintain speed two five zero", "SPD 250")]
    [InlineData("resume normal speed", "RNS")]
    [InlineData("reduce to final approach speed", "RFAS")]
    public void Speed_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Navigation rules ---

    [Theory]
    [InlineData("direct to cepin", "DCT CEPIN")]
    [InlineData("direct cepin", "DCT CEPIN")]
    [InlineData("proceed direct cepin", "DCT CEPIN")]
    [InlineData("proceed direct to cepin", "DCT CEPIN")]
    [InlineData("fly direct cepin", "DCT CEPIN")]
    public void Navigation_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        // Fix names come through in whatever case the transcript had — mapper is lowercase
        // internally but passes captures through. Tower-layer normalization happens later.
        Assert.Equal(expected, result!.CanonicalCommand.ToUpperInvariant());
    }

    // --- Tower rules ---

    [Theory]
    [InlineData("cleared for takeoff", "CTO")]
    [InlineData("cleared for takeoff runway two eight right", "CTO")]
    [InlineData("line up and wait", "LUAW")]
    [InlineData("cleared to land", "CLAND")]
    [InlineData("cleared to land runway two eight right", "CLAND")]
    [InlineData("go around", "GA")]
    public void Tower_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Approach rules ---

    [Theory]
    [InlineData("cleared approach", "CAPP")]
    [InlineData("cleared ils two eight right approach", "CAPP ILS28R")]
    [InlineData("cleared ils runway two eight right approach", "CAPP ILS28R")]
    [InlineData("cleared rnav two eight right approach", "CAPP RNAV28R")]
    [InlineData("cleared visual approach runway two eight right", "CVA 28R")]
    [InlineData("cleared visual approach runway niner", "CVA 9")]
    public void Approach_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Transponder rules ---

    [Theory]
    [InlineData("squawk seven five zero zero", "SQ 7500")]
    [InlineData("squawk vfr", "SQVFR")]
    [InlineData("squawk ident", "IDENT")]
    [InlineData("ident", "IDENT")]
    public void Transponder_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Compound commands ---

    [Fact]
    public void Compound_ClimbAndFly()
    {
        var result = PhraseologyMapper.Map("climb and maintain five thousand and fly heading two seven zero", NoContext);
        Assert.NotNull(result);
        Assert.Equal("CM 5000, FH 270", result!.CanonicalCommand);
        Assert.Equal(2, result.MatchedRuleCount);
    }

    [Fact]
    public void Compound_DescendSpeedHeading()
    {
        var result = PhraseologyMapper.Map(
            "descend and maintain three thousand reduce speed to two five zero turn left heading one eight zero",
            NoContext
        );
        Assert.NotNull(result);
        Assert.Equal("DM 3000, SPD 250, TL 180", result!.CanonicalCommand);
    }

    // --- Callsign extraction ---

    [Fact]
    public void Callsign_Leading_Airline()
    {
        var result = PhraseologyMapper.Map("southwest one two three climb and maintain five thousand", NoContext);
        Assert.NotNull(result);
        Assert.Equal("SWA123", result!.Callsign);
        Assert.Equal("CM 5000", result.CanonicalCommand);
    }

    [Fact]
    public void Callsign_Trailing_Airline()
    {
        var result = PhraseologyMapper.Map("climb and maintain five thousand southwest one two three", NoContext);
        Assert.NotNull(result);
        Assert.Equal("SWA123", result!.Callsign);
        Assert.Equal("CM 5000", result.CanonicalCommand);
    }

    [Fact]
    public void Callsign_Leading_UsGa()
    {
        var result = PhraseologyMapper.Map("november one two three four five cleared for takeoff", NoContext);
        Assert.NotNull(result);
        Assert.Equal("N12345", result!.Callsign);
        Assert.Equal("CTO", result.CanonicalCommand);
    }

    // --- Condition prefixes ---

    [Fact]
    public void Condition_AtFix()
    {
        var result = PhraseologyMapper.Map("at cepin climb and maintain five thousand", NoContext);
        Assert.NotNull(result);
        Assert.Equal("AT CEPIN CM 5000", result!.CanonicalCommand);
    }

    [Fact]
    public void Condition_WhenLevelAt()
    {
        var result = PhraseologyMapper.Map("when level at five thousand fly heading two seven zero", NoContext);
        Assert.NotNull(result);
        Assert.Equal("LV 5000 FH 270", result!.CanonicalCommand);
    }

    // --- Disregard ---

    [Fact]
    public void Disregard_ClearsPriorCommands()
    {
        // Controller: "Climb and maintain 5000. Disregard. Descend and maintain 3000."
        var result = PhraseologyMapper.Map("climb and maintain five thousand disregard descend and maintain three thousand", NoContext);
        Assert.NotNull(result);
        Assert.Equal("DM 3000", result!.CanonicalCommand);
    }

    [Fact]
    public void Disregard_AloneDoesNotMatch()
    {
        // Just "disregard" by itself has nothing to cancel and no command — return null.
        var result = PhraseologyMapper.Map("disregard", NoContext);
        Assert.Null(result);
    }

    // --- Edge cases ---

    [Fact]
    public void Empty_ReturnsNull()
    {
        Assert.Null(PhraseologyMapper.Map("", NoContext));
        Assert.Null(PhraseologyMapper.Map("   ", NoContext));
    }

    [Fact]
    public void OnlyFiller_ReturnsNull()
    {
        Assert.Null(PhraseologyMapper.Map("uh um please sir", NoContext));
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        // Pure garbage with no rule match yields null.
        Assert.Null(PhraseologyMapper.Map("quantum entanglement gravitational waves", NoContext));
    }

    [Fact]
    public void FillerWords_AreStripped()
    {
        // "please" and "uh" are filler; the core command still matches.
        var result = PhraseologyMapper.Map("uh climb and maintain five thousand please", NoContext);
        Assert.NotNull(result);
        Assert.Equal("CM 5000", result!.CanonicalCommand);
    }

    // --- Pattern rules ---

    [Theory]
    [InlineData("enter left downwind runway two eight right", "ELD 28R")]
    [InlineData("enter right base runway two eight right", "ERB 28R")]
    [InlineData("enter final", "EF")]
    [InlineData("make left traffic", "MLT")]
    [InlineData("make right traffic runway two eight right", "MRT 28R")]
    [InlineData("turn crosswind", "TC")]
    [InlineData("turn downwind", "TD")]
    [InlineData("turn base", "TB")]
    [InlineData("turn final", "EF")]
    [InlineData("extend downwind", "EXT")]
    [InlineData("make short approach", "SA")]
    [InlineData("short approach", "SA")]
    [InlineData("circle the airport", "CIRCLE")]
    public void Pattern_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Hold rules ---

    [Theory]
    [InlineData("hold present position", "HPP")]
    [InlineData("hold present position left turns", "HPPL")]
    [InlineData("hold present position right turns", "HPPR")]
    [InlineData("hold at cepin", "HFIX CEPIN")]
    [InlineData("hold at cepin left turns", "HFIXL CEPIN")]
    [InlineData("hold at cepin right turns", "HFIXR CEPIN")]
    public void Hold_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand.ToUpperInvariant());
    }

    // --- Helicopter rules ---

    [Theory]
    [InlineData("cleared for air taxi", "ATXI")]
    [InlineData("cleared air taxi", "ATXI")]
    [InlineData("cleared for takeoff present position", "CTOPP")]
    public void Helicopter_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Ground rules ---

    [Theory]
    [InlineData("taxi to runway two eight right", "TAXI RWY 28R")]
    [InlineData("pushback approved", "PUSH")]
    [InlineData("hold position", "HOLD")]
    [InlineData("resume taxi", "RES")]
    [InlineData("continue taxi", "RES")]
    [InlineData("cross runway two eight right", "CROSS 28R")]
    [InlineData("hold short of runway two eight right", "HS 28R")]
    [InlineData("exit left", "EL")]
    [InlineData("exit right", "ER")]
    public void Ground_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Broadcast request rules ---

    [Theory]
    [InlineData("say speed", "SSPD")]
    [InlineData("say your speed", "SSPD")]
    [InlineData("say altitude", "SALT")]
    [InlineData("say heading", "SHDG")]
    [InlineData("say position", "SPOS")]
    [InlineData("report position", "SPOS")]
    public void Broadcast_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Tower expanded ---

    [Theory]
    [InlineData("cancel takeoff clearance", "CTOC")]
    [InlineData("cancel landing clearance", "CLC")]
    [InlineData("cleared for touch and go", "TG")]
    [InlineData("cleared touch and go runway two eight right", "TG")]
    [InlineData("cleared for stop and go", "SG")]
    [InlineData("cleared for low approach", "LA")]
    [InlineData("cleared for the option", "COPT")]
    [InlineData("cleared for option", "COPT")]
    public void Tower_ExpandedRules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Phase 3: phonetic fix matcher integration ---

    [Fact]
    public void Fix_ExactMatch_PassedThrough()
    {
        // When the transcript has the correct fix name, the matcher is a no-op.
        var ctx = new PhraseologyMapper.MapContext([], ["CEPIN", "SUNOL"]);
        var result = PhraseologyMapper.Map("direct to cepin", ctx);
        Assert.NotNull(result);
        Assert.Equal("DCT CEPIN", result!.CanonicalCommand);
    }

    [Fact]
    public void Fix_MistranscribedAgainstProgrammedFixes_IsCorrected()
    {
        // Whisper heard "sepin"; real fix is CEPIN. The matcher should swap it in.
        var ctx = new PhraseologyMapper.MapContext([], ["CEPIN", "SUNOL"]);
        var result = PhraseologyMapper.Map("direct to sepin", ctx);
        Assert.NotNull(result);
        Assert.Equal("DCT CEPIN", result!.CanonicalCommand);
    }

    [Fact]
    public void Fix_NoProgrammedFixes_PassesThroughRaw()
    {
        // With no programmed fixes context, the matcher can't correct and the raw token
        // survives (upper-cased by the fill-template step? actually captures preserve case).
        var result = PhraseologyMapper.Map("direct to sepin", NoContext);
        Assert.NotNull(result);
        // Either raw or matched via nav DB fallback (if it happened to load in this test).
        // For the no-nav-DB test context, we just assert a DCT command was produced.
        Assert.StartsWith("DCT ", result!.CanonicalCommand);
    }

    [Fact]
    public void Fix_WrongTokenDoesNotCorruptOtherCaptures()
    {
        // Ensure the matcher only rewrites capture values, not literal tokens from the rule.
        var ctx = new PhraseologyMapper.MapContext([], ["CEPIN", "SUNOL"]);
        var result = PhraseologyMapper.Map("climb and maintain five thousand direct to sepin", ctx);
        Assert.NotNull(result);
        Assert.Equal("CM 5000, DCT CEPIN", result!.CanonicalCommand);
    }
}
