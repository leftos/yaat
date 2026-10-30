using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A visual clearance and the expected approach: <c>CVA</c> replaces the expectation with the visual it clears, a bare
/// <c>CAPP</c> after <c>EAPP VIS{rwy}</c> clears that visual as <c>CVA {rwy}</c> would, and a bare <c>JFAC</c> has no
/// final approach course to join for an expected visual.
/// </summary>
public class CvaExpectedVisualTests
{
    public CvaExpectedVisualTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // Five miles east of KOAK 28R, heading for it: a straight-in visual join.
    private static AircraftState MakeArrival() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(295),
            Altitude = 3000,
            IndicatedAirspeed = 180,
            Position = new LatLon(37.735, -122.12),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    private static CommandResult Dispatch(ParsedCommand command, AircraftState aircraft) =>
        CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(new Random(1)));

    private static ParsedCommand Parse(string text) => CommandParser.Parse(text).Value!;

    private static CommandResult DispatchTowered(string text, AircraftState aircraft) =>
        CommandDispatcher.Dispatch(
            Parse(text),
            aircraft,
            TestDispatch.Context(
                new Random(1),
                toweredAirports: ToweredAirports.From([TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing")])
            )
        );

    /// <summary>
    /// KOAK is towered, so an airport-level visual needs a runway (7110.65 7-4-3.c.4); with the visual to 28R expected, the
    /// runway is already named, and a bare <c>CVA</c> clears that visual as bare <c>CAPP</c> does.
    /// </summary>
    [Fact]
    public void BareCva_AtATowerField_WithAVisualExpected_ClearsThatVisual()
    {
        AircraftState aircraft = MakeArrival();
        aircraft.Approach.HasReportedFieldInSight = true;
        Assert.True(DispatchTowered("EAPP VIS28R", aircraft).Success);

        CommandResult result = DispatchTowered("CVA", aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared visual approach runway 28R", result.Message);
        Assert.Equal("VIS28R", aircraft.Phases?.ActiveApproach?.ApproachId);
        Assert.NotNull(result.PilotReadback);
        Assert.Equal(CanonicalCommandType.ClearedVisualApproach, result.PilotReadback.Command);
        Assert.Equal("cleared visual approach runway two eight right", result.PilotReadback.Speech.Tts);
    }

    /// <summary>
    /// The runway form reads back as 7110.65 7-4-3.c.4 phrases it ("cleared visual approach runway two eight right"), the
    /// pattern side and the traffic to follow after it in the order the grammar takes them; the RPO's terminal names the
    /// traffic's callsign, the student's does not.
    /// </summary>
    [Theory]
    [InlineData("CVA 28R", "", "", "")]
    [InlineData("CVA 28R LEFT", ", left traffic", "", "")]
    [InlineData("CVA 28R RIGHT FOLLOW N456", ", right traffic", ", follow the traffic", ", follow N456")]
    public void RunwayForm_ReadsBackTheRunway(string command, string traffic, string follow, string rpoFollow)
    {
        AircraftState aircraft = MakeArrival();
        aircraft.Approach.HasReportedFieldInSight = true;
        aircraft.Approach.HasReportedTrafficInSight = true;
        aircraft.Approach.LastReportedTrafficCallsign = "N456";

        CommandResult result = DispatchTowered(command, aircraft);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.PilotReadback);
        Assert.Equal(CanonicalCommandType.ClearedVisualApproach, result.PilotReadback.Command);
        Assert.Equal($"cleared visual approach runway 28R{traffic}{follow}", result.PilotReadback.Speech.Terminal);
        Assert.Equal($"cleared visual approach runway two eight right{traffic}{follow}", result.PilotReadback.Speech.Tts);
        Assert.Equal($"cleared visual approach runway 28R{traffic}{rpoFollow}", result.PilotReadback.Speech.TerminalForRpo);
    }

    /// <summary>
    /// A <c>CVA {rwy}</c> in a later block carries no handler readback; the verbalizer reads its clause back from the
    /// 7110.65 7-4-3.c.4 rule ("cleared visual approach runway two eight right").
    /// </summary>
    [Fact]
    public void RunwayForm_InALaterBlock_ReadsBackTheVisualClause()
    {
        AircraftState aircraft = MakeArrival();
        CompoundCommand compound = CommandParser.ParseCompound("DM 30; CVA 28R").Value!;

        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            compound,
            new CommandResult(true, "queued"),
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(readback);
        Assert.Contains("cleared visual approach runway two eight right", readback.Tts);
    }

    /// <summary>
    /// A bare <c>CAPP</c> that clears the expected visual reads back the visual clearance, never "cleared approach": a visual
    /// is no instrument approach (7110.65 4-8-1.a, 7-4-1). The readback replaces the <c>CAPP</c> clause the controller issued.
    /// </summary>
    [Fact]
    public void BareCapp_WithAVisualExpected_ReadsBackTheVisual()
    {
        AircraftState aircraft = MakeArrival();
        aircraft.Approach.HasReportedFieldInSight = true;
        Assert.True(Dispatch(Parse("EAPP VIS28R"), aircraft).Success);
        ParsedCommand capp = Parse("CAPP");

        CommandResult result = Dispatch(capp, aircraft);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.PilotReadback);
        Assert.Equal(CanonicalCommandType.ClearedApproach, result.PilotReadback.Command);
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            new CompoundCommand([new ParsedBlock(null, [capp])]),
            result,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        Assert.NotNull(readback);
        Assert.StartsWith("cleared visual approach runway two eight right", readback.Tts);
    }

    [Fact]
    public void Cva_ReplacesTheExpectedApproachWithTheVisual()
    {
        AircraftState aircraft = MakeArrival();
        aircraft.Approach.Expected = "I28R";
        aircraft.Approach.HasReportedFieldInSight = true;

        CommandResult result = Dispatch(Parse("CVA 28R"), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal("VIS28R", aircraft.Approach.Expected);
        Assert.Equal("VIS28R", aircraft.Phases?.ActiveApproach?.ApproachId);
    }

    [Fact]
    public void BareCapp_WithAVisualExpected_ClearsThatVisual()
    {
        AircraftState aircraft = MakeArrival();
        aircraft.Approach.HasReportedFieldInSight = true;
        Assert.True(Dispatch(Parse("EAPP VIS28R"), aircraft).Success);

        CommandResult result = Dispatch(Parse("CAPP"), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared visual approach runway 28R", result.Message);
        Assert.Equal("VIS28R", aircraft.Phases?.ActiveApproach?.ApproachId);
        Assert.Null(aircraft.Phases?.ActiveApproach?.Procedure);
    }

    [Fact]
    public void BareCapp_WithAVisualExpected_KeepsTheFieldInSightGate()
    {
        AircraftState aircraft = MakeArrival();
        Assert.True(Dispatch(Parse("EAPP VIS28R"), aircraft).Success);

        CommandResult result = Dispatch(Parse("CAPP"), aircraft);

        Assert.False(result.Success);
        Assert.Equal("Field not in sight — issue RFIS first", result.Message);
    }

    [Fact]
    public void BareJfac_WithAVisualExpected_IsRefused()
    {
        AircraftState aircraft = MakeArrival();
        Assert.True(Dispatch(Parse("EAPP VIS28R"), aircraft).Success);

        CommandResult result = Dispatch(Parse("JFAC"), aircraft);

        Assert.False(result.Success);
        Assert.Equal("Unable, expecting a visual approach; clear it with CVA", result.Message);
    }
}
