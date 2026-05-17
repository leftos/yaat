using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public class PhraseologyVerbalizerTests
{
    // --- Altitude ---

    [Fact]
    public void Verbalize_ClimbMaintain_ReadsbackVerbatimWithSpokenAltitude()
    {
        var result = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(5000));
        Assert.Equal("climb and maintain five thousand", result);
    }

    [Fact]
    public void Verbalize_DescendMaintain_ReadsbackVerbatimWithSpokenAltitude()
    {
        var result = PhraseologyVerbalizer.Verbalize(new DescendMaintainCommand(3500));
        Assert.Equal("descend and maintain three thousand five hundred", result);
    }

    [Fact]
    public void Verbalize_ClimbMaintain_FlightLevel()
    {
        var result = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(33000));
        Assert.Equal("climb and maintain flight level three three zero", result);
    }

    [Fact]
    public void Verbalize_VariedModerate_KeepsVerbatim()
    {
        var result = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(5000), PilotPersonality.Varied, FrequencyActivityLevel.Moderate);

        Assert.Equal("climb and maintain five thousand", result);
    }

    [Theory]
    [InlineData(FrequencyActivityLevel.Busy)]
    [InlineData(FrequencyActivityLevel.Saturated)]
    public void Verbalize_VariedBusy_UsesShortestAltitudeShortcut(FrequencyActivityLevel activityLevel)
    {
        var result = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(5000), PilotPersonality.Varied, activityLevel);

        Assert.Equal("up to five thousand", result);
    }

    // --- Heading ---

    [Fact]
    public void Verbalize_FlyHeading_ThreeDigitForm()
    {
        var result = PhraseologyVerbalizer.Verbalize(new FlyHeadingCommand(new MagneticHeading(270)));
        Assert.Equal("fly heading two seven zero", result);
    }

    [Fact]
    public void Verbalize_TurnLeft_ThreeDigitForm()
    {
        var result = PhraseologyVerbalizer.Verbalize(new TurnLeftCommand(new MagneticHeading(90)));
        Assert.Equal("turn left heading zero nine zero", result);
    }

    [Fact]
    public void Verbalize_TurnRight_ZeroPaddedDigits()
    {
        var result = PhraseologyVerbalizer.Verbalize(new TurnRightCommand(new MagneticHeading(5)));
        Assert.Equal("turn right heading zero zero five", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_UsesShortestHeadingShortcut()
    {
        var result = PhraseologyVerbalizer.Verbalize(
            new TurnLeftCommand(new MagneticHeading(90)),
            PilotPersonality.Varied,
            FrequencyActivityLevel.Busy
        );

        Assert.Equal("left heading zero nine zero", result);
    }

    [Fact]
    public void Verbalize_RelativeLeft_TensWordsForm()
    {
        var result = PhraseologyVerbalizer.Verbalize(new LeftTurnCommand(30));
        Assert.Equal("turn thirty degrees left", result);
    }

    [Fact]
    public void Verbalize_RelativeRight_TensWordsForm()
    {
        var result = PhraseologyVerbalizer.Verbalize(new RightTurnCommand(20));
        Assert.Equal("turn twenty degrees right", result);
    }

    [Theory]
    [InlineData(45, "forty five")]
    [InlineData(270, "two seventy")]
    public void DegreesWords_RelativeTurnsUseGroupedForm(int degrees, string expected)
    {
        Assert.Equal(expected, PhraseologyVerbalizer.DegreesWords(degrees));
    }

    // --- Speed ---

    [Fact]
    public void Verbalize_Speed_SpellsKnotsLiteral()
    {
        // Rule pattern is ["maintain", "{spd}", "knots"] (first declared in AltitudeSpeedRules).
        // SpeedCommand uses the SpeedWords colloquial form for round knots — "two fifty" not
        // "two five zero" — so Whisper handles the readback far better. See SpeedWords doc.
        var result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(250));
        Assert.Equal("maintain two fifty knots", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_SpeedShortcutKeepsKnots()
    {
        var result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(250), PilotPersonality.Varied, FrequencyActivityLevel.Busy);

        Assert.Equal("two fifty knots", result);
    }

    // --- Transponder ---

    [Fact]
    public void Verbalize_Squawk_FourDigitsSpoken()
    {
        var result = PhraseologyVerbalizer.Verbalize(new SquawkCommand(1234));
        Assert.Equal("squawk one two three four", result);
    }

    [Fact]
    public void Verbalize_Squawk_PadsTo4DigitsForLowCodes()
    {
        var result = PhraseologyVerbalizer.Verbalize(new SquawkCommand(56));
        Assert.Equal("squawk zero zero five six", result);
    }

    [Fact]
    public void Verbalize_Ident_LiteralFromRule()
    {
        // The rule is ["squawk", "ident"] (it's filed under transponder phraseology).
        var result = PhraseologyVerbalizer.Verbalize(new IdentCommand());
        Assert.Equal("squawk ident", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_NoShortcutFallsBackToVerbatim()
    {
        var result = PhraseologyVerbalizer.Verbalize(new IdentCommand(), PilotPersonality.Varied, FrequencyActivityLevel.Busy);

        Assert.Equal("squawk ident", result);
    }

    // --- Tower (no captures filled — fewest-captures rule wins) ---

    [Fact]
    public void Verbalize_LineUpAndWait_PicksRuleWithoutRunway()
    {
        // LineUpAndWaitCommand carries no runway. Verbalizer prefers the "line up and wait"
        // rule over "line up and wait runway {rwy}" because it has zero captures.
        var result = PhraseologyVerbalizer.Verbalize(new LineUpAndWaitCommand());
        Assert.Equal("line up and wait", result);
    }

    [Fact]
    public void Verbalize_ClearedForTakeoff_PicksRuleWithoutRunway()
    {
        var result = PhraseologyVerbalizer.Verbalize(new ClearedForTakeoffCommand(new DefaultDeparture()));
        Assert.Equal("cleared for takeoff", result);
    }

    [Fact]
    public void Verbalize_ClearedToLand_PicksRuleWithoutRunway()
    {
        var result = PhraseologyVerbalizer.Verbalize(new ClearedToLandCommand());
        Assert.Equal("cleared to land", result);
    }

    // --- Unknown / unverbalizable ---

    [Fact]
    public void Verbalize_UnsupportedCommand_ReturnsNull()
    {
        var result = PhraseologyVerbalizer.Verbalize(new UnsupportedCommand("ZZZ 999"));
        Assert.Null(result);
    }

    // --- Round-trip: verbalized output should re-parse via PhraseologyMapper ---

    [Fact]
    public void Verbalize_ClimbMaintain_RoundTripsViaPhraseologyMapper()
    {
        var verbalized = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(5000))!;
        var normalized = Yaat.Sim.Speech.AtcNumberParser.NormalizeDigits(verbalized);
        Assert.Equal("climb and maintain 5000", normalized);
    }

    [Fact]
    public void Verbalize_FlyHeading_RoundTripsViaPhraseologyMapper()
    {
        var verbalized = PhraseologyVerbalizer.Verbalize(new FlyHeadingCommand(new MagneticHeading(270)))!;
        var normalized = Yaat.Sim.Speech.AtcNumberParser.NormalizeDigits(verbalized);
        Assert.Equal("fly heading 270", normalized);
    }

    // --- Helpers exposed for AtParkingPhase + responder ---

    [Fact]
    public void HeadingDigits_Wraps360ToZero()
    {
        Assert.Equal("zero zero zero", PhraseologyVerbalizer.HeadingDigits(new MagneticHeading(360)));
    }

    [Fact]
    public void HeadingDigits_NegativeWraps()
    {
        Assert.Equal("two seven zero", PhraseologyVerbalizer.HeadingDigits(new MagneticHeading(-90)));
    }

    [Fact]
    public void DigitsWords_Padding()
    {
        Assert.Equal("zero zero one", PhraseologyVerbalizer.DigitsWords(1, minWidth: 3));
    }

    [Fact]
    public void SpellRunway_LeftRightCenter()
    {
        Assert.Equal("two eight right", PhraseologyVerbalizer.SpellRunway("28R"));
        Assert.Equal("two eight left", PhraseologyVerbalizer.SpellRunway("28L"));
        Assert.Equal("three four center", PhraseologyVerbalizer.SpellRunway("34C"));
    }

    [Fact]
    public void SpellTaxiway_NatoLetters()
    {
        Assert.Equal("bravo six", PhraseologyVerbalizer.SpellTaxiway("B6"));
        Assert.Equal("alpha alpha", PhraseologyVerbalizer.SpellTaxiway("AA"));
    }

    // --- Frequency formatting (FAA 7110.65 §2-4-16) ---

    [Fact]
    public void FrequencyToWords_TwoDecimalDigits()
    {
        Assert.Equal("one two five point three five", PhraseologyVerbalizer.FrequencyToWords(125.35));
    }

    [Fact]
    public void FrequencyToWords_OneDecimalDigit_NoZeroPad()
    {
        // 7110.65 §2-4-16 example: 121.5 MHz → "One two one point five." Trailing zeros dropped.
        Assert.Equal("one two one point five", PhraseologyVerbalizer.FrequencyToWords(121.5));
    }

    [Fact]
    public void FrequencyToWords_ThirdDecimalTruncated()
    {
        // 7110.65 §2-4-16 example: 135.275 MHz → "One three five point two seven."
        Assert.Equal("one three five point two seven", PhraseologyVerbalizer.FrequencyToWords(135.275));
    }

    [Fact]
    public void FrequencyToWords_WholeNumber_SingleZeroAfterPoint()
    {
        // 7110.65 §2-4-16 example: 369.0 MHz → "Three six niner point zero." YAAT uses "nine"
        // for digit 9 across all spoken numbers; see DigitToWord in AtcNumberParser.
        Assert.Equal("three six nine point zero", PhraseologyVerbalizer.FrequencyToWords(369.0));
    }

    [Fact]
    public void FrequencyToWords_LiveAtcExamples()
    {
        Assert.Equal("one one eight point two", PhraseologyVerbalizer.FrequencyToWords(118.2));
        Assert.Equal("one one nine point six", PhraseologyVerbalizer.FrequencyToWords(119.6));
        Assert.Equal("one two four point nine five", PhraseologyVerbalizer.FrequencyToWords(124.95));
    }

    [Fact]
    public void FrequencyToWords_8_33kHzSpacing_DropsImpliedThirdDigit()
    {
        // 8.33 kHz channels: 128.525 reads as "one two eight point five two" — pilots and
        // controllers omit the trailing 5 because it's implied by the channel spacing.
        Assert.Equal("one two eight point five two", PhraseologyVerbalizer.FrequencyToWords(128.525));
        Assert.Equal("one three two point zero one", PhraseologyVerbalizer.FrequencyToWords(132.015));
    }

    [Fact]
    public void FrequencyToWords_FloatingPointTolerance()
    {
        // 119.6 represented as 119.60000000000001 (binary-fp drift) must not produce extra digits.
        Assert.Equal("one one nine point six", PhraseologyVerbalizer.FrequencyToWords(119.60000000000001));
    }

    // --- Speed: round-knot pilot colloquial forms ---

    [Theory]
    [InlineData(100, "maintain one hundred knots")]
    [InlineData(150, "maintain one fifty knots")]
    [InlineData(180, "maintain one eighty knots")]
    [InlineData(200, "maintain two hundred knots")]
    [InlineData(220, "maintain two twenty knots")]
    [InlineData(250, "maintain two fifty knots")]
    [InlineData(280, "maintain two eighty knots")]
    [InlineData(310, "maintain three ten knots")]
    [InlineData(350, "maintain three fifty knots")]
    public void Verbalize_Speed_RoundKnots_UsesColloquialForm(int speed, string expected)
    {
        // Pilot readbacks favor "two fifty" over the 7110.65 controller form "two five zero".
        // Whisper handles the colloquial form far better than the digit-by-digit form (the
        // ouroboros harness consistently mistranscribed "two zero zero knots" as "18.20").
        var result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(speed));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(213, "maintain two one three knots")]
    [InlineData(247, "maintain two four seven knots")]
    public void Verbalize_Speed_NonRoundKnots_FallsBackToDigits(int speed, string expected)
    {
        // Speeds not on a 10-knot boundary aren't real pilot-readback values, but if the system
        // is ever asked to verbalize one, fall back to digit-by-digit rather than guess at a
        // colloquial form.
        var result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(speed));
        Assert.Equal(expected, result);
    }

    // --- Pattern entry: runway-bearing form preferred when command carries runway (issue #154 #6) ---

    [Fact]
    public void Verbalize_EnterRightDownwind_WithRunway_IncludesRunwayPhrase()
    {
        var result = PhraseologyVerbalizer.Verbalize(new EnterRightDownwindCommand("28R"));
        Assert.Equal("enter right downwind runway two eight right", result);
    }

    [Fact]
    public void Verbalize_EnterRightDownwind_NoRunway_FallsBackToBareForm()
    {
        var result = PhraseologyVerbalizer.Verbalize(new EnterRightDownwindCommand());
        Assert.Equal("enter right downwind", result);
    }

    [Fact]
    public void Verbalize_EnterLeftDownwind_WithRunway_IncludesRunwayPhrase()
    {
        var result = PhraseologyVerbalizer.Verbalize(new EnterLeftDownwindCommand("10L"));
        Assert.Equal("enter left downwind runway one zero left", result);
    }

    [Fact]
    public void Verbalize_EnterLeftBase_WithRunway_IncludesRunwayPhrase()
    {
        var result = PhraseologyVerbalizer.Verbalize(new EnterLeftBaseCommand("28R"));
        Assert.Equal("enter left base runway two eight right", result);
    }

    [Fact]
    public void Verbalize_EnterRightBase_WithRunway_IncludesRunwayPhrase()
    {
        var result = PhraseologyVerbalizer.Verbalize(new EnterRightBaseCommand("28R"));
        Assert.Equal("enter right base runway two eight right", result);
    }

    [Fact]
    public void Verbalize_MakeLeftTraffic_WithRunway_IncludesRunwayPhrase()
    {
        var result = PhraseologyVerbalizer.Verbalize(new MakeLeftTrafficCommand("28R", null));
        Assert.Equal("make left traffic runway two eight right", result);
    }

    [Fact]
    public void Verbalize_MakeRightTraffic_WithRunway_IncludesRunwayPhrase()
    {
        var result = PhraseologyVerbalizer.Verbalize(new MakeRightTrafficCommand("28R", null));
        Assert.Equal("make right traffic runway two eight right", result);
    }

    // --- SttOnly rules must NOT leak into pilot speech ---

    [Fact]
    public void Verbalize_DescendMaintain_PrefersDescendOverSttOnlyDescentAlias()
    {
        // PhraseologyRules.AltitudeSpeedRules() declares a "descent and maintain {alt}" alias
        // flagged SttOnly to recover Whisper mistranscriptions of "descend" as "descent".
        // The verbalizer must skip SttOnly rules so the pilot AI never speaks "descent and maintain"
        // — that would be grammatically wrong and a regression of the canonical readback.
        var result = PhraseologyVerbalizer.Verbalize(new DescendMaintainCommand(3000));
        Assert.Equal("descend and maintain three thousand", result);
    }
}
