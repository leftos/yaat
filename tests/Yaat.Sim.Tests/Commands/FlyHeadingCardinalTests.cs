using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The VFR cardinals of <c>FH</c>: <c>FH E</c> flies what <c>FH 090</c> flies but keeps the controller's own
/// word, so the readback says "proceeding eastbound" and an IFR aircraft is refused with the numeric form.
/// </summary>
public class FlyHeadingCardinalTests
{
    public FlyHeadingCardinalTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState CreateAircraft()
    {
        return new AircraftState
        {
            Callsign = "N347VR",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 5000,
            Position = new LatLon(37.0, -122.0),
            Declination = 0,
        };
    }

    private static FlyHeadingCommand ParseFlyHeading(string text) => Assert.IsType<FlyHeadingCommand>(CommandParser.Parse(text).Value);

    [Theory]
    [InlineData("FH N", "N", 360)]
    [InlineData("FH NE", "NE", 45)]
    [InlineData("FH E", "E", 90)]
    [InlineData("FH SE", "SE", 135)]
    [InlineData("FH S", "S", 180)]
    [InlineData("FH SW", "SW", 225)]
    [InlineData("FH W", "W", 270)]
    [InlineData("FH NW", "NW", 315)]
    public void Parse_FhCardinal_MapsToHeading(string text, string cardinal, int heading)
    {
        FlyHeadingCommand cmd = ParseFlyHeading(text);

        Assert.Equal(cardinal, cmd.Cardinal);
        Assert.Equal(heading, cmd.MagneticHeading.ToDisplayInt());
    }

    [Fact]
    public void Parse_HAliasCardinal_MapsToHeading()
    {
        FlyHeadingCommand cmd = ParseFlyHeading("H NW");

        Assert.Equal("NW", cmd.Cardinal);
        Assert.Equal(315, cmd.MagneticHeading.ToDisplayInt());
    }

    [Fact]
    public void Parse_FhCardinalThenVerb_Splits()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("FH E CM 5000", CommandScheme.Default());

        Assert.NotNull(result);
        Assert.Equal("FH E, CM 5000", result!.CanonicalString);
    }

    [Fact]
    public void Parse_FhConcatenatedCardinal_StaysInvalid()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("FHE");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Parse_FhNonCardinalLetter_StaysInvalid()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("FH X");

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid heading 'X' (expected 001-360)", result.Reason);
    }

    [Fact]
    public void Canonical_FhCardinal_RoundTrips()
    {
        FlyHeadingCommand cmd = ParseFlyHeading("FH E");

        string canonical = CommandDescriber.DescribeCommand(cmd);
        FlyHeadingCommand reparsed = ParseFlyHeading(canonical);

        Assert.Equal("FH E", canonical);
        Assert.Equal(cmd.Cardinal, reparsed.Cardinal);
        Assert.Equal(cmd.MagneticHeading, reparsed.MagneticHeading);
    }

    [Fact]
    public void Dispatch_FhCardinal_Ifr_IsRefused()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.FlightPlan.FlightRules = "IFR";
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(180);
        aircraft.Targets.TargetTrueHeading = new TrueHeading(180);

        CommandResult result = CommandDispatcher.Dispatch(ParseFlyHeading("FH E"), aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Equal("FH E is VFR only; use FH 090", result.Message);
        Assert.Equal(new MagneticHeading(180), aircraft.Targets.AssignedMagneticHeading);
        Assert.Equal(new TrueHeading(180), aircraft.Targets.TargetTrueHeading);
    }

    [Fact]
    public void Dispatch_FhCardinal_Vfr_FliesHeading()
    {
        AircraftState cardinal = CreateAircraft();
        cardinal.FlightPlan.FlightRules = "VFR";
        AircraftState numeric = CreateAircraft();
        numeric.FlightPlan.FlightRules = "VFR";

        CommandResult cardinalResult = CommandDispatcher.Dispatch(ParseFlyHeading("FH E"), cardinal, TestDispatch.Context(Random.Shared));
        CommandResult numericResult = CommandDispatcher.Dispatch(
            new FlyHeadingCommand(new MagneticHeading(90)),
            numeric,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(cardinalResult.Success);
        Assert.True(numericResult.Success);
        Assert.Equal(new MagneticHeading(90), cardinal.Targets.AssignedMagneticHeading);
        Assert.Equal(numeric.Targets.AssignedMagneticHeading, cardinal.Targets.AssignedMagneticHeading);
        Assert.Equal(numeric.Targets.TargetTrueHeading, cardinal.Targets.TargetTrueHeading);
    }

    [Fact]
    public void Dispatch_FhCardinal_Vfr_ReportsProceedDirection()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.FlightPlan.FlightRules = "VFR";

        CommandResult result = CommandDispatcher.Dispatch(ParseFlyHeading("FH E"), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("Proceed eastbound (heading 090)", result.Message);
    }

    [Theory]
    [InlineData("FH N", "proceeding northbound")]
    [InlineData("FH NE", "proceeding northeastbound")]
    [InlineData("FH E", "proceeding eastbound")]
    [InlineData("FH SE", "proceeding southeastbound")]
    [InlineData("FH S", "proceeding southbound")]
    [InlineData("FH SW", "proceeding southwestbound")]
    [InlineData("FH W", "proceeding westbound")]
    [InlineData("FH NW", "proceeding northwestbound")]
    public void Readback_FhCardinal_SaysProceedingDirection(string text, string expected)
    {
        string? readback = PhraseologyVerbalizer.Verbalize(ParseFlyHeading(text));

        Assert.Equal(expected, readback);
    }
}
