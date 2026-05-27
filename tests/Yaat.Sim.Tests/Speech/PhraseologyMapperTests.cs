using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Speech;

public class PhraseologyMapperTests
{
    public PhraseologyMapperTests()
    {
        // PhraseologyMapper now validates rule outputs via CommandParser.Parse, which needs the
        // NavigationDatabase loaded for fix-based commands (DCT, HFIX, etc.). Load real nav data
        // from TestData/ so every rule's parser path works — synthetic stubs would hide integration
        // problems per the repo's testing convention.
        TestVnasData.EnsureInitialized();
    }

    private static readonly MapContext NoContext = MapContext.Empty;

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

    // --- Cross-fix altitude restrictions ---
    // FAA 7110.65 §4-5-7 / §5-7 / AIM §4-4-10 / §5-3-1: "CROSS (fix) AT (altitude)", with
    // AT OR ABOVE/BELOW modifiers and the AIM "at and maintain" variant. Flight-level forms
    // are normalized to a single digit token by AtcNumberParser, so they match the basic
    // "{alt}" capture without a dedicated rule.

    [Theory]
    [InlineData("cross cepin at five thousand", "CFIX CEPIN AT 5000")]
    [InlineData("cross cepin at and maintain five thousand", "CFIX CEPIN AT 5000")]
    [InlineData("cross cepin at maintain five thousand", "CFIX CEPIN AT 5000")]
    [InlineData("cross cepin at flight level two five zero", "CFIX CEPIN AT 25000")]
    [InlineData("cross cepin at or above five thousand", "CFIX CEPIN A5000")]
    [InlineData("cross cepin at or below five thousand", "CFIX CEPIN B5000")]
    [InlineData("cross cepin at or above flight level two five zero", "CFIX CEPIN A25000")]
    [InlineData("cross cepin at and maintain five thousand at two five zero knots", "CFIX CEPIN AT 5000 250")]
    [InlineData("cross cepin at five thousand at two five zero knots", "CFIX CEPIN AT 5000 250")]
    public void CrossFix_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand.ToUpperInvariant());
    }

    [Fact]
    public void CrossFix_RunwayCrossWins_NoFixMisparse()
    {
        // Regression guard: ground-side "cross runway 28R" must keep mapping to CROSS (taxi),
        // not be hijacked by the new "cross {fix} at {alt}" rule with {fix}="runway".
        var result = PhraseologyMapper.Map("cross runway two eight right", NoContext);
        Assert.NotNull(result);
        Assert.Equal("CROSS 28R", result!.CanonicalCommand);
    }

    [Fact]
    public void CrossFix_PlusClearedApproach_CompoundsViaGreedyMatcher()
    {
        // §4-8 / §5-9 / AIM §5-4: "Cross (fix) at or above (altitude), cleared (type) approach."
        // The greedy multi-clause matcher should consume CrossFix then ClearedApproach as
        // two adjacent clauses joined by ", " — no special compound rule needed.
        var result = PhraseologyMapper.Map("cross cepin at or above five thousand cleared ils runway two eight right approach", NoContext);
        Assert.NotNull(result);
        Assert.Equal("CFIX CEPIN A5000, CAPP ILS28R", result!.CanonicalCommand.ToUpperInvariant());
    }

    // --- ClimbVia (CLIMB VIA SID) ---
    // FAA 7110.65 §4-3-2, §4-5-7, §5-2-9, §5-5-14. Bare "climb via SID" and the
    // "except maintain {alt}" override form. Named-SID variants (e.g. "climb via the
    // SUZAN2 departure") require a SID-name normalizer that isn't yet in the pipeline.

    [Theory]
    [InlineData("climb via sid", "CVIA")]
    [InlineData("climb via sid except maintain five thousand", "CVIA 5000")]
    [InlineData("climb via sid except maintain flight level one eight zero", "CVIA 18000")]
    public void ClimbVia_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void ClimbVia_PlusCrossFix_ExceptModifierChainsViaGreedyMatcher()
    {
        // FAA 7110.65 §4-5: "CLIMB VIA SID, EXCEPT CROSS (fix) (revised altitude)".
        // The greedy multi-clause matcher should parse the bare CV rule plus the CrossFix rule
        // as two adjacent clauses joined by ", ", with "except" skipped as an unmatched filler.
        var result = PhraseologyMapper.Map("climb via sid except cross cepin at or above five thousand", NoContext);
        Assert.NotNull(result);
        Assert.Equal("CVIA, CFIX CEPIN A5000", result!.CanonicalCommand.ToUpperInvariant());
    }

    // --- Single-canonical clusters (Stage 11+) ---

    [Theory]
    // §5-6-6: "DEPART (fix) HEADING (degrees)".
    [InlineData("depart cepin heading two seven zero", "DEPART CEPIN 270")]
    [InlineData("depart cepin heading zero niner zero", "DEPART CEPIN 090")]
    public void DepartFix_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand.ToUpperInvariant());
    }

    [Theory]
    // §3-10-11 "option approved" — alternate to "cleared for the option".
    [InlineData("option approved", "COPT")]
    public void OptionApproved_Rule(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    // §2-1-20 "CAUTION WAKE TURBULENCE" — bare wake-turbulence caution. The trailing
    // traffic description is captured by the variadic and dropped (CWT carries no args).
    [InlineData("caution wake turbulence", "CWT")]
    [InlineData("caution wake turbulence boeing seven three seven on five mile final", "CWT")]
    public void WakeAdvisory_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Taxi/ground verb synonyms (FAA 7110.65 §3-7) ---
    // The §3-7 phraseology block lists "TAXI / CONTINUE TAXIING / PROCEED VIA (route)" as
    // synonyms, "BEHIND (traffic)" as an alternate to FOLLOW, and "HOLD FOR (reason)" /
    // "ACROSS RUNWAY (number)" as alternate forms. Shipping all of these as STT synonyms.

    [Theory]
    [InlineData("continue taxiing via bravo charlie", "TAXI B C")]
    [InlineData("proceed via bravo charlie", "TAXI B C")]
    [InlineData("across runway two eight right", "CROSS 28R")]
    [InlineData("hold for wake turbulence", "HOLD")]
    [InlineData("hold for traffic", "HOLD")]
    public void TaxiAndGroundSynonyms_Rules(string transcript, string expected)
    {
        var ctx = MapContext.Empty with
        {
            TaxiwayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B", "C" },
        };
        var result = PhraseologyMapper.Map(transcript, ctx);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void TaxiAndGroundSynonyms_BehindCallsign_Pattern()
    {
        // "Behind {callsign}" only captures a single token. The outer trailing CallsignParser
        // swallows N-numbers and known active callsigns, so this rule serves mostly as an
        // intermediary path before the LLM fallback. We verify the rule pattern matches an
        // arbitrary single-token capture via the test-only matcher to keep the regression
        // honest without depending on CallsignParser's behavior in the full pipeline.
        var captures = PhraseologyMapper.TryMatchPatternForTests(["behind", "{callsign}"], ["behind", "ualx5321"]);
        Assert.NotNull(captures);
        Assert.Equal("ualx5321", captures!["callsign"]);
    }

    // --- Pattern-entry APPROVED shorthand (FAA 7110.65 §3-10-1) ---
    // Controllers commonly use the bare-direction approval form when accepting a pilot's
    // request, e.g. "straight in approved" / "right traffic approved". Maps to the same
    // canonicals as the longer "make right traffic" / "enter straight-in" forms.

    [Theory]
    [InlineData("straight in approved", "EF")]
    [InlineData("right traffic approved", "MRT")]
    [InlineData("left traffic approved", "MLT")]
    public void PatternEntryApproved_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Tower modifier wedges (FAA 7110.65 §3-9-7, §3-9-10, §3-10-1, §3-10-2) ---
    // Between the runway designator and the verb, controllers insert "shortened" / "full
    // length" (runway availability) or "wind (direction) at (velocity)" (informational).
    // These modifiers don't change the canonical command — the sim doesn't model reduced
    // landing distance available — so the rules silently consume them.

    [Theory]
    [InlineData("runway two eight right shortened cleared for takeoff", "CTO")]
    [InlineData("runway two eight right full length cleared for takeoff", "CTO")]
    [InlineData("runway two eight right wind two seven zero at one five cleared for takeoff", "CTO")]
    [InlineData("runway two eight right shortened cleared to land", "CLAND")]
    [InlineData("runway two eight right wind two seven zero at one five cleared to land", "CLAND")]
    [InlineData("runway two eight right shortened line up and wait", "LUAW")]
    [InlineData("runway two eight right full length line up and wait", "LUAW")]
    // §3-9-10 sub 7 — runway intersection departure + shortened modifier wedge.
    [InlineData("runway two eight right at charlie five intersection departure shortened cleared for takeoff", "CTO")]
    // §3-10 — "change to runway" preamble drops; the second runway literal anchors the verb.
    [InlineData("change to runway two eight right runway two eight right cleared to land", "CLAND")]
    public void TowerModifierWedges_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
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

    // --- Approach-type variants beyond ILS/RNAV (LOC, LOC BC, VOR, LDA) ---
    // FAA 7110.65 §4-8-1, AIM §5-4. Canonical encodes the type as a prefix on the
    // approach ID: ILS=I, LOC=L, LOC BC=B, VOR=V, LDA=X — all resolved by
    // NavigationDatabase.ResolveApproachId.

    [Theory]
    [InlineData("cleared localizer runway two eight right approach", "CAPP LOC28R")]
    [InlineData("cleared localizer two eight right approach", "CAPP LOC28R")]
    [InlineData("cleared localizer back course runway one one approach", "CAPP B11")]
    [InlineData("cleared vor runway three four approach", "CAPP VOR34")]
    [InlineData("cleared vor three four approach", "CAPP VOR34")]
    [InlineData("cleared lda runway one seven left approach", "CAPP LDA17L")]
    public void ApproachType_Variants(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- ExpectApproach type-token variants (FAA 7110.65 §4-7-5, AIM §5-4) ---
    // Same alternation as ClearedApproach above — LOC, LOC BC, VOR, LDA in addition to
    // ILS/RNAV/visual that were already covered.

    [Theory]
    [InlineData("expect localizer runway two eight right approach", "EAPP LOC28R")]
    [InlineData("expect localizer back course runway one one approach", "EAPP B11")]
    [InlineData("expect vor runway three four approach", "EAPP VOR34")]
    [InlineData("expect lda runway one seven left approach", "EAPP LDA17L")]
    public void ExpectApproachType_Variants(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- PTAC rules (combined vector + altitude + approach clearance) ---

    [Theory]
    [InlineData(
        "turn left heading two seven zero descend and maintain three thousand cleared ils runway two eight right approach",
        "PTAC 270 3000 ILS28R"
    )]
    [InlineData(
        "turn right heading one zero zero descend and maintain two thousand five hundred cleared ils runway two eight left approach",
        "PTAC 100 2500 ILS28L"
    )]
    [InlineData("fly heading two seven zero descend and maintain three thousand cleared ils runway two eight right approach", "PTAC 270 3000 ILS28R")]
    [InlineData("turn left heading two seven zero descend and maintain three thousand cleared ils two eight right approach", "PTAC 270 3000 ILS28R")]
    [InlineData(
        "turn left heading two seven zero descend maintain three thousand cleared ils runway two eight right approach",
        "PTAC 270 3000 ILS28R"
    )]
    [InlineData(
        "turn left heading three six zero climb and maintain five thousand cleared ils runway two eight right approach",
        "PTAC 360 5000 ILS28R"
    )]
    [InlineData(
        "turn right heading one eight zero descend and maintain four thousand cleared rnav runway two eight right approach",
        "PTAC 180 4000 RNAV28R"
    )]
    [InlineData("fly heading two seven zero descend and maintain three thousand cleared rnav runway one two approach", "PTAC 270 3000 RNAV12")]
    public void Ptac_Rules(string transcript, string expected)
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

    // --- Capture validation (rejects noisy mistranscriptions) ---

    [Fact]
    public void Climb_To_NonNumericAltitude_RejectsMatch()
    {
        // Whisper mistranscription of "climb and maintain flight level three five zero" as
        // "climb to main aim flight level tree five zero". The rule engine must NOT accept
        // "climb to {alt}" with {alt}="main" and produce the nonsense canonical "CM main".
        // Instead it should fail to match and let a later position (the flight level) pick up
        // the actual altitude — or fall through to null if nothing valid is found.
        var result = PhraseologyMapper.Map("climb to main aim flight level tree five zero", NoContext);
        if (result is not null)
        {
            // Whatever we produce, it must not have "main" or any non-digit token after CM.
            Assert.DoesNotContain("CM main", result.CanonicalCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("main", result.CanonicalCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aim", result.CanonicalCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FlyHeading_NonNumericHeading_RejectsMatch()
    {
        // "fly heading apple" should not match "fly heading {hdg}" with {hdg}="apple".
        var result = PhraseologyMapper.Map("fly heading apple", NoContext);
        Assert.Null(result);
    }

    [Fact]
    public void Squawk_NonNumericCode_RejectsMatch()
    {
        // "squawk vfr" is a special-case rule (SQVFR); "squawk random" should not match.
        var result = PhraseologyMapper.Map("squawk random", NoContext);
        Assert.Null(result);
    }

    [Fact]
    public void ClimbToFlightLevel_ValidAltitude_StillMatches()
    {
        // Happy path regression: verify the validator didn't break normal "climb to {alt}" matching.
        var result = PhraseologyMapper.Map("climb to flight level three five zero", NoContext);
        Assert.NotNull(result);
        Assert.Equal("CM 35000", result!.CanonicalCommand);
    }

    [Fact]
    public void Taxi_ToRunwayWithoutPath_IsUnsupported()
    {
        // "taxi to runway 28R" is not valid phraseology — controllers say either
        // "runway 28R, taxi via <path>" or "taxi via <path>" and attach the runway/hold-short
        // inline. The rule engine must not match this; the LLM fallback handles it.
        var result = PhraseologyMapper.Map("taxi to runway two eight right", NoContext);
        Assert.Null(result);
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

    [Fact]
    public void Callsign_Leading_UsGa_HybridWhisperForm()
    {
        // Whisper's initial_prompt is seeded with the ICAO form "N9225L", so it normalizes the
        // tail mid-transcription while still emitting the word "november" the speaker prefixed.
        // Regression test for the hybrid form reaching the mapper.
        var result = PhraseologyMapper.Map("november N9225L climb and maintain 2000", NoContext);
        Assert.NotNull(result);
        Assert.Equal("N9225L", result!.Callsign);
        Assert.Equal("CM 2000", result.CanonicalCommand);
    }

    [Fact]
    public void Callsign_Leading_UsGa_BareIcaoForm()
    {
        // Fully-normalized form: Whisper emitted just "N9225L" with no "november" prefix.
        var result = PhraseologyMapper.Map("N9225L climb and maintain 2000", NoContext);
        Assert.NotNull(result);
        Assert.Equal("N9225L", result!.Callsign);
        Assert.Equal("CM 2000", result.CanonicalCommand);
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
    // Two-pass filler stripping: the user says "enter right downwind FOR runway 28R" (with "for"
    // as a conversational filler). Pass 1 against the unmodified tokens fails to match the
    // five-token rule because "downwind" isn't directly followed by "runway" — pass 1 falls back
    // to the bare "ERD" three-token rule. Pass 2 strips "for" and the five-token rule then
    // matches cleanly, recovering the runway capture.
    [InlineData("enter right downwind for runway two eight right", "ERD 28R")]
    [InlineData("enter left downwind for runway one eight left", "ELD 18L")]
    public void Pattern_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    // --- Runway capture validation ---
    //
    // PhraseologyMapper validates {rwy} captures against MapContext.AvailableRunways and fails
    // the rule when the captured token isn't a real runway in any scenario airport. This catches
    // Whisper mishears like "288" so the LLM fallback gets a chance to recover the intended
    // runway. When AvailableRunways is empty (the default for MapContext.Empty / NoContext), the
    // validator is skipped — every existing test in this file exercises that path implicitly.

    /// <summary>
    /// Build a MapContext containing a single airport's runway list. Used by the runway-validation
    /// tests below to drive the post-pass without touching scenario-loading machinery.
    /// </summary>
    private static MapContext ContextWithRunways(string airport, params string[] runways)
    {
        return new MapContext([], [])
        {
            AvailableRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [airport] = runways },
        };
    }

    [Fact]
    public void RunwayCapture_ValidRunway_Matches()
    {
        var ctx = ContextWithRunways("KOAK", "28R", "28L", "10R", "10L", "30", "12", "33", "15");
        var result = PhraseologyMapper.Map("enter right downwind for runway two eight right", ctx);
        Assert.NotNull(result);
        Assert.Equal("ERD 28R", result!.CanonicalCommand);
    }

    [Fact]
    public void RunwayCapture_InvalidRunway_NoFuzzyRecovery_FailsRule()
    {
        // A captured runway with no fuzzy-recovery signal (e.g. "274" — trailing 4 has no
        // phonetic mapping) drops the rule match entirely so the LLM fallback gets a chance to
        // recover with full transcript context. The 28R/28L/etc. variants ARE in the airport's
        // list, so the only reason this fails is the absent phonetic snap for trailing 4.
        var ctx = ContextWithRunways("KOAK", "28R", "28L", "10R", "10L", "30", "12", "33", "15");
        var result = PhraseologyMapper.Map("enter right downwind for runway 274", ctx);
        Assert.Null(result);
    }

    [Fact]
    public void RunwayCapture_InvalidRunway_NoContext_PassesThrough()
    {
        // With AvailableRunways empty, validation is skipped — the rule still fires and produces
        // its raw capture. This protects every existing PhraseologyMapperTests case that passes
        // MapContext.Empty (NoContext) from regressing when the validator is added.
        var result = PhraseologyMapper.Map("enter right downwind for runway 288", NoContext);
        Assert.NotNull(result);
        Assert.Equal("ERD 288", result!.CanonicalCommand);
    }

    [Theory]
    // Single-digit runway numbers are zero-padded to two digits during the runway-collapse pass
    // so they match real-world designators ("01R", "09L"). Without this, "one right" would become
    // "1R" and miss the airport's actual runway list.
    [InlineData("enter right downwind runway one right", "ERD 01R")]
    [InlineData("enter right downwind runway zero one right", "ERD 01R")]
    [InlineData("enter right downwind runway nine left", "ERD 09L")]
    [InlineData("enter right downwind runway zero nine left", "ERD 09L")]
    public void RunwayCapture_SingleDigit_ZeroPadded(string transcript, string expected)
    {
        // Validation requires the runway to be in AvailableRunways — supply both the padded form
        // (the actual airport runway) and confirm the collapse produces it.
        var ctx = ContextWithRunways("KSFO", "01L", "01R", "19L", "19R", "09L", "09R", "27L", "27R");
        var result = PhraseologyMapper.Map(transcript, ctx);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    // CollapseRunwayDesignators uses .EndsWith matching for "ight" and "eft" so the long tail of
    // Whisper rhyme mishears all collapse to the right suffix without us maintaining a static
    // vocab. "10 tight" / "10 ight" / "10 sight" / "10 light" → 10R; "10 deft" / "10 weft" → 10L.
    [InlineData("enter right downwind runway one zero tight", "ERD 10R")]
    [InlineData("enter right downwind runway one zero ight", "ERD 10R")]
    [InlineData("enter right downwind runway one zero light", "ERD 10R")]
    [InlineData("enter right downwind runway one zero sight", "ERD 10R")]
    [InlineData("enter right downwind runway one zero might", "ERD 10R")]
    [InlineData("enter right downwind runway one zero kite", "ERD 10R")]
    [InlineData("enter right downwind runway one zero bite", "ERD 10R")]
    [InlineData("enter right downwind runway one zero rite", "ERD 10R")]
    [InlineData("enter right downwind runway one zero deft", "ERD 10L")]
    [InlineData("enter right downwind runway one zero weft", "ERD 10L")]
    [InlineData("enter right downwind runway one zero eft", "ERD 10L")]
    public void RunwayCapture_PhoneticSuffixCollapse(string transcript, string expected)
    {
        var ctx = ContextWithRunways("KOAK", "10L", "10R", "28L", "28R");
        var result = PhraseologyMapper.Map(transcript, ctx);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    // TryRecoverRunway: when the {rwy} capture is a 3-digit token (Whisper concatenated
    // "two eight right" → "288"), snap to the matching real runway via the trailing-digit
    // phonetic mapping. Trailing 8 → R is the strongest signal (right rhymes with eight) —
    // this is the user's reported case.
    [InlineData("288", "ERD 28R")]
    [InlineData("108", "ERD 10R")]
    [InlineData("018", "ERD 01R")]
    [InlineData("098", "ERD 09R")]
    public void RunwayCapture_FuzzyRecovery_TrailingEight(string captured, string expected)
    {
        var ctx = ContextWithRunways("KOAK", "01R", "01L", "09R", "09L", "10R", "10L", "28R", "28L");
        var result = PhraseologyMapper.Map($"enter right downwind runway {captured}", ctx);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Theory]
    // Trailing 0 → L is a weaker fallback (used when "left" gets dropped into a digit slot).
    // Only applied when the L-suffixed runway exists at that base; otherwise tries C, then bare.
    [InlineData("280", "ERD 28L")]
    [InlineData("100", "ERD 10L")]
    public void RunwayCapture_FuzzyRecovery_TrailingZero_PrefersLeft(string captured, string expected)
    {
        var ctx = ContextWithRunways("KOAK", "10R", "10L", "28R", "28L");
        var result = PhraseologyMapper.Map($"enter right downwind runway {captured}", ctx);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void RunwayCapture_FuzzyRecovery_NoSignal_FailsRule()
    {
        // Trailing digits other than 8 and 0 have no phonetic mapping — TryRecoverRunway returns
        // null, which escalates runwayInvalid and Map returns null. The LLM fallback (which
        // doesn't run in this unit test) would then own recovery.
        var ctx = ContextWithRunways("KOAK", "28R", "28L");
        var result = PhraseologyMapper.Map("enter right downwind runway 285", ctx);
        Assert.Null(result);
    }

    [Fact]
    public void RunwayCapture_FuzzyRecovery_BareNumberFallback()
    {
        // 3-digit "300" with no L/R/C variant in the airport's list — falls back to the bare
        // base "30" if it exists.
        var ctx = ContextWithRunways("KOAK", "30", "12", "33", "15");
        var result = PhraseologyMapper.Map("enter right downwind runway 300", ctx);
        Assert.NotNull(result);
        Assert.Equal("ERD 30", result!.CanonicalCommand);
    }

    [Fact]
    public void RunwayCapture_UnionAcrossMultipleAirports()
    {
        // Membership check spans every airport in AvailableRunways, not just one — a controller
        // working multiple destinations should be able to issue a runway clearance for any of them.
        var ctx = new MapContext([], [])
        {
            AvailableRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["KOAK"] = ["28R", "28L"],
                ["KSFO"] = ["28L", "28R", "01L", "01R"],
            },
        };
        var result = PhraseologyMapper.Map("enter right downwind runway zero one right", ctx);
        Assert.NotNull(result);
        Assert.Equal("ERD 01R", result!.CanonicalCommand);
    }

    [Theory]
    // The two-pass logic must NOT regress rules that legitimately use "for" as a literal token.
    // Pass 1 against the unmodified tokens matches these rules directly, so pass 2 is never
    // invoked (matchedRules already contains a rule whose pattern includes "for").
    [InlineData("cleared for takeoff", "CTO")]
    [InlineData("cleared for the option", "COPT")]
    [InlineData("cleared for low approach", "LA")]
    [InlineData("cleared for touch and go", "TG")]
    [InlineData("cleared for stop and go", "SG")]
    public void TwoPassFiller_PreservesForLiteralRules(string transcript, string expected)
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
    [InlineData("pushback approved", "PUSH")]
    [InlineData("hold position", "HOLD")]
    [InlineData("resume taxi", "RES")]
    [InlineData("continue taxi", "RES")]
    [InlineData("cross runway two eight right", "CROSS 28R")]
    [InlineData("hold short of runway two eight right", "HS 28R")]
    [InlineData("exit left", "EL")]
    [InlineData("exit right", "ER")]
    // Taxi with NATO-phonetic path. Empty taxiway set → each NATO word splits to one letter.
    [InlineData("taxi via delta hotel", "TAXI D H")]
    [InlineData("taxi via tango uniform whiskey", "TAXI T U W")]
    [InlineData("runway two eight right taxi via bravo charlie", "TAXI B C 28R")]
    [InlineData("taxi to runway two eight right via bravo charlie", "TAXI B C 28R")]
    [InlineData("taxi via bravo charlie hold short of runway two eight right", "TAXI B C HS 28R")]
    [InlineData("taxi via bravo charlie hold short of two eight right", "TAXI B C HS 28R")]
    [InlineData("taxi via bravo charlie cross runway two five left", "TAXI B C CROSS 25L")]
    // Dual runway clearance: cross + hold-short (7110.65 §3-7-2.b).
    [InlineData("taxi via charlie cross runway two seven left hold short of runway two seven right", "TAXI C CROSS 27L HS 27R")]
    [InlineData("taxi via charlie cross runway two seven left hold short of two seven right", "TAXI C CROSS 27L HS 27R")]
    // Leading-runway + hold-short (combined rule — mirrors line 632).
    [InlineData("runway three zero taxi via bravo charlie hold short of runway two eight right", "TAXI B C 30 HS 28R")]
    [InlineData("runway three zero taxi via bravo charlie hold short runway two eight right", "TAXI B C 30 HS 28R")]
    // Leading-runway + cross (combined rule).
    [InlineData("runway three zero taxi via bravo charlie cross runway two five left", "TAXI B C 30 CROSS 25L")]
    // Leading-runway + cross + hold-short (combined rule).
    [InlineData("runway three zero taxi via charlie cross runway two seven left hold short of runway two seven right", "TAXI C 30 CROSS 27L HS 27R")]
    // Regression guard for the exact production failure: Whisper hyphenated digits ("Runway 3-0",
    // "2-8-Rate") plus the /aɪt/ → /eɪt/ mishear. Exercises the digit-merge pass, the
    // EndsWith("ate") suffix match, and the new leading-runway + hold-short rule together.
    [InlineData("runway 3 0 taxi via bravo charlie hold short runway 2 8 rate", "TAXI B C 30 HS 28R")]
    // Pushback — onto taxiway variations.
    [InlineData("pushback onto tango approved", "PUSH T")]
    [InlineData("push back onto tango approved", "PUSH T")]
    [InlineData("pushback onto tango facing taxiway uniform approved", "PUSH T U")]
    [InlineData("pushback onto tango facing taxiway uniform", "PUSH T U")]
    [InlineData("pushback approved facing north", "PUSH FACE N")]
    [InlineData("pushback facing south", "PUSH FACE S")]
    [InlineData("pushback onto tango facing east", "PUSH T FACE E")]
    [InlineData("pushback onto tango facing west approved", "PUSH T FACE W")]
    // "face" synonym (no -ing).
    [InlineData("pushback face north", "PUSH FACE N")]
    [InlineData("pushback approved face south", "PUSH FACE S")]
    [InlineData("pushback onto tango face east", "PUSH T FACE E")]
    [InlineData("pushback onto tango face west approved", "PUSH T FACE W")]
    // "tail" — instructs the tail direction; canonical emits TAIL.
    [InlineData("pushback tail north", "PUSH TAIL N")]
    [InlineData("pushback approved tail east", "PUSH TAIL E")]
    [InlineData("pushback onto tango tail south", "PUSH T TAIL S")]
    [InlineData("pushback onto tango tail west approved", "PUSH T TAIL W")]
    public void Ground_Rules(string transcript, string expected)
    {
        var result = PhraseologyMapper.Map(transcript, NoContext);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.CanonicalCommand);
    }

    [Fact]
    public void Taxi_Via_TopologyDisambiguation_CollapsesMultiLetterTaxiway()
    {
        // Airport has taxiway "TE" — "tango echo" should collapse to a single TE token
        // rather than two letters. Rule output carries the multi-letter name through.
        var ctx = new MapContext([], []) { TaxiwayNames = new HashSet<string>(["TE"], StringComparer.OrdinalIgnoreCase) };
        var result = PhraseologyMapper.Map("taxi via tango echo", ctx);
        Assert.NotNull(result);
        Assert.Equal("TAXI TE", result!.CanonicalCommand);
    }

    [Fact]
    public void Taxi_Via_TopologyDisambiguation_SplitsWhenMultiLetterAbsent()
    {
        // Airport has separate T and E taxiways with no TE — "tango echo" must split.
        var ctx = new MapContext([], []) { TaxiwayNames = new HashSet<string>(["T", "E"], StringComparer.OrdinalIgnoreCase) };
        var result = PhraseologyMapper.Map("taxi via tango echo", ctx);
        Assert.NotNull(result);
        Assert.Equal("TAXI T E", result!.CanonicalCommand);
    }

    [Fact]
    public void Pushback_Onto_Cardinal_WithTaxiwaySet()
    {
        // Taxiway set present; single-letter "tango" still collapses, cardinal still resolves.
        var ctx = new MapContext([], []) { TaxiwayNames = new HashSet<string>(["T"], StringComparer.OrdinalIgnoreCase) };
        var result = PhraseologyMapper.Map("pushback onto tango facing north", ctx);
        Assert.NotNull(result);
        Assert.Equal("PUSH T FACE N", result!.CanonicalCommand);
    }

    [Fact]
    public void LoggingPipeline_EmitsCorrectiveStepLines()
    {
        // Not a behavior test — this is a regression guard on the debug logging output. The
        // user wants every corrective step to surface in the client log file so STT failures
        // can be debugged post-hoc. Capture PhraseologyMapper's debug output against a
        // transcript that exercises most of the transformations and assert each step logged.
        var output = new CollectingOutput();
        SimLogBuilder.CreateForTest(output).EnableCategory("PhraseologyMapper", LogLevel.Debug).InitializeSimLog();

        var result = PhraseologyMapper.Map("runway 288 right uh taxi via tingo uniform whiskey november 346 gulf", NoContext);
        Assert.NotNull(result);

        // The order below is the exact pipeline order; each string is a tag from the log line.
        string[] expectedSteps =
        [
            "NumberNormalize", // "288 right" → "28R" etc
            "FillerStrip", // "uh" removed
            "NatoNearMiss", // "tingo" → "tango", "gulf" → "golf"
            "CallsignExtract", // "november 346 golf" → N346G
            "NatoCollapse", // "tango uniform whiskey" → "T U W"
            "RuleMatch", // matched rule
        ];

        var log = output.ToString();
        foreach (var step in expectedSteps)
        {
            Assert.Contains($"[Speech] {step}", log);
        }

        // Reset SimLog to the null factory so subsequent tests don't inherit this capture.
        SimLog.InitializeForTest(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }

    // xUnit 2.x ITestOutputHelper shim that captures all output into a single string so the
    // test can assert on accumulated log content without needing the real TestOutputHelper
    // (which rejects output after the test completes).
    private sealed class CollectingOutput : ITestOutputHelper
    {
        private readonly System.Text.StringBuilder _sb = new();

        public void WriteLine(string message) => _sb.AppendLine(message);

        public void WriteLine(string format, params object[] args) => _sb.AppendLine(string.Format(format, args));

        public override string ToString() => _sb.ToString();
    }

    [Fact]
    public void Taxi_LiveWhisperRegression_TingoAndGulfMishear()
    {
        // Live Whisper transcript after the NATO-scramble fix: "runway 288 right taxi via
        // tingo uniform whiskey november 346 gulf". Whisper misheard "tango" as "tingo" and
        // "golf" as "gulf". Both are 1-char phonetic mishears that the callsign parser and
        // NATO normalizer can't resolve on their own. With the near-miss resolver in place,
        // "tingo" → "tango" and "gulf" → "golf" before callsign extraction, letting the full
        // pipeline recover the intended canonical command.
        var result = PhraseologyMapper.Map("runway 288 right taxi via tingo uniform whiskey november 346 gulf", NoContext);
        Assert.NotNull(result);
        Assert.Equal("N346G", result!.Callsign);
        Assert.Equal("TAXI T U W 28R", result.CanonicalCommand);
    }

    [Fact]
    public void Taxi_LiveWhisperRegression_288RightWithNatoPath()
    {
        // Regression: live Whisper transcript from an instructor saying "runway two eight right
        // taxi via tango uniform whiskey, November 346 Golf". Whisper doubled up the eight/right
        // homophone → "288 right" (which must collapse to "28R", not "288R"), and the callsign
        // trailed with a "for November..." leading filler that was a pilot-style signoff.
        // Both mappers previously failed here; with the fix the rule engine handles it end-to-end.
        var result = PhraseologyMapper.Map("runway 288 right taxi via tango uniform whiskey november three four six golf", NoContext);
        Assert.NotNull(result);
        Assert.Equal("N346G", result!.Callsign);
        Assert.Equal("TAXI T U W 28R", result.CanonicalCommand);
    }

    [Fact]
    public void Pushback_Facing_InvalidCardinal_DropsFacingClause()
    {
        // "facing northeast" is an intercardinal and deliberately out of scope — the cardinal
        // post-pass fails the facing-cardinal rule, and the greedy engine falls back to the
        // bare "pushback approved" rule, silently dropping the facing clause. Known limitation;
        // the LLM fallback path can't engage here because the rule engine already produced a
        // valid canonical. Document the behavior with a test so regressions are caught.
        var result = PhraseologyMapper.Map("pushback approved facing northeast", NoContext);
        Assert.NotNull(result);
        Assert.Equal("PUSH", result!.CanonicalCommand);
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
        var ctx = new MapContext([], ["CEPIN", "SUNOL"]);
        var result = PhraseologyMapper.Map("direct to cepin", ctx);
        Assert.NotNull(result);
        Assert.Equal("DCT CEPIN", result!.CanonicalCommand);
    }

    [Fact]
    public void Fix_MistranscribedAgainstProgrammedFixes_IsCorrected()
    {
        // Whisper heard "sepin"; real fix is CEPIN. The matcher should swap it in.
        var ctx = new MapContext([], ["CEPIN", "SUNOL"]);
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
        var ctx = new MapContext([], ["CEPIN", "SUNOL"]);
        var result = PhraseologyMapper.Map("climb and maintain five thousand direct to sepin", ctx);
        Assert.NotNull(result);
        Assert.Equal("CM 5000, DCT CEPIN", result!.CanonicalCommand);
    }

    // --- Variadic capture {name...} ---

    [Fact]
    public void Variadic_Trailing_GreedyConsumesAllRemaining()
    {
        // Trailing variadic in the last position consumes every remaining token into one capture.
        var captures = PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}"], ["taxi", "via", "b", "c", "d"]);
        Assert.NotNull(captures);
        Assert.Equal("b c d", captures!["path"]);
    }

    [Fact]
    public void Variadic_MidPattern_StopsAtNextLiteral()
    {
        // Variadic followed by a required literal captures up to (not including) the first
        // occurrence of that literal. Minimum 1 token consumed.
        var captures = PhraseologyMapper.TryMatchPatternForTests(
            ["taxi", "via", "{path...}", "hold", "short"],
            ["taxi", "via", "b", "c", "hold", "short"]
        );
        Assert.NotNull(captures);
        Assert.Equal("b c", captures!["path"]);
    }

    [Fact]
    public void Variadic_SingleTokenCapture_IsAllowed()
    {
        // Minimum-size match: exactly one variadic token before the trailing literal.
        var captures = PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}", "hold"], ["taxi", "via", "b", "hold"]);
        Assert.NotNull(captures);
        Assert.Equal("b", captures!["path"]);
    }

    [Fact]
    public void Variadic_ZeroTokensCaptured_Fails()
    {
        // Variadic requires at least one token — an empty slice (literal immediately follows)
        // must not match.
        var captures = PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}", "hold"], ["taxi", "via", "hold"]);
        Assert.Null(captures);
    }

    [Fact]
    public void Variadic_Trailing_ZeroInput_Fails()
    {
        // Trailing variadic with no remaining tokens must fail — minimum 1 token consumed.
        var captures = PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}"], ["taxi", "via"]);
        Assert.Null(captures);
    }

    [Fact]
    public void Variadic_NoMatchingNextLiteral_Fails()
    {
        // Variadic followed by a literal that never appears in the input must fail.
        var captures = PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}", "hold"], ["taxi", "via", "b", "c", "d"]);
        Assert.Null(captures);
    }

    [Fact]
    public void Variadic_FollowedByOptional_ThrowsRuleAuthoringError()
    {
        // Optional literal directly after a variadic is ambiguous and not supported.
        Assert.Throws<InvalidOperationException>(() =>
            PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}", "hold?"], ["taxi", "via", "b", "hold"])
        );
    }

    [Fact]
    public void Variadic_FollowedByCapture_ThrowsRuleAuthoringError()
    {
        // Another capture directly after a variadic has no anchor to stop the greedy consume.
        Assert.Throws<InvalidOperationException>(() =>
            PhraseologyMapper.TryMatchPatternForTests(["taxi", "via", "{path...}", "{rwy}"], ["taxi", "via", "b", "28R"])
        );
    }
}
