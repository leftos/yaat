using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <c>EAPP VIS{rwy}</c>: the controller tells the pilot to expect a visual approach (7110.65 4-7-10.d). The expectation is
/// stored as <c>VIS{designator}</c> in the expected-approach slot and names the destination runway, resolved from the
/// navigation data rather than from a published procedure.
/// </summary>
public class ExpectVisualApproachTests
{
    public ExpectVisualApproachTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeArrival(string destination) =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(280),
            Altitude = 5000,
            Position = new LatLon(37.75, -122.35),
            FlightPlan = new AircraftFlightPlan { Destination = destination },
        };

    private static CommandResult Dispatch(ParsedCommand command, AircraftState aircraft) =>
        CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(new Random(1)));

    [Fact]
    public void EappVisual_SetsExpectedVisualAndDestinationRunway()
    {
        AircraftState aircraft = MakeArrival("OAK");

        CommandResult result = Dispatch(new ExpectApproachCommand("VIS28R", null), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal("VIS28R", aircraft.Approach.Expected);
        Assert.Equal("28R", aircraft.Procedure.DestinationRunway);
        Assert.Equal("Expecting visual approach runway 28R", result.Message);
    }

    [Fact]
    public void EappVisual_WithAirport_ResolvesAtThatAirport()
    {
        AircraftState aircraft = MakeArrival("SFO");

        CommandResult result = Dispatch(new ExpectApproachCommand("VIS28R", "KOAK"), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal("VIS28R", aircraft.Approach.Expected);
        Assert.Equal("28R", aircraft.Procedure.DestinationRunway);
    }

    [Fact]
    public void EappVisual_UnpaddedRunway_StoresTheNavDataDesignator()
    {
        AircraftState aircraft = MakeArrival("SFO");

        CommandResult result = Dispatch(new ExpectApproachCommand("VIS1R", null), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal("VIS01R", aircraft.Approach.Expected);
        Assert.Equal("01R", aircraft.Procedure.DestinationRunway);
        Assert.Equal("Expecting visual approach runway 1R", result.Message);
    }

    [Fact]
    public void EappVisual_UnknownRunway_IsRefusedAndLeavesTheExpectation()
    {
        AircraftState aircraft = MakeArrival("OAK");
        aircraft.Approach.Expected = "I28R";

        CommandResult result = Dispatch(new ExpectApproachCommand("VIS35", null), aircraft);

        Assert.False(result.Success);
        Assert.Equal("Unknown runway 35 at OAK", result.Message);
        Assert.Equal("I28R", aircraft.Approach.Expected);
    }

    [Fact]
    public void SayExpectedApproach_Visual_NamesTheVisualApproach()
    {
        AircraftState aircraft = MakeArrival("OAK");
        aircraft.Approach.Expected = "VIS28R";

        Assert.Equal("Expecting the visual approach runway 28R", PilotSayBuilder.BuildExpectedApproach(aircraft));
    }

    [Fact]
    public void Readback_Visual_SpeaksTheVisualApproachRunway() =>
        Assert.Equal("expect visual approach runway two eight right", PhraseologyVerbalizer.Verbalize(new ExpectApproachCommand("VIS28R", null)));

    [Theory]
    [InlineData("VIS28R", "expect visual approach runway 28R")]
    [InlineData("VIS01R", "expect visual approach runway 1R")]
    public void Readback_Visual_TerminalText(string approachId, string expected) =>
        Assert.Equal(expected, PhraseologyVerbalizer.VerbalizeTerminal(new ExpectApproachCommand(approachId, "OAK")));

    [Theory]
    [InlineData("VIS28R", "28R")]
    [InlineData("vis01r", "01R")]
    [InlineData("VIS9", "9")]
    public void TryParseVisual_ReadsTheRunway(string approachId, string runway)
    {
        Assert.True(ApproachIds.TryParseVisual(approachId, out string? parsed));
        Assert.Equal(runway, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("I28R")]
    [InlineData("V28R")]
    [InlineData("VIS")]
    [InlineData("VISA")]
    [InlineData("VIS28RX")]
    public void TryParseVisual_RejectsOtherIds(string? approachId) => Assert.False(ApproachIds.TryParseVisual(approachId, out _));
}
