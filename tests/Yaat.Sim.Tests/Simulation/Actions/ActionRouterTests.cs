using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Soak;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// <see cref="ActionRouter"/>: one routing for a fresh and a recorded command. The scope is resolved before the arm
/// runs (a global command applies with an empty callsign; an aircraft-scoped one refuses identically on every entry
/// point), the post-dispatch state a live command produced is produced on replay too, every fresh command is recorded
/// accepted or not, a recorded command is never re-recorded, and a replay whose verdict differs from live's says so.
/// </summary>
public class ActionRouterTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ActionRouterTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine BuildEngine(bool soloTrainingMode, int reactionDelaySeconds)
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                SoloTrainingMode = soloTrainingMode,
                CommandRunDelayMinSeconds = reactionDelaySeconds,
                CommandRunDelayMaxSeconds = reactionDelaySeconds,
            },
        };
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        return engine;
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, uint assignedCode)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
        };
        ac.Transponder.AssignCode(assignedCode, null, null);
        engine.World.AddAircraft(ac);
        return ac;
    }

    private static AircraftState AddLinedUpForDeparture(SimulationEngine engine, string callsign)
    {
        AircraftState ac = LinedUpAircraft.AtOak28R(callsign);
        engine.World.AddAircraft(ac);
        return ac;
    }

    private static RecordedCommand Recorded(string callsign, string command) => new(0, callsign, command, "XX", "conn-1");

    [Fact]
    public void Global_EmptyCallsign_Applies()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AircraftState first = AddAirborne(engine, "UAL1", 1234);
        AircraftState second = AddAirborne(engine, "UAL2", 4321);
        Assert.NotEqual(1234u, first.Transponder.Code);

        ActionOutcome outcome = engine.Actions.Apply(Recorded("", "SQALL"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(1234u, first.Transponder.Code);
        Assert.Equal(4321u, second.Transponder.Code);
        Assert.Equal(new ActionTrace(RecordedCommandKind.SquawkAll, ActionScope.Global), outcome.Trace);
    }

    [Fact]
    public void AircraftScope_Unknown_RefusesIdenticallyOnIssueAndApply()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);

        ActionOutcome issued = engine.Actions.Issue(new ActionInput("ZZZ", "FH 270", "conn-1", "XX", Baked: null));
        ActionOutcome applied = engine.Actions.Apply(Recorded("ZZZ", "FH 270"));

        Assert.False(issued.Result.Success);
        Assert.Equal("Aircraft 'ZZZ' not found", issued.Result.Message);
        Assert.Equal(issued.Result.Message, applied.Result.Message);
        Assert.Equal(issued.Trace, applied.Trace);
        // The refusal is still a routed command: recorded as rejected on the fresh path, never on the recorded one.
        RecordedCommand recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.False(recorded.Accepted);
    }

    [Fact]
    public void Issue_SamplesTheReactionDelay_AndBakesIt()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 5);
        AircraftState ac = AddAirborne(engine, "UAL123", 1234);

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("UAL123", "FH 270", "conn-1", "XX", Baked: null));

        Assert.True(outcome.Result.Success);
        Assert.Equal(5.0, Assert.Single(ac.DeferredDispatches).RemainingSeconds);
        Assert.NotNull(outcome.ToRecord);
        Assert.Equal(5.0, outcome.ToRecord.ReactionDelaySeconds);
        Assert.True(outcome.ToRecord.Accepted);
        Assert.Same(outcome.ToRecord, Assert.Single(engine.Scenario!.ActionLog));
    }

    [Fact]
    public void Replay_QueuesTheReadback_AndArmsTheGate_WhenAnswering()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: true, reactionDelaySeconds: 0);
        AircraftState ac = AddAirborne(engine, "UAL123", 1234);
        Assert.True(engine.Scenario!.PilotContacts.AnyAnswering);

        ActionOutcome outcome = engine.Actions.Apply(Recorded("UAL123", "FH 270"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Contains(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("UAL123", engine.World.ActiveFrequency.AwaitingReadbackFrom);
        Assert.Empty(engine.Scenario.ActionLog);
    }

    [Fact]
    public void Replay_DoesNotQueueAReadback_WhenNobodyAnswers()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AircraftState ac = AddAirborne(engine, "UAL123", 1234);

        engine.Actions.Apply(Recorded("UAL123", "FH 270"));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(engine.World.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void Apply_LogsAReplayFidelityWarning_OnlyWhenTheRecordedVerdictDiffers()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AddAirborne(engine, "UAL123", 1234);

        engine.Actions.Apply(Recorded("UAL123", "FH 270") with { Accepted = false });
        CapturedLogRecord disagreement = Assert.Single(tap.Drain(), r => r.Category == "ActionRouter");
        Assert.Contains("replay-fidelity", disagreement.Message);

        engine.Actions.Apply(Recorded("UAL123", "FH 180") with { Accepted = true });
        engine.Actions.Apply(Recorded("UAL123", "FH 090"));
        Assert.DoesNotContain(tap.Drain(), r => r.Category == "ActionRouter");
    }

    [Fact]
    public void Issue_RecordsEveryCommand_AcceptedOrNot()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AddAirborne(engine, "UAL123", 1234);

        engine.Actions.Issue(new ActionInput("UAL123", "FH 270", "conn-1", "XX", Baked: null));
        engine.Actions.Issue(new ActionInput("UAL123", "BOGUSVERB 1", "conn-1", "XX", Baked: null));

        var log = engine.Scenario!.ActionLog.OfType<RecordedCommand>().ToList();
        Assert.Equal(["FH 270", "BOGUSVERB 1"], log.Select(r => r.Command));
        Assert.Equal([true, false], log.Select(r => r.Accepted));
    }

    [Fact]
    public void LegacyTransportRecord_IsInert()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AircraftState ac = AddAirborne(engine, "UAL123", 1234);

        engine.Scenario!.IsPaused = false;

        ActionOutcome outcome = engine.Actions.Apply(Recorded("", "PAUSE"));

        Assert.False(outcome.Result.Success);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Transport, ActionScope.Global), outcome.Trace);
        Assert.False(engine.Scenario!.IsPaused);
        Assert.Empty(engine.Scenario!.ActionLog);
        Assert.Empty(ac.DeferredDispatches);
    }

    /// <summary>
    /// <see cref="ActionRouter.WouldRecord"/> answers what the log does. Each text is issued into a bare engine and the
    /// answer is compared with whether that call grew the action log — the mirror has to agree with the thing it mirrors,
    /// because the server gates its take-control on it: a false positive cuts a tape the session clock never touched, a
    /// false negative lets a real write replay over the instructor.
    /// </summary>
    [Fact]
    public void WouldRecord_AgreesWithTheLog()
    {
        (string Callsign, string Command, bool Records)[] cases =
        [
            // RecordingPolicy.Never: the session clock, bookmarks, the queued-conditionals query.
            ("", "UNPAUSE", false),
            ("", "PAUSE", false),
            ("", "SIMRATE 4", false),
            ("", "BM Test", false),
            ("UAL123", "SHOWAT", false),
            // Everything a controller writes, including through an AS prefix and a chain.
            ("UAL123", "H270", true),
            ("UAL123", "TRACK", true),
            ("UAL123", "AS 2B TRACK", true),
            ("UAL123", "AN 1 RV", true),
            ("UAL123", "RDH", true),
            ("UAL123", "FH 270; TRACK", true),
            // The two branches where each side hardcodes its answer instead of reading the arm table: Route passes a
            // literal RecordingPolicy.Text to Finish for a chain refusal, WouldRecord returns a literal true. Nothing
            // but these cases can catch the two drifting apart. "HO 3G; PAUSE" is the non-compoundable chain (PAUSE is
            // in the rejection set); "CTO, R270" is the takeoff-paired-with-immediate-turn refusal.
            ("UAL123", "HO 3G; PAUSE", true),
            ("UAL123", "CTO, R270", true),
        ];

        foreach ((string? callsign, string? command, bool records) in cases)
        {
            SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
            AddAirborne(engine, "UAL123", 1234);
            List<RecordedAction> log = engine.Scenario!.ActionLog;
            int before = log.Count;

            engine.Actions.Issue(new ActionInput(callsign, command, "conn-1", "XX", Baked: null));

            bool grew = log.Count > before;
            Assert.Equal(records, grew);
            Assert.Equal(grew, ActionRouter.WouldRecord(command));
        }
    }

    [Fact]
    public void SpecialCompound_RoutesEachUnit_AndRecordsEach()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
        scenario.StudentTcp = new Tcp(3, "T", "tcp-oak-twr", null);

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput(AiTestFixture.Callsign, "TRACK; SP1 ABC", "conn-1", "XX", Baked: null));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.Equal(scenario.StudentPosition, aircraft.Track.Owner);
        Assert.Equal("ABC", aircraft.Stars.Scratchpad1);
        // One record per unit, so replay stays per-unit; the compound itself records nothing.
        Assert.Null(outcome.ToRecord);
        Assert.Equal(["TRACK", "SP1 ABC"], scenario.ActionLog.OfType<RecordedCommand>().Select(r => r.Command));
    }

    [Fact]
    public void ChainWithANonCompoundableVerb_IsRefused_AndRecordedAsRejected()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AddAirborne(engine, "UAL123", 1234);

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("UAL123", "FH 270; PAUSE", "conn-1", "XX", Baked: null));

        Assert.False(outcome.Result.Success);
        Assert.Contains("cannot be part of a chained command", outcome.Result.Message);
        RecordedCommand recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.False(recorded.Accepted);
    }

    /// <summary>
    /// The same refusal when the chain is led by a scoped special whose argument arm takes any tail: `HO 3G; PAUSE`
    /// parses whole as a handoff to the TCP "3G; PAUSE", so the router used to send the swallowed text to the track
    /// arm as one command and the PAUSE vanished. The refusal has to fire, be recorded rejected, and leave the
    /// aircraft's handoff state untouched.
    /// </summary>
    [Fact]
    public void ChainLedByAHandoff_IsRefused_AndInitiatesNoHandoff()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AircraftState ac = AddAirborne(engine, "UAL123", 1234);
        ac.Track.Owner = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("UAL123", "HO 3G; PAUSE", "conn-1", "XX", Baked: null));

        Assert.False(outcome.Result.Success);
        Assert.Contains("cannot be part of a chained command", outcome.Result.Message);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.Null(ac.Track.HandoffInitiatedAt);
        RecordedCommand recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.False(recorded.Accepted);
    }

    /// <summary>
    /// The same shape for a takeoff clearance paired with an immediate turn (`CTO, R270`, the mis-spelling of the
    /// departure modifier `CTO MR270`): refused before dispatch, so the aircraft keeps the phase it was on and no
    /// MakeTurnPhase is inserted ahead of its takeoff chain.
    /// </summary>
    [Fact]
    public void ChainPairingATakeoffClearanceWithATurn_IsRefused_AndRecordedAsRejected()
    {
        SimulationEngine engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AircraftState ac = AddLinedUpForDeparture(engine, "UAL123");
        Phase? phaseBefore = ac.Phases!.CurrentPhase;

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("UAL123", "CTO, R270", "conn-1", "XX", Baked: null));

        Assert.False(outcome.Result.Success);
        Assert.Contains("cannot be paired with a takeoff clearance", outcome.Result.Message);
        Assert.Same(phaseBefore, ac.Phases!.CurrentPhase);
        Assert.DoesNotContain(ac.Phases.Phases, p => p is MakeTurnPhase);
        RecordedCommand recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.False(recorded.Accepted);
    }
}
