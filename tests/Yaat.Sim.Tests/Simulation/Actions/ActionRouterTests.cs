using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
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

    private static RecordedCommand Recorded(string callsign, string command) => new(0, callsign, command, "XX", "conn-1");

    [Fact]
    public void Global_EmptyCallsign_Applies()
    {
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        var first = AddAirborne(engine, "UAL1", 1234);
        var second = AddAirborne(engine, "UAL2", 4321);
        Assert.NotEqual(1234u, first.Transponder.Code);

        var outcome = engine.Actions.Apply(Recorded("", "SQALL"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(1234u, first.Transponder.Code);
        Assert.Equal(4321u, second.Transponder.Code);
        Assert.Equal(new ActionTrace(RecordedCommandKind.SquawkAll, ActionScope.Global, IsHostSlot: false), outcome.Trace);
    }

    [Fact]
    public void AircraftScope_Unknown_RefusesIdenticallyOnIssueAndApply()
    {
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);

        var issued = engine.Actions.Issue(new ActionInput("ZZZ", "FH 270", "conn-1", "XX", Baked: null));
        var applied = engine.Actions.Apply(Recorded("ZZZ", "FH 270"));

        Assert.False(issued.Result.Success);
        Assert.Equal("Aircraft 'ZZZ' not found", issued.Result.Message);
        Assert.Equal(issued.Result.Message, applied.Result.Message);
        Assert.Equal(issued.Trace, applied.Trace);
        // The refusal is still a routed command: recorded as rejected on the fresh path, never on the recorded one.
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.False(recorded.Accepted);
    }

    [Fact]
    public void Issue_SamplesTheReactionDelay_AndBakesIt()
    {
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 5);
        var ac = AddAirborne(engine, "UAL123", 1234);

        var outcome = engine.Actions.Issue(new ActionInput("UAL123", "FH 270", "conn-1", "XX", Baked: null));

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
        var engine = BuildEngine(soloTrainingMode: true, reactionDelaySeconds: 0);
        var ac = AddAirborne(engine, "UAL123", 1234);
        Assert.True(engine.Scenario!.PilotContacts.AnyAnswering);

        var outcome = engine.Actions.Apply(Recorded("UAL123", "FH 270"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Contains(ac.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("UAL123", engine.World.ActiveFrequency.AwaitingReadbackFrom);
        Assert.Empty(engine.Scenario.ActionLog);
    }

    [Fact]
    public void Replay_DoesNotQueueAReadback_WhenNobodyAnswers()
    {
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        var ac = AddAirborne(engine, "UAL123", 1234);

        engine.Actions.Apply(Recorded("UAL123", "FH 270"));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Null(engine.World.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void Apply_LogsAReplayFidelityWarning_OnlyWhenTheRecordedVerdictDiffers()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AddAirborne(engine, "UAL123", 1234);

        engine.Actions.Apply(Recorded("UAL123", "FH 270") with { Accepted = false });
        var disagreement = Assert.Single(tap.Drain(), r => r.Category == "ActionRouter");
        Assert.Contains("replay-fidelity", disagreement.Message);

        engine.Actions.Apply(Recorded("UAL123", "FH 180") with { Accepted = true });
        engine.Actions.Apply(Recorded("UAL123", "FH 090"));
        Assert.DoesNotContain(tap.Drain(), r => r.Category == "ActionRouter");
    }

    [Fact]
    public void Issue_RecordsEveryCommand_AcceptedOrNot()
    {
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
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
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        var ac = AddAirborne(engine, "UAL123", 1234);

        var outcome = engine.Actions.Apply(Recorded("", "PAUSE"));

        Assert.False(outcome.Result.Success);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Transport, ActionScope.Global, IsHostSlot: true), outcome.Trace);
        Assert.Empty(engine.Scenario!.ActionLog);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void SpecialCompound_RoutesEachUnit_AndRecordsEach()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
        scenario.StudentTcp = new Tcp(3, "T", "tcp-oak-twr", null);

        var outcome = engine.Actions.Issue(new ActionInput(AiTestFixture.Callsign, "TRACK; SP1 ABC", "conn-1", "XX", Baked: null));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.Equal(scenario.StudentPosition, aircraft.Track.Owner);
        Assert.Equal("ABC", aircraft.Stars.Scratchpad1);
        // One record per unit, so replay stays per-unit; the compound itself records nothing.
        Assert.Null(outcome.ToRecord);
        Assert.Equal(["TRACK", "SP1 ABC"], scenario.ActionLog.OfType<RecordedCommand>().Select(r => r.Command));
    }

    [Fact]
    public void ChainWithANonCompoundableVerb_IsRefused_AndRecordedAsRejected()
    {
        var engine = BuildEngine(soloTrainingMode: false, reactionDelaySeconds: 0);
        AddAirborne(engine, "UAL123", 1234);

        var outcome = engine.Actions.Issue(new ActionInput("UAL123", "FH 270; PAUSE", "conn-1", "XX", Baked: null));

        Assert.False(outcome.Result.Success);
        Assert.Contains("cannot be part of a chained command", outcome.Result.Message);
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.False(recorded.Accepted);
    }
}
