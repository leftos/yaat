using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class AtcNumberParserTests
{
    // --- NormalizeDigits: spoken → digit form ---

    [Theory]
    // Basic digit sequences
    [InlineData("two seven zero", "270")]
    [InlineData("one two three", "123")]
    [InlineData("zero niner zero", "090")]
    [InlineData("tree fife", "35")]
    [InlineData("niner", "9")]
    // Teen / tens words
    [InlineData("eleven", "11")]
    [InlineData("twenty", "20")]
    [InlineData("twenty five", "25")]
    [InlineData("fifteen", "15")]
    // Thousand multiplier
    [InlineData("five thousand", "5000")]
    [InlineData("one one thousand", "11000")]
    [InlineData("eleven thousand", "11000")]
    [InlineData("two five thousand", "25000")]
    // Thousand + hundred compound
    [InlineData("five thousand five hundred", "5500")]
    [InlineData("one one thousand five hundred", "11500")]
    // Hundred multiplier alone
    [InlineData("three hundred", "300")]
    // Flight level
    [InlineData("flight level three five zero", "35000")]
    [InlineData("flight level one eight zero", "18000")]
    [InlineData("fl two five zero", "25000")]
    public void NormalizeDigits_PureNumberPhrase(string input, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.NormalizeDigits(input));
    }

    [Theory]
    // Embedded in ATC commands
    [InlineData("climb and maintain five thousand", "climb and maintain 5000")]
    [InlineData("descend and maintain flight level three five zero", "descend and maintain 35000")]
    [InlineData("fly heading two seven zero", "fly heading 270")]
    [InlineData("turn left heading zero niner zero", "turn left heading 090")]
    [InlineData("reduce speed to two five zero", "reduce speed to 250")]
    [InlineData("squawk seven five zero zero", "squawk 7500")]
    [InlineData("cleared for takeoff runway two eight right", "cleared for takeoff runway 28R")]
    // Whisper formats spoken "two thousand" as "2,000" using English thousand-separator commas
    // when the model recognizes the word but emits its numeric form. Without comma stripping,
    // the tokenizer splits "2,000" into ["2", "000"] and the rule engine's {alt} capture grabs
    // only "2" → producing "CM 2" instead of "CM 2000". Cover the common groupings.
    [InlineData("climb and maintain 2,000", "climb and maintain 2000")]
    [InlineData("descend and maintain 12,000", "descend and maintain 12000")]
    [InlineData("descend and maintain 1,234,567", "descend and maintain 1234567")]
    // Runway designator collapse: split-token pairs from Whisper ("28 right") canonicalize to
    // single tokens ("28R") so both the rule engine's {rwy} capture and the LLM mapper see the
    // same form. Lowercases happen because NormalizeDigits emits lowercase tokens; the runway
    // direction characters are uppercased by CollapseRunwayDesignators.
    [InlineData("cleared for takeoff runway 28 right", "cleared for takeoff runway 28R")]
    [InlineData("enter right downwind for runway 28 left", "enter right downwind for runway 28L")]
    [InlineData("hold short of runway 27 center", "hold short of runway 27C")]
    // Whisper's right/eight homophone mishear: pilot says "two eight right", Whisper doubles up
    // and emits "two eight eight right" → AtcNumberParser concatenates to "288 right" →
    // CollapseRunwayDesignators must recognize the 3-digit prefix as a mishear and trim to
    // 2 digits before appending the suffix, yielding "28R" instead of the nonsense "288R".
    // Real runway numbers are 01-36, so any digit prefix longer than 2 is always a mishear.
    [InlineData("cleared for takeoff runway 288 right", "cleared for takeoff runway 28R")]
    [InlineData("runway 288 right taxi via bravo", "runway 28R taxi via bravo")]
    public void NormalizeDigits_WithCommandContext(string input, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.NormalizeDigits(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("climb and maintain", "climb and maintain")]
    public void NormalizeDigits_NoNumbers_PassesThrough(string input, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.NormalizeDigits(input));
    }

    // --- NormalizeDigits: paired-cardinal flight-number coalescing ---
    //
    // Inverse of FlightNumberToPairedWords. The pre-fix parser treated each spoken compound as
    // its own number run, so "two thirty four" mangled to "2 34" and downstream callsign
    // extraction resolved "United 234" as UAL2. The fix consumes trailing 2-digit pairs after a
    // single-digit-word OR 2-digit-compound lead and concatenates the digits.
    [Theory]
    // Pair-spoken flight numbers (FlightNumberToPairedWords inverse).
    [InlineData("two thirty four", "234")]
    [InlineData("twelve thirty four", "1234")]
    [InlineData("november one twenty three", "november 123")]
    [InlineData("united two thirty four", "united 234")]
    [InlineData("delta twelve thirty four", "delta 1234")]
    [InlineData("one twenty three forty five", "12345")]
    [InlineData("seven forty two", "742")]
    // Trailing leading-zero pair ("zero zero" → 00) — needed for flight numbers like 1500
    // whose paired form is "fifteen zero zero" (PairToWords falls back to digit-by-digit for
    // any pair with a zero tens-digit).
    [InlineData("fifteen zero zero", "1500")]
    [InlineData("forty five zero zero", "4500")]
    // Cardinal arithmetic form ("two hundred [and] thirty four" = 234). The hundred multiplier
    // already shifts the lead to its hundreds magnitude, so the trailing pair is added
    // arithmetically (not digit-shifted). The "and" filler is optional.
    [InlineData("two hundred thirty four", "234")]
    [InlineData("two hundred and thirty four", "234")]
    // Heading / speed shortcuts that previously dropped trailing digits the same way callsigns
    // did. "Fly heading two thirty" = heading 230. "Reduce speed to one eighty" = speed 180.
    [InlineData("fly heading two thirty", "fly heading 230")]
    [InlineData("reduce speed to one eighty", "reduce speed to 180")]
    public void NormalizeDigits_PairedCardinalFlightNumbers(string input, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.NormalizeDigits(input));
    }

    // --- FlightNumberToWords: digit → spoken form ---

    [Theory]
    [InlineData(123, "one two three")]
    [InlineData(1, "one")]
    [InlineData(9, "nine")]
    [InlineData(1234, "one two three four")]
    [InlineData(4500, "four five zero zero")]
    [InlineData(0, "zero")]
    public void FlightNumberToWords_Returns_DigitByDigit(int flight, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.FlightNumberToWords(flight));
    }

    [Fact]
    public void FlightNumberToWords_Negative_ReturnsEmpty()
    {
        Assert.Equal("", AtcNumberParser.FlightNumberToWords(-1));
    }

    // --- FlightNumberToPairedWords: digit → paired spoken form ---

    [Theory]
    // Single digit
    [InlineData(1, "one")]
    [InlineData(5, "five")]
    [InlineData(9, "nine")]
    // 2 digits
    [InlineData(10, "ten")]
    [InlineData(12, "twelve")]
    [InlineData(15, "fifteen")]
    [InlineData(19, "nineteen")]
    [InlineData(20, "twenty")]
    [InlineData(25, "twenty five")]
    [InlineData(42, "forty two")]
    [InlineData(99, "ninety nine")]
    // 3 digits
    [InlineData(100, "one zero zero")]
    [InlineData(105, "one zero five")]
    [InlineData(123, "one twenty three")]
    [InlineData(500, "five zero zero")]
    [InlineData(742, "seven forty two")]
    // 4 digits
    [InlineData(1234, "twelve thirty four")]
    [InlineData(1500, "fifteen zero zero")]
    [InlineData(4500, "forty five zero zero")]
    [InlineData(9999, "ninety nine ninety nine")]
    // 5 digits
    [InlineData(12345, "one twenty three forty five")]
    public void FlightNumberToPairedWords_Returns_PairedForm(int flight, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.FlightNumberToPairedWords(flight));
    }

    [Fact]
    public void FlightNumberToPairedWords_Negative_ReturnsEmpty()
    {
        Assert.Equal("", AtcNumberParser.FlightNumberToPairedWords(-1));
    }

    // --- AltitudeToWords: digit → spoken form ---

    [Theory]
    [InlineData(5000, "five thousand")]
    [InlineData(11000, "one one thousand")]
    [InlineData(5500, "five thousand five hundred")]
    [InlineData(11500, "one one thousand five hundred")]
    [InlineData(2500, "two thousand five hundred")]
    [InlineData(18000, "flight level one eight zero")]
    [InlineData(35000, "flight level three five zero")]
    [InlineData(41000, "flight level four one zero")]
    public void AltitudeToWords_Returns_SpokenForm(int altitude, string expected)
    {
        Assert.Equal(expected, AtcNumberParser.AltitudeToWords(altitude));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void AltitudeToWords_NonPositive_ReturnsEmpty(int altitude)
    {
        Assert.Equal("", AtcNumberParser.AltitudeToWords(altitude));
    }
}
