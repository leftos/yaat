using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class CallsignParserTests
{
    private static readonly string[] NoActiveCallsigns = [];

    // --- TryParseLeading ---

    // Consumed = telephony_word_count + 1 (for the flight number token).

    [Theory]
    [InlineData("southwest 123 climb and maintain 5000", "SWA123", 2)]
    [InlineData("united 456 descend and maintain 3000", "UAL456", 2)]
    [InlineData("american 789 fly heading 270", "AAL789", 2)]
    [InlineData("delta 1234 squawk 5555", "DAL1234", 2)]
    [InlineData("alaska 42 cleared for takeoff", "ASA42", 2)]
    public void TryParseLeading_AirlineCallsign(string transcript, string expectedCallsign, int expectedConsumed)
    {
        var result = CallsignParser.TryParseLeading(transcript, NoActiveCallsigns);
        Assert.NotNull(result);
        Assert.Equal(expectedCallsign, result!.IcaoCallsign);
        Assert.Equal(expectedConsumed, result.TokensConsumed);
    }

    [Theory]
    [InlineData("air canada 789 turn right", "ACA789", 3)]
    [InlineData("all nippon 100 contact departure", "ANA100", 3)]
    public void TryParseLeading_MultiWordTelephony(string transcript, string expectedCallsign, int expectedConsumed)
    {
        var result = CallsignParser.TryParseLeading(transcript, NoActiveCallsigns);
        Assert.NotNull(result);
        Assert.Equal(expectedCallsign, result!.IcaoCallsign);
        Assert.Equal(expectedConsumed, result.TokensConsumed);
    }

    [Theory]
    [InlineData("november 12345 taxi via alpha", "N12345", 2)]
    [InlineData("november 42 contact ground", "N42", 2)]
    public void TryParseLeading_GeneralAviation(string transcript, string expectedCallsign, int expectedConsumed)
    {
        var result = CallsignParser.TryParseLeading(transcript, NoActiveCallsigns);
        Assert.NotNull(result);
        Assert.Equal(expectedCallsign, result!.IcaoCallsign);
        Assert.Equal(expectedConsumed, result.TokensConsumed);
    }

    [Theory]
    // Hybrid form: Whisper normalized the tail ("N9225L") but kept the word "november" the
    // speaker said in front of it. Biased output from seeding the initial_prompt with ICAO forms.
    [InlineData("november N9225L climb and maintain 2000", "N9225L", 2)]
    [InlineData("november N123BS cleared for takeoff", "N123BS", 2)]
    // Bare ICAO form: Whisper fully normalized ("november niner two two five lima" → "N9225L").
    [InlineData("N9225L climb and maintain 2000", "N9225L", 1)]
    [InlineData("N42 contact ground", "N42", 1)]
    public void TryParseLeading_UsGa_HybridAndBareIcaoForms(string transcript, string expectedCallsign, int expectedConsumed)
    {
        var result = CallsignParser.TryParseLeading(transcript, NoActiveCallsigns);
        Assert.NotNull(result);
        Assert.Equal(expectedCallsign, result!.IcaoCallsign);
        Assert.Equal(expectedConsumed, result.TokensConsumed);
    }

    [Theory]
    // Trailing NATO phonetic letters after the digits: "november 9225 lima" → N9225L.
    [InlineData("november 9225 lima climb and maintain 2000", "N9225L", 3)]
    [InlineData("november 123 bravo sierra cleared for takeoff", "N123BS", 4)]
    // Multi-token digit run (Whisper partially normalized: ["9", "225"] instead of "9225") plus
    // trailing NATO letter. This is the exact shape produced after NormalizeDigits sees "diner
    // 225" — the word-form digit and the numeric-form digits land as separate tokens.
    [InlineData("november 9 225 lima climb and maintain 2000", "N9225L", 4)]
    public void TryParseLeading_UsGa_DigitsPlusTrailingNatoLetters(string transcript, string expectedCallsign, int expectedConsumed)
    {
        var result = CallsignParser.TryParseLeading(transcript, NoActiveCallsigns);
        Assert.NotNull(result);
        Assert.Equal(expectedCallsign, result!.IcaoCallsign);
        Assert.Equal(expectedConsumed, result.TokensConsumed);
    }

    [Fact]
    public void TryParseLeading_BareAirlineIcao_OnlyMatchesWhenActive()
    {
        // Bare "SWA123" is only accepted when it's in the active-callsigns list, to avoid false
        // positives from random 3-letter+digits tokens.
        string[] active = ["SWA123"];
        var result = CallsignParser.TryParseLeading("SWA123 fly heading 270", active);
        Assert.NotNull(result);
        Assert.Equal("SWA123", result!.IcaoCallsign);
        Assert.Equal(1, result.TokensConsumed);
    }

    [Fact]
    public void TryParseLeading_BareAirlineIcao_NotInActive_ReturnsNull()
    {
        // SWA999 is not active — the parser must decline to avoid matching random 3+digit tokens.
        var result = CallsignParser.TryParseLeading("SWA999 fly heading 270", NoActiveCallsigns);
        Assert.Null(result);
    }

    [Fact]
    public void TryParseLeading_NoCallsign_ReturnsNull()
    {
        Assert.Null(CallsignParser.TryParseLeading("climb and maintain 5000", NoActiveCallsigns));
    }

    [Fact]
    public void TryParseLeading_TelephonyWithoutNumber_ReturnsNull()
    {
        Assert.Null(CallsignParser.TryParseLeading("southwest climb", NoActiveCallsigns));
    }

    [Fact]
    public void TryParseLeading_SharedCallsign_PrefersActiveMatch()
    {
        // VIRGIN is shared between VIR and VOZ. If VOZ123 is an active callsign, the parser
        // should pick VOZ over VIR even though VIR is lexically-smaller.
        string[] active = ["VOZ123"];
        var result = CallsignParser.TryParseLeading("virgin 123 descend", active);
        Assert.NotNull(result);
        Assert.Equal("VOZ123", result!.IcaoCallsign);
    }

    [Fact]
    public void TryParseLeading_SharedCallsign_NoActiveMatch_PicksFirst()
    {
        var result = CallsignParser.TryParseLeading("virgin 999 descend", NoActiveCallsigns);
        Assert.NotNull(result);
        // Just confirm *some* VIRGIN ICAO wins; order depends on TSV row order.
        Assert.EndsWith("999", result!.IcaoCallsign);
        Assert.True(result.IcaoCallsign.Length == 6, $"Expected 3-letter ICAO + 3-digit number, got {result.IcaoCallsign}");
    }

    // --- TryParseTrailing ---

    [Fact]
    public void TryParseTrailing_CallsignAtEnd()
    {
        var result = CallsignParser.TryParseTrailing("climb and maintain 5000 southwest 123", NoActiveCallsigns);
        Assert.NotNull(result);
        Assert.Equal("SWA123", result!.IcaoCallsign);
    }

    [Fact]
    public void TryParseTrailing_NoCallsignAtEnd_ReturnsNull()
    {
        // "southwest 123" is at the start, not the end, so TryParseTrailing should find nothing.
        Assert.Null(CallsignParser.TryParseTrailing("southwest 123 climb and maintain 5000", NoActiveCallsigns));
    }

    // --- IcaoToSpoken (primary paired form) ---

    [Theory]
    [InlineData("SWA123", "southwest one twenty three")]
    [InlineData("UAL456", "united four fifty six")]
    [InlineData("AAL789", "american seven eighty nine")]
    [InlineData("DAL1234", "delta twelve thirty four")]
    [InlineData("ASA42", "alaska forty two")]
    [InlineData("SWA5", "southwest five")]
    [InlineData("UAL100", "united one zero zero")]
    public void IcaoToSpoken_AirlineCallsign_UsesPairedForm(string icao, string expected)
    {
        Assert.Equal(expected, CallsignParser.IcaoToSpoken(icao));
    }

    [Theory]
    [InlineData("N12345", "november one two three four five")]
    [InlineData("N42", "november four two")]
    [InlineData("N123BS", "november one two three bravo sierra")]
    [InlineData("N7AB", "november seven alpha bravo")]
    public void IcaoToSpoken_UsGeneralAviation(string icao, string expected)
    {
        Assert.Equal(expected, CallsignParser.IcaoToSpoken(icao));
    }

    [Theory]
    // Bolivia: CP-prefix. No digit-delimiter, so full NATO phonetic.
    [InlineData("CPZXA", "charlie papa zulu xray alpha")]
    // Canada: C-FXXX, C-GXXX
    [InlineData("CFABC", "charlie foxtrot alpha bravo charlie")]
    [InlineData("CGXYZ", "charlie golf xray yankee zulu")]
    // UK: G-XXXX
    [InlineData("GABCD", "golf alpha bravo charlie delta")]
    // Germany: D-XXXX
    [InlineData("DEABC", "delta echo alpha bravo charlie")]
    public void IcaoToSpoken_ForeignGeneralAviation(string icao, string expected)
    {
        Assert.Equal(expected, CallsignParser.IcaoToSpoken(icao));
    }

    [Fact]
    public void IcaoToSpoken_UnknownAirline_UsesNatoPhonetic()
    {
        // QZX is confirmed absent from the OpenFlights dataset. Unknown airlines fall back
        // to NATO phonetic for the letters, which is what ATC actually uses on the radio.
        var result = CallsignParser.IcaoToSpoken("QZX123");
        Assert.Equal("quebec zulu xray one two three", result);
    }

    [Fact]
    public void IcaoToSpoken_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", CallsignParser.IcaoToSpoken(""));
        Assert.Equal("", CallsignParser.IcaoToSpoken("   "));
    }

    // --- GetSpokenVariants: airline ---

    [Fact]
    public void GetSpokenVariants_Airline_IncludesPairedAndDigitByDigit()
    {
        var variants = CallsignParser.GetSpokenVariants("SWA123", aircraftType: null, NoActiveCallsigns);
        Assert.Contains("southwest one twenty three", variants);
        Assert.Contains("southwest one two three", variants);
    }

    [Fact]
    public void GetSpokenVariants_Airline_IgnoresAircraftType()
    {
        // Aircraft type is irrelevant for airline flights — pilots always use the telephony.
        var variants = CallsignParser.GetSpokenVariants("SWA123", "B738", NoActiveCallsigns);
        Assert.DoesNotContain(variants, v => v.Contains("boeing"));
        Assert.Contains("southwest one twenty three", variants);
    }

    // --- GetSpokenVariants: US GA with aircraft type ---

    [Fact]
    public void GetSpokenVariants_UsGa_IncludesFullAndTypeBasedForms()
    {
        var variants = CallsignParser.GetSpokenVariants("N12345", "C172", NoActiveCallsigns);
        Assert.Contains("november one two three four five", variants);
        // C172 manufacturer is cessna, family is skyhawk — both should appear
        Assert.Contains(variants, v => v.StartsWith("cessna "));
        Assert.Contains(variants, v => v.StartsWith("skyhawk "));
    }

    [Fact]
    public void GetSpokenVariants_UsGa_IncludesShortenedWhenUnambiguous()
    {
        var variants = CallsignParser.GetSpokenVariants("N12345", "C172", NoActiveCallsigns);
        // Shortened last-3 form ("three four five")
        Assert.Contains("november three four five", variants);
        Assert.Contains("skyhawk three four five", variants);
    }

    [Fact]
    public void GetSpokenVariants_UsGa_OmitsShortenedWhenAmbiguous()
    {
        // Another active GA callsign ends with the same last 3 → shortened form unsafe.
        // Full forms (for both november and every type-based prefix) stay; only shortened is blocked.
        string[] active = ["N67345"];
        var variants = CallsignParser.GetSpokenVariants("N12345", "C172", active);
        Assert.Contains("november one two three four five", variants);
        Assert.Contains("cessna one two three four five", variants);
        Assert.Contains("skyhawk one two three four five", variants);
        Assert.DoesNotContain("november three four five", variants);
        Assert.DoesNotContain("cessna three four five", variants);
        Assert.DoesNotContain("skyhawk three four five", variants);
    }

    [Fact]
    public void GetSpokenVariants_UsGa_NoTypeInfo_OmitsTypeForms()
    {
        var variants = CallsignParser.GetSpokenVariants("N12345", aircraftType: null, NoActiveCallsigns);
        Assert.Contains("november one two three four five", variants);
        Assert.Contains("november three four five", variants);
        Assert.DoesNotContain(variants, v => v.StartsWith("cessna "));
    }

    [Fact]
    public void GetSpokenVariants_UsGa_ShortTailOmitsShortenedForm()
    {
        // N42 has only 2 tail chars, shortened form wouldn't be shorter than full — skip it.
        var variants = CallsignParser.GetSpokenVariants("N42", aircraftType: null, NoActiveCallsigns);
        Assert.Contains("november four two", variants);
        Assert.Single(variants); // no shortened form
    }

    [Fact]
    public void GetSpokenVariants_UsGa_WithLetters_UsesNatoPhoneticShortened()
    {
        // N123BS → last 3 is "3BS" → "three bravo sierra"
        var variants = CallsignParser.GetSpokenVariants("N123BS", aircraftType: null, NoActiveCallsigns);
        Assert.Contains("november one two three bravo sierra", variants);
        Assert.Contains("november three bravo sierra", variants);
    }

    // --- GetSpokenVariants: foreign GA / unknown ---

    [Fact]
    public void GetSpokenVariants_ForeignGa_SingleNatoForm()
    {
        var variants = CallsignParser.GetSpokenVariants("CPZXA", aircraftType: null, NoActiveCallsigns);
        Assert.Single(variants);
        Assert.Equal("charlie papa zulu xray alpha", variants[0]);
    }

    [Fact]
    public void GetSpokenVariants_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(CallsignParser.GetSpokenVariants("", null, NoActiveCallsigns));
        Assert.Empty(CallsignParser.GetSpokenVariants("   ", null, NoActiveCallsigns));
    }
}
