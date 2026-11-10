using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A pilot's initial call to the solo student puts it on the student's frequency (<see cref="AircraftState.TunedPosition"/>
/// null): the transfer of communications, not the accept, moves the pilot (7110.65 2-1-17.a, 5-4-5.i). ZOA lets an
/// approach-owned arrival call the tower once the handoff is initiated, and NCT's arrivals call SFO Tower with no handoff at
/// all; either way the student's next instruction is heard.
/// </summary>
public class CheckInRetuneTests
{
    private const string Callsign = "N731RT";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public CheckInRetuneTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private ArtccConfigRoot Zoa
    {
        get
        {
            if (_zoa is null)
            {
                Assert.Skip("The ZOA ARTCC config is not available");
            }

            return _zoa;
        }
    }

    private TrackOwner Position(string callsign) =>
        Zoa.ResolvePosition(Zoa.FindPositionByCallsign(callsign, null)!.Id) ?? throw new InvalidOperationException($"{callsign} does not resolve");

    /// <summary>One VFR arrival to <paramref name="airport"/>, airborne at the given point, inbound to the field.</summary>
    private static string ArrivalScenario(string airport, string lat, string lon) =>
        $$"""
            {
              "id": "check-in-retune",
              "name": "Check-in retune",
              "artccId": "ZOA",
              "primaryAirportId": "{{airport}}",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{Callsign}}",
                  "aircraftType": "C172",
                  "transponderMode": "C",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": { "lat": {{lat}}, "lon": {{lon}} },
                    "altitude": 3000,
                    "heading": 125,
                    "speed": 110,
                    "navigationPath": "{{airport}}"
                  },
                  "onAltitudeProfile": false,
                  "flightplan": {
                    "rules": "VFR", "departure": "KAPC", "destination": "K{{airport}}", "cruiseAltitude": 3000, "cruiseSpeed": 110,
                    "route": "", "remarks": "", "aircraftType": "C172"
                  },
                  "presetCommands": [],
                  "spawnDelay": 0
                }
              ]
            }
            """;

    /// <summary>
    /// The solo tower student at <paramref name="studentCallsign"/>, the arrival tracked by <paramref name="approach"/> and
    /// tuned to it, as an airborne spawn owned by approach is, with no reaction delay.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft, TrackOwner Student) Engine(
        string scenarioJson,
        string studentCallsign,
        TrackOwner approach
    )
    {
        SimulationEngine engine = AiTestFixture.Load(scenarioJson, Zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        TrackOwner student = Position(studentCallsign);
        scenario.StudentPosition = student;
        scenario.StudentPositionType = "TWR";
        scenario.SoloTrainingMode = true;
        scenario.CommandRunDelayMinSeconds = 0;
        scenario.CommandRunDelayMaxSeconds = 0;
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        aircraft.Track.Owner = approach;
        aircraft.TunedPosition = approach;
        Assert.False(aircraft.HasMadeInitialContact);
        return (engine, aircraft, student);
    }

    /// <summary>A command from a hub connection the host judged to be the student.</summary>
    private static CommandResult Student(SimulationEngine engine, string command) =>
        engine
            .Actions.Issue(
                new ActionInput(Callsign, command, SimulationEngine.LocalConnectionId, Initials: "", Baked: null) { IssuedByStudent = true }
            )
            .Result;

    [Fact]
    public void ApproachArrival_ChecksInWithTheTowerOnceTheHandoffStarts_IsRetunedAndHeard()
    {
        TrackOwner approach = Position("OAK_APP");
        (SimulationEngine engine, AircraftState aircraft, TrackOwner student) = Engine(
            ArrivalScenario("OAK", "37.83", "-122.42"),
            "OAK_TWR",
            approach
        );
        AiTestFixture.Tick(engine, 3);
        Assert.False(aircraft.HasMadeInitialContact, "under ZOA's approach-to-tower rule the pilot waits for the handoff");

        aircraft.Track.HandoffPeer = student;
        aircraft.Track.HandoffInitiatedAt = engine.Scenario!.ElapsedSeconds;
        AiTestFixture.TickUntil(engine, Callsign, a => a.HasMadeInitialContact, 10);

        Assert.Same(approach, aircraft.Track.Owner);
        Assert.Null(aircraft.TunedPosition);
        aircraft.PendingPilotTransmissions.Clear();
        List<TerminalEntry> warnings = [];
        engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Kind == "Warning")
            {
                warnings.Add(entry);
            }
        };

        CommandResult landing = Student(engine, "CTL 28R");

        Assert.True(landing.Success, landing.Message);
        Assert.NotEmpty(aircraft.PendingPilotTransmissions);
        Assert.Empty(warnings);
    }

    [Fact]
    public void SfoArrival_ChecksInWithTheTowerWithNoHandoff_IsRetunedAndHeard()
    {
        TrackOwner approach = Position("SFO_B_APP");
        (SimulationEngine engine, AircraftState aircraft, _) = Engine(ArrivalScenario("SFO", "37.70", "-122.50"), "SFO_TWR", approach);

        AiTestFixture.TickUntil(engine, Callsign, a => a.HasMadeInitialContact, 10);

        Assert.Same(approach, aircraft.Track.Owner);
        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.Null(aircraft.TunedPosition);
        CommandResult heading = Student(engine, "FH 270");
        Assert.True(heading.Success, heading.Message);
        Assert.Equal(270.0, aircraft.Targets.AssignedMagneticHeading?.Degrees);
    }
}
