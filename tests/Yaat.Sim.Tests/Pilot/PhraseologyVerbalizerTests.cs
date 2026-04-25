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

    // --- Speed ---

    [Fact]
    public void Verbalize_Speed_SpellsKnotsLiteral()
    {
        // Rule pattern is ["maintain", "{spd}", "knots"] (first declared in AltitudeSpeedRules).
        var result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(250));
        Assert.Equal("maintain two five zero knots", result);
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
}
