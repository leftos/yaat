using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="AircraftState.TunedPosition"/>: the position the pilot is tuned to. A CT tunes it to the target, an FCA to
/// <see cref="TrackOwner.Elsewhere"/>; a human instruction to an aircraft with nothing recorded tunes it to the student's
/// position; an AI-controller instruction never does. "Left the student's frequency" is a tuned position that is not
/// the student's (<see cref="AircraftState.IsTunedAwayFrom"/>).
/// </summary>
public class TunedPositionTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TunedPositionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string StudentCallsign = "OAK_GND";

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

    /// <summary>The parked-at-OAK engine, the student working <paramref name="studentCallsign"/> (none when null).</summary>
    private (SimulationEngine Engine, AircraftState Aircraft, TrackOwner? Student) Engine(string? studentCallsign)
    {
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, Zoa, 7, []);
        TrackOwner? student = studentCallsign is null ? null : Position(studentCallsign);
        engine.Scenario!.StudentPosition = student;
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.Null(aircraft.TunedPosition);
        return (engine, aircraft, student);
    }

    [Fact]
    public void Contact_ToAResolvablePosition_TunesToItAndLeavesTheStudent()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CT OAK_TWR").Success);

        Assert.Equal(Position("OAK_TWR"), aircraft.TunedPosition);
        Assert.True(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void FrequencyChangeApproved_TunesElsewhere()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "FCA").Success);

        Assert.Equal(TrackOwner.Elsewhere, aircraft.TunedPosition);
        Assert.True(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void HumanInstruction_AfterSpawn_TunesToTheStudent()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "SQ 4321").Success);

        Assert.Equal(student, aircraft.TunedPosition);
        Assert.False(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void AiInstruction_DoesNotTune()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        CompoundCommand compound = CommandParser.ParseCompound("SQ 4321", aircraft.FlightPlan.Route).Value!;
        var capture = PreDispatchCapture.BeforeDispatch(aircraft);
        CommandResult result = CommandDispatcher.DispatchCompound(
            compound,
            aircraft,
            TestDispatch.Context(new Random(0), artccConfig: Zoa, isScenarioScripted: true)
        );

        engine.ApplyPostDispatch(aircraft, compound, result, DispatchOrigin.ControllerAi, capture);

        Assert.True(result.Success, result.Message);
        Assert.Null(aircraft.TunedPosition);
        Assert.False(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void StudentInstruction_AfterAContact_KeepsTheContactsPosition()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CT OAK_TWR").Success);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "SQ 4321").Success);

        Assert.Equal(Position("OAK_TWR"), aircraft.TunedPosition);
        Assert.True(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void HumanInstruction_WithNoStudentPosition_DoesNotTune()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(studentCallsign: null);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "SQ 4321").Success);

        Assert.Null(student);
        Assert.Null(aircraft.TunedPosition);
        Assert.False(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void QueuedContact_TunesOnceItFires()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        engine.Scenario!.SoloTrainingMode = true;

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "WAIT 5 CT OAK_TWR").Success);
        Assert.False(aircraft.IsTunedAwayFrom(student));

        AiTestFixture.Tick(engine, 10);

        Assert.Equal(Position("OAK_TWR"), aircraft.TunedPosition);
        Assert.True(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void ReactionDelayedContact_TunesAwayAtIssue()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        engine.Scenario!.SoloTrainingMode = true;
        engine.Scenario.CommandRunDelayMinSeconds = 10;
        engine.Scenario.CommandRunDelayMaxSeconds = 10;

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "FH 270, CT OAK_TWR").Success);
        Assert.NotEmpty(aircraft.DeferredDispatches);

        Assert.True(aircraft.IsTunedAwayFrom(student));
        CommandResult again = engine.SendCommand(AiTestFixture.Callsign, "AGAIN");
        Assert.False(again.Success);
        Assert.Contains("is no longer on your frequency", again.Message);
    }

    [Fact]
    public void RpoReactionDelayedContact_TunesAwayOnlyOnceItFires()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        engine.Scenario!.SoloTrainingMode = false;
        engine.Scenario.CommandRunDelayMinSeconds = 10;
        engine.Scenario.CommandRunDelayMaxSeconds = 10;

        // A squawk, which the parked aircraft accepts, so the whole compound applies when the delay runs out.
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "SQ 4321, CT OAK_TWR").Success);
        Assert.NotEmpty(aircraft.DeferredDispatches);

        Assert.False(aircraft.IsTunedAwayFrom(student));

        AiTestFixture.Tick(engine, 12);

        Assert.Empty(aircraft.DeferredDispatches);
        Assert.Equal(Position("OAK_TWR"), aircraft.TunedPosition);
        Assert.True(aircraft.IsTunedAwayFrom(student));
    }

    [Fact]
    public void DeferredSayAgain_AfterAStudentInstruction_IsAccepted()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        engine.Scenario!.SoloTrainingMode = true;
        List<TerminalEntry> entries = [];
        engine.TerminalEntryEmitted += entries.Add;
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "SQ 4321").Success);
        Assert.Equal(student, aircraft.TunedPosition);

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "WAIT 5 AGAIN").Success);
        Assert.NotEmpty(aircraft.DeferredDispatches);
        AiTestFixture.Tick(engine, 8);

        Assert.Empty(aircraft.DeferredDispatches);
        Assert.DoesNotContain(entries, e => e.Message.Contains("could not apply", StringComparison.Ordinal));
    }

    [Fact]
    public void RpoBareContact_BackToTheStudent_IsNotTunedAway()
    {
        (SimulationEngine engine, AircraftState aircraft, TrackOwner? student) = Engine(StudentCallsign);
        engine.Scenario!.SoloTrainingMode = false;
        aircraft.Track.HandoffPeer = student;

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CT").Success);

        Assert.Equal(student, aircraft.TunedPosition);
        Assert.False(aircraft.IsTunedAwayFrom(student));
    }
}
