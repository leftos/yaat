using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// In solo training, a student instruction to a pilot tuned to another position is not heard: nothing applies, no
/// read-back or "unable" is queued, the result fails with no message, and the instructor sees a pending warning that
/// the aircraft is not on the student's frequency. The verbatim <c>SAY</c> broadcast, track entries, AI-controller
/// commands and RPO rooms are unaffected.
/// </summary>
public class NotOnFrequencyTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public NotOnFrequencyTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static readonly TrackOwner TowerStudent = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");
    private static readonly Tcp TowerTcp = new(3, "T", "tcp-oak-twr", null);

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

    /// <summary>
    /// The aircraft on final at OAK, the student working OAK_TWR, no reaction delay, and the pilot tuned to
    /// <paramref name="tuned"/> (null: on the student's side).
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft) Engine(TrackOwner? tuned, bool solo = true)
    {
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, Zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = TowerStudent;
        scenario.StudentTcp = TowerTcp;
        scenario.SoloTrainingMode = solo;
        scenario.CommandRunDelayMinSeconds = 0;
        scenario.CommandRunDelayMaxSeconds = 0;
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        aircraft.TunedPosition = tuned;
        aircraft.PendingPilotTransmissions.Clear();
        aircraft.PendingWarnings.Clear();
        return (engine, aircraft);
    }

    private static string NotOnFrequency(string suffix = " (on NCT_4U)") => $"{AiTestFixture.Callsign} is not on your frequency{suffix}";

    /// <summary>The refusal: a failed result with no message, the one warning, and no pilot transmission queued.</summary>
    private static void AssertNotHeard(CommandResult result, AircraftState aircraft, string warning)
    {
        Assert.False(result.Success);
        Assert.Null(result.Message);
        Assert.Equal(warning, Assert.Single(aircraft.PendingWarnings));
        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Empty(aircraft.DeferredDispatches);
    }

    [Fact]
    public void StudentHeading_ToAPilotTunedElsewhere_IsNotApplied()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "FH 270");

        AssertNotHeard(result, aircraft, NotOnFrequency());
        AiTestFixture.Tick(engine, 5);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void StudentContact_ToAPilotTunedElsewhere_IsNotApplied()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "CT OAK_GND");

        AssertNotHeard(result, aircraft, NotOnFrequency());
        Assert.Same(Nct4U, aircraft.TunedPosition);
    }

    [Fact]
    public void StudentSayAltitude_ToAPilotTunedElsewhere_IsNotApplied()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);
        List<TerminalEntry> entries = [];
        engine.TerminalEntryEmitted += entries.Add;

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "SALT");

        AssertNotHeard(result, aircraft, NotOnFrequency());
        Assert.DoesNotContain(entries, e => e.Kind == "SayAltitude");
    }

    [Fact]
    public void StudentRoger_ToAPilotTunedElsewhere_IsNotApplied()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "ROGER");

        AssertNotHeard(result, aircraft, NotOnFrequency());
    }

    [Fact]
    public void StudentSquawk_ToAPilotTunedElsewhere_IsNotApplied()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);
        uint before = aircraft.Transponder.AssignedCode;

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "SQ 4321");

        AssertNotHeard(result, aircraft, NotOnFrequency());
        Assert.Equal(before, aircraft.Transponder.AssignedCode);
    }

    [Fact]
    public void TunedToElsewhere_WarningNamesNoPosition()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(TrackOwner.Elsewhere);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "FH 270");

        AssertNotHeard(result, aircraft, NotOnFrequency(suffix: ""));
    }

    [Fact]
    public void VerbatimSay_ToAPilotTunedElsewhere_StillBroadcasts()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);
        List<TerminalEntry> entries = [];
        engine.TerminalEntryEmitted += entries.Add;

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "SAY request higher");

        Assert.True(result.Success, result.Message);
        Assert.Contains(entries, e => (e.Kind == "Say") && e.Message.Contains("request higher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }

    [Fact]
    public void StudentTrackEntry_ToAPilotTunedElsewhere_StillApplies()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);
        aircraft.Track.Owner = Nct4U;

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "HO");

        Assert.True(result.Success, result.Message);
        Assert.Same(TowerStudent, aircraft.Track.HandoffPeer);
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }

    [Fact]
    public void AiInstruction_ToAPilotTunedElsewhere_StillApplies()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);

        CommandResult result = engine.DispatchAiCommand(TestAiPositions.OakGround(Zoa), AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, aircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }

    [Fact]
    public void RpoRoom_Instruction_ToATunedAwayPilot_StillApplies()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U, solo: false);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, aircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }

    [Fact]
    public void StudentHeading_ToAPilotOnTheStudentsSide_Applies()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(tuned: null);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, aircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }

    [Fact]
    public void StudentHeading_AfterAcceptingTheHandoff_Applies()
    {
        (SimulationEngine engine, AircraftState aircraft) = Engine(Nct4U);
        aircraft.Track.Owner = Nct4U;
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "HO").Success);
        CommandResult accepted = engine.SendCommand(AiTestFixture.Callsign, "ACCEPT");
        Assert.True(accepted.Success, accepted.Message);
        Assert.Null(aircraft.TunedPosition);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, aircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }
}
