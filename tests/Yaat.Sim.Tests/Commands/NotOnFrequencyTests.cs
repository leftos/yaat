using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// In solo training, a student instruction to a pilot tuned to another position is not heard: nothing applies, no
/// read-back or "unable" is queued, the result fails with no message, and the instructor sees a warning that the
/// aircraft is not on the student's frequency, at once, paused or not. "The student" is the host's issue-time verdict
/// carried on the action (<see cref="ActionInput.IssuedByStudent"/>) and recorded with it; an instructor on another
/// account, the standalone engine (no verdict), AI-controller commands and RPO rooms are always heard. The verbatim
/// <c>SAY</c> broadcast, track, flight-plan and sim-control entries are never refused.
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
    /// The aircraft on final at OAK, the student working OAK_TWR, no reaction delay, the pilot tuned to
    /// <paramref name="tuned"/> (null: on the student's side), and every warning terminal entry the engine emits collected
    /// in <see cref="Run.Warnings"/>.
    /// </summary>
    private Run Engine(TrackOwner? tuned, bool solo = true)
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
        var warnings = new List<string>();
        engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Kind == "Warning")
            {
                warnings.Add(entry.Message);
            }
        };
        return new Run(engine, aircraft, warnings);
    }

    private sealed record Run(SimulationEngine Engine, AircraftState Aircraft, List<string> Warnings);

    /// <summary>The command as a hub connection the host judged to be (or not to be) the student.</summary>
    private static ActionOutcome Issue(SimulationEngine engine, string command, bool? issuedByStudent) =>
        engine.Actions.Issue(
            new ActionInput(AiTestFixture.Callsign, command, SimulationEngine.LocalConnectionId, Initials: "", Baked: null)
            {
                IssuedByStudent = issuedByStudent,
            }
        );

    private static string NotOnFrequency(string suffix = " (on NCT_4U)") => $"{AiTestFixture.Callsign} is not on your frequency{suffix}";

    /// <summary>
    /// The refusal: a failed result with no message, the one warning delivered at once (nothing left pending for a
    /// tick to drain), and no pilot transmission or deferral queued.
    /// </summary>
    private static void AssertNotHeard(CommandResult result, Run run, string warning)
    {
        Assert.False(result.Success);
        Assert.Null(result.Message);
        Assert.Equal(warning, Assert.Single(run.Warnings));
        Assert.Empty(run.Aircraft.PendingWarnings);
        Assert.Empty(run.Aircraft.PendingPilotTransmissions);
        Assert.Empty(run.Aircraft.DeferredDispatches);
    }

    private static void AssertNoRefusal(Run run)
    {
        Assert.DoesNotContain(run.Warnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Aircraft.PendingWarnings, w => w.Contains("is not on your frequency", StringComparison.Ordinal));
    }

    [Fact]
    public void StudentHeading_ToAPilotTunedElsewhere_IsNotApplied()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        AssertNotHeard(result, run, NotOnFrequency());
        AiTestFixture.Tick(run.Engine, 5);
        Assert.Null(run.Aircraft.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void StudentContact_ToAPilotTunedElsewhere_IsNotApplied()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "CT OAK_GND");

        AssertNotHeard(result, run, NotOnFrequency());
        Assert.Same(Nct4U, run.Aircraft.TunedPosition);
    }

    [Fact]
    public void StudentSayAltitude_ToAPilotTunedElsewhere_IsNotApplied()
    {
        Run run = Engine(Nct4U);
        List<TerminalEntry> entries = [];
        run.Engine.TerminalEntryEmitted += entries.Add;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "SALT");

        AssertNotHeard(result, run, NotOnFrequency());
        Assert.DoesNotContain(entries, e => e.Kind == "SayAltitude");
    }

    [Fact]
    public void StudentRoger_ToAPilotTunedElsewhere_IsNotApplied()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "ROGER");

        AssertNotHeard(result, run, NotOnFrequency());
    }

    [Fact]
    public void StudentSquawk_ToAPilotTunedElsewhere_IsNotApplied()
    {
        Run run = Engine(Nct4U);
        uint before = run.Aircraft.Transponder.AssignedCode;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "SQ 4321");

        AssertNotHeard(result, run, NotOnFrequency());
        Assert.Equal(before, run.Aircraft.Transponder.AssignedCode);
    }

    /// <summary>The advisories and the IFR cancellation are radio instructions a tuned-away pilot does not hear.</summary>
    [Theory]
    [InlineData("RFIS")]
    [InlineData("RTIS")]
    [InlineData("SAFAL 12 2")]
    [InlineData("CWT")]
    [InlineData("CIFR")]
    public void StudentAdvisory_ToAPilotTunedElsewhere_IsNotApplied(string command)
    {
        Run run = Engine(Nct4U);
        string rulesBefore = run.Aircraft.FlightPlan.FlightRules;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, command);

        AssertNotHeard(result, run, NotOnFrequency());
        Assert.Equal(rulesBefore, run.Aircraft.FlightPlan.FlightRules);
    }

    [Fact]
    public void TunedToElsewhere_WarningNamesNoPosition()
    {
        Run run = Engine(TrackOwner.Elsewhere);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        AssertNotHeard(result, run, NotOnFrequency(suffix: ""));
    }

    /// <summary>Tower-cab positions share a TCP: a pilot tuned to ground is away from a tower student (callsign, not TCP).</summary>
    [Fact]
    public void TunedToAGroundSharingTheTowersTcp_IsAwayFromTheTowerStudent()
    {
        Run run = Engine(TrackOwner.CreateStars("OAK_GND", "OAK", 3, "T"));

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        AssertNotHeard(result, run, NotOnFrequency(" (on OAK_GND)"));
    }

    /// <summary>A compound with one radio instruction is refused whole: the sim-control half does not apply either.</summary>
    [Fact]
    public void StudentMixedCompound_ToAPilotTunedElsewhere_IsRefusedWhole()
    {
        Run run = Engine(Nct4U);
        double headingBefore = run.Aircraft.TrueHeading.Degrees;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FHN 090, SQ 4321");

        AssertNotHeard(result, run, NotOnFrequency());
        Assert.Equal(headingBefore, run.Aircraft.TrueHeading.Degrees);
    }

    /// <summary>The refusal's warning reaches the instructor at once while the sim is paused, not at the next tick.</summary>
    [Fact]
    public void Refusal_WhilePaused_DeliversTheWarningAtOnce()
    {
        Run run = Engine(Nct4U);
        run.Engine.Scenario!.IsPaused = true;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        AssertNotHeard(result, run, NotOnFrequency());
    }

    /// <summary>The record as a recording stores it and reads it back: through the recording's JSON.</summary>
    private static RecordedCommand ThroughTheRecordingJson(RecordedCommand record)
    {
        string json = JsonSerializer.Serialize<RecordedAction>(record, RecordingJsonOptions.Default);
        return Assert.IsType<RecordedCommand>(JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default));
    }

    /// <summary>A recorded refusal carries the student verdict, and its replay is refused again with the same warning.</summary>
    [Fact]
    public void RecordedRefusal_ReplaysRefusedWithTheSameWarning()
    {
        Run live = Engine(Nct4U);
        ActionOutcome issued = Issue(live.Engine, "FH 270", issuedByStudent: true);
        RecordedCommand record = ThroughTheRecordingJson(issued.ToRecord ?? throw new InvalidOperationException("the refusal was not recorded"));
        Assert.True(record.IssuedByStudent);
        Assert.False(record.Accepted);

        Run replay = Engine(Nct4U);
        CommandResult replayed = replay.Engine.Actions.Apply(record).Result;

        AssertNotHeard(replayed, replay, NotOnFrequency());
        Assert.Null(replay.Aircraft.Targets.AssignedMagneticHeading);
    }

    /// <summary>A recording made before the verdict existed stores none: its command replays heard by a tuned-away pilot.</summary>
    [Fact]
    public void OldRecording_WithNoVerdict_ReplaysHeard()
    {
        Run run = Engine(Nct4U);
        var old = new RecordedCommand(0, AiTestFixture.Callsign, "FH 270", "", SimulationEngine.LocalConnectionId) { Accepted = true };
        RecordedCommand record = ThroughTheRecordingJson(old);
        Assert.Null(record.IssuedByStudent);

        CommandResult replayed = run.Engine.Actions.Apply(record).Result;

        Assert.True(replayed.Success, replayed.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    /// <summary>A say again to a pilot tuned elsewhere answers in the refusal's own words.</summary>
    [Fact]
    public void SayAgain_ToAPilotTunedElsewhere_AnswersTheRefusalsText()
    {
        Run run = Engine(Nct4U);
        AssertNotHeard(run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270"), run, NotOnFrequency());

        CommandResult again = run.Engine.SendCommand(AiTestFixture.Callsign, "AGAIN");

        Assert.False(again.Success);
        Assert.Equal(Assert.Single(run.Warnings), again.Message);
    }

    [Fact]
    public void InstructorOnAnotherAccount_ToAPilotTunedElsewhere_IsHeard()
    {
        Run run = Engine(Nct4U);

        CommandResult result = Issue(run.Engine, "FH 270", issuedByStudent: false).Result;

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    /// <summary>The standalone engine (and a recording made before the verdict existed) carries no verdict: heard.</summary>
    [Fact]
    public void NoVerdict_ToAPilotTunedElsewhere_IsHeard()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.SendCommand(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    [Fact]
    public void VerbatimSay_ToAPilotTunedElsewhere_StillBroadcasts()
    {
        Run run = Engine(Nct4U);
        List<TerminalEntry> entries = [];
        run.Engine.TerminalEntryEmitted += entries.Add;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "SAY request higher");

        Assert.True(result.Success, result.Message);
        Assert.Contains(entries, e => (e.Kind == "Say") && e.Message.Contains("request higher", StringComparison.OrdinalIgnoreCase));
        AssertNoRefusal(run);
    }

    [Fact]
    public void StudentTrackEntry_ToAPilotTunedElsewhere_StillApplies()
    {
        Run run = Engine(Nct4U);
        run.Aircraft.Track.Owner = Nct4U;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "HO");

        Assert.True(result.Success, result.Message);
        Assert.Same(TowerStudent, run.Aircraft.Track.HandoffPeer);
        AssertNoRefusal(run);
    }

    /// <summary>A sim-control entry (the instant heading) is not a radio instruction: it applies to a tuned-away pilot.</summary>
    [Fact]
    public void StudentSimControl_ToAPilotTunedElsewhere_StillApplies()
    {
        Run run = Engine(Nct4U);
        double headingBefore = run.Aircraft.TrueHeading.Degrees;

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FHN 090");

        Assert.True(result.Success, result.Message);
        Assert.NotEqual(headingBefore, run.Aircraft.TrueHeading.Degrees);
        AssertNoRefusal(run);
    }

    /// <summary>A destination change is a flight-plan edit, not a radio instruction.</summary>
    [Fact]
    public void StudentDestinationChange_ToAPilotTunedElsewhere_StillApplies()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "APT SFO");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSFO", run.Aircraft.FlightPlan.Destination);
        AssertNoRefusal(run);
    }

    /// <summary>A command the dispatcher does not support yet keeps the dispatcher's own error; the predicate does not throw.</summary>
    [Fact]
    public void StudentUnsupportedCommand_ToAPilotTunedElsewhere_AnswersTheDispatchersError()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "PS X");

        Assert.False(result.Success);
        Assert.Equal("Command not yet supported: PS X", result.Message);
        AssertNoRefusal(run);
    }

    [Fact]
    public void AiInstruction_ToAPilotTunedElsewhere_StillApplies()
    {
        Run run = Engine(Nct4U);

        CommandResult result = run.Engine.DispatchAiCommand(TestAiPositions.OakGround(Zoa), AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    [Fact]
    public void RpoRoom_Instruction_ToATunedAwayPilot_StillApplies()
    {
        Run run = Engine(Nct4U, solo: false);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    [Fact]
    public void StudentHeading_ToAPilotOnTheStudentsSide_Applies()
    {
        Run run = Engine(tuned: null);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    /// <summary>
    /// The refusal is decided at issue time: an instruction the pilot heard and is holding for its reaction delay still
    /// fires after a later CT tunes the pilot away.
    /// </summary>
    [Fact]
    public void StudentHeading_DeferredByTheReactionDelay_FiresAfterAContactTunesThePilotAway()
    {
        Run run = Engine(tuned: null);
        run.Engine.Scenario!.CommandRunDelayMinSeconds = 4;
        run.Engine.Scenario.CommandRunDelayMaxSeconds = 4;

        CommandResult heading = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");
        Assert.True(heading.Success, heading.Message);
        Assert.NotEmpty(run.Aircraft.DeferredDispatches);
        CommandResult contact = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "CT OAK_GND");
        Assert.True(contact.Success, contact.Message);
        Assert.True(run.Aircraft.IsTunedAwayFrom(TowerStudent));

        AiTestFixture.Tick(run.Engine, 10);

        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }

    [Fact]
    public void StudentHeading_AfterAcceptingTheHandoff_Applies()
    {
        Run run = Engine(Nct4U);
        run.Aircraft.Track.Owner = Nct4U;
        Assert.True(run.Engine.IssueAsStudent(AiTestFixture.Callsign, "HO").Success);
        CommandResult accepted = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "ACCEPT");
        Assert.True(accepted.Success, accepted.Message);
        Assert.Null(run.Aircraft.TunedPosition);

        CommandResult result = run.Engine.IssueAsStudent(AiTestFixture.Callsign, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(270.0, run.Aircraft.Targets.AssignedMagneticHeading?.Degrees);
        AssertNoRefusal(run);
    }
}
