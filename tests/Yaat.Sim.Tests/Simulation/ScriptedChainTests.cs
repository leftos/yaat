using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The room-wide squawks (SQALL / SNALL / SSALL) riding a chain, typed or in a preset: each runs when its block runs, at once
/// for the first block and at the WAIT gate for a later one, through the engine's scripted route, once live and once in a
/// replay. Every other non-compoundable verb keeps the chain refusal, and so does a squawk beside a scoped special.
/// </summary>
public class ScriptedChainTests
{
    private const string Chained = "cannot be part of a chained command";

    private const string CouldNotApply = "could not apply";

    private const string WaitExpired = "[Deferred] WAIT expired";

    private const string ShadowCallsign = "DAL456";

    private static readonly string[] Callsigns = ["UAL1", "UAL2", "UAL3"];

    /// <summary>Three filed aircraft parked at OAK, each squawking 1234 off its assigned code from a t=0 preset.</summary>
    private const string ThreeParkedAtOak = """
        {
          "id": "scripted-chain",
          "name": "Three parked at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "N152SP", "aircraftType": "C172", "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KSQL", "cruiseAltitude": 1500, "cruiseSpeed": 100,
                "route": "", "remarks": "", "aircraftType": "C172"
              },
              "presetCommands": [ { "id": "p1", "command": "SQ 1234", "timeOffset": 0 } ]
            },
            {
              "id": "a2", "aircraftId": "N123AB", "aircraftType": "C172", "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "NEW7" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KSQL", "cruiseAltitude": 1500, "cruiseSpeed": 100,
                "route": "", "remarks": "", "aircraftType": "C172"
              },
              "presetCommands": [ { "id": "p2", "command": "SQ 1234", "timeOffset": 0 } ]
            },
            {
              "id": "a3", "aircraftId": "N346G", "aircraftType": "AA5", "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "GA3" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KAPC", "cruiseAltitude": 2000, "cruiseSpeed": 100,
                "route": "", "remarks": "", "aircraftType": "AA5"
              },
              "presetCommands": [ { "id": "p3", "command": "SQ 1234", "timeOffset": 0 } ]
            }
          ]
        }
        """;

    /// <summary>Three filed aircraft airborne near OAK, each squawking 1234 off its assigned code from a t=0 preset.</summary>
    private const string ThreeAirborneNearOak = """
        {
          "id": "scripted-chain-airborne",
          "name": "Three airborne near OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "N80ZU", "aircraftType": "DA62", "transponderMode": "C",
              "startingConditions": { "type": "FixOrFrd", "fix": "OAK340010", "altitude": 3500, "navigationPath": "OAK" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KSQL", "cruiseAltitude": 3500, "cruiseSpeed": 120,
                "route": "", "remarks": "", "aircraftType": "DA62"
              },
              "presetCommands": [ { "id": "p1", "command": "SQ 1234", "timeOffset": 0 } ]
            },
            {
              "id": "a2", "aircraftId": "N655EX", "aircraftType": "C210", "transponderMode": "C",
              "startingConditions": { "type": "FixOrFrd", "fix": "OAK160010", "altitude": 2500, "navigationPath": "OAK" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KSQL", "cruiseAltitude": 2500, "cruiseSpeed": 120,
                "route": "", "remarks": "", "aircraftType": "C210"
              },
              "presetCommands": [ { "id": "p2", "command": "SQ 1234", "timeOffset": 0 } ]
            },
            {
              "id": "a3", "aircraftId": "N929AW", "aircraftType": "BE33", "transponderMode": "C",
              "startingConditions": { "type": "FixOrFrd", "fix": "OAK050008", "altitude": 3000, "navigationPath": "OAK" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KSQL", "cruiseAltitude": 3000, "cruiseSpeed": 120,
                "route": "", "remarks": "", "aircraftType": "BE33"
              },
              "presetCommands": [ { "id": "p3", "command": "SQ 1234", "timeOffset": 0 } ]
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ScriptedChainTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Preset_WaitSqall_ResetsEveryCodeAtTheGate()
    {
        SimulationEngine engine = AirborneEngine();
        List<TerminalEntry> entries = Capture(engine);

        DispatchPreset(engine, "UAL1", "WAIT 5 SQALL");

        Tick(engine, 4);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.NotEqual(ac.Transponder.AssignedCode, ac.Transponder.Code));
        Tick(engine, 1);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal(ac.Transponder.AssignedCode, ac.Transponder.Code));
        Assert.DoesNotContain(entries, e => e.Message.Contains(Chained, StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Message.Contains(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void Preset_FhThenWaitSnall_AssignsTheHeadingAtOnce_AndSetsModeCExactlyFiveSecondsAfterTheTurn()
    {
        SimulationEngine engine = AirborneEngine();
        SetModes(engine, "Standby");
        List<TerminalEntry> entries = Capture(engine);
        AircraftState ual1 = engine.FindAircraft("UAL1")!;

        DispatchPreset(engine, "UAL1", "FH 090; WAIT 5 SNALL");

        Assert.Equal(90, ual1.Targets.AssignedMagneticHeading?.Degrees);
        CommandBlock heading = ual1.Queue.Blocks[0];
        int? turnDone = null;
        int? modeC = null;
        for (int second = 1; (second <= 60) && (modeC is null); second++)
        {
            engine.TickOneSecond();
            turnDone ??= heading.IsPassed ? second : null;
            modeC = engine.World.GetSnapshot().All(ac => ac.Transponder.Mode == "C") ? second : null;
        }

        Assert.NotNull(turnDone);
        Assert.Equal(turnDone + 5, modeC);
        Assert.DoesNotContain(entries, e => e.Message.Contains(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void Preset_SsallThenFh_SetsStandbyAtOnce_AndAssignsTheHeading()
    {
        SimulationEngine engine = AirborneEngine();
        SetModes(engine, "C");
        List<TerminalEntry> entries = Capture(engine);

        DispatchPreset(engine, "UAL1", "SSALL; FH 090");

        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal("Standby", ac.Transponder.Mode));
        Tick(engine, 1);
        Assert.Equal(90, engine.FindAircraft("UAL1")!.Targets.AssignedMagneticHeading?.Degrees);
        Assert.DoesNotContain(entries, e => e.Message.Contains(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_WaitSqall_ResetsEveryCodeAtTheGate_AndRecordsTheLineOnce()
    {
        SimulationEngine engine = AirborneEngine();

        CommandResult result = Issue(engine, "UAL1", "WAIT 5 SQALL");

        Assert.True(result.Success, result.Message);
        Tick(engine, 4);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.NotEqual(ac.Transponder.AssignedCode, ac.Transponder.Code));
        Tick(engine, 1);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal(ac.Transponder.AssignedCode, ac.Transponder.Code));
        RecordedCommand recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.Equal("WAIT 5 SQALL", recorded.Command);
    }

    [Fact]
    public void Typed_FhThenSqall_AssignsTheHeadingAtOnce_AndResetsEveryCodeWhenItsBlockRuns()
    {
        SimulationEngine engine = AirborneEngine();

        CommandResult result = Issue(engine, "UAL1", "FH 090; SQALL");

        Assert.True(result.Success, result.Message);
        Assert.Equal(90, engine.FindAircraft("UAL1")!.Targets.AssignedMagneticHeading?.Degrees);
        Tick(engine, 20);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal(ac.Transponder.AssignedCode, ac.Transponder.Code));
        Assert.Equal("FH 090; SQALL", Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog)).Command);
    }

    [Fact]
    public void RecordedTypedWaitSqall_ReplaysToTheSameTransponders_FiringOnceInEachRun()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine live = AiTestFixture.Load(ThreeParkedAtOak, _zoa, 7, []);
        List<TerminalEntry> liveEntries = Capture(live);
        Tick(live, 2);
        Assert.All(live.World.GetSnapshot(), ac => Assert.NotEqual(ac.Transponder.AssignedCode, ac.Transponder.Code));

        CommandResult result = Issue(live, "N152SP", "WAIT 5 SQALL");
        Assert.True(result.Success, result.Message);
        Tick(live, 18);
        Assert.All(live.World.GetSnapshot(), ac => Assert.Equal(ac.Transponder.AssignedCode, ac.Transponder.Code));

        var replayEntries = new List<TerminalEntry>();
        SimulationEngine replay = Replayed(live, 20, replayEntries);

        Assert.Equal(Transponders(live), Transponders(replay));
        Assert.Equal(["WAIT 5 SQALL"], live.Scenario!.ActionLog.OfType<RecordedCommand>().Select(r => r.Command));
        Assert.Single(liveEntries, e => e.Message.StartsWith(WaitExpired, StringComparison.Ordinal));
        Assert.Single(replayEntries, e => e.Message.StartsWith(WaitExpired, StringComparison.Ordinal));
    }

    [Fact]
    public void QueuedSqallBlock_SurvivesASnapshotRestore_AndFiresAfterIt()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine live = AiTestFixture.Load(ThreeAirborneNearOak, _zoa, 7, []);
        Tick(live, 2);
        AircraftState ac = live.FindAircraft("N80ZU")!;
        CommandResult result = Issue(live, "N80ZU", "FH 090; WAIT 10 SQALL");
        Assert.True(result.Success, result.Message);
        for (int second = 0; (second < 10) && (ac.DeferredDispatches.Count > 0); second++)
        {
            live.TickOneSecond();
        }

        Assert.True(
            ac.Queue.Blocks.Exists(HoldsAPendingGlobal),
            $"deferred {ac.DeferredDispatches.Count}; blocks: "
                + string.Join(" | ", ac.Queue.Blocks.Select(b => $"{b.Description} applied={b.IsApplied} parsed={b.ParsedCommands?.Count}"))
        );

        SimulationEngine restored = AiTestFixture.Load(ThreeAirborneNearOak, _zoa, 7, []);
        restored.RestoreFromSnapshot(live.CaptureSnapshot());

        Assert.Equal(ac.Queue.Blocks.Count(b => !b.IsApplied), restored.FindAircraft("N80ZU")!.Queue.Blocks.Count(b => !b.IsApplied));
        Assert.All(restored.World.GetSnapshot(), a => Assert.NotEqual(a.Transponder.AssignedCode, a.Transponder.Code));
        Tick(restored, 90);
        Assert.All(restored.World.GetSnapshot(), a => Assert.Equal(a.Transponder.AssignedCode, a.Transponder.Code));
    }

    [Theory]
    [InlineData("FH 090; PAUSE", "PAUSE")]
    [InlineData("FH 090; TAXIALL 28R", "TAXIALL")]
    [InlineData("FH 090; DA C172", "DA")]
    public void Typed_ChainWithAnotherNonCompoundableVerb_IsStillRefused(string command, string verb)
    {
        SimulationEngine engine = AirborneEngine();

        CommandResult result = Issue(engine, "UAL1", command);

        Assert.False(result.Success);
        Assert.StartsWith(verb, result.Message, StringComparison.Ordinal);
        Assert.EndsWith(Chained, result.Message, StringComparison.Ordinal);
        Assert.Null(engine.FindAircraft("UAL1")!.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void Preset_WaitPause_RefusesAtOnce()
    {
        SimulationEngine engine = AirborneEngine();
        List<TerminalEntry> entries = Capture(engine);

        DispatchPreset(engine, "UAL1", "WAIT 5 PAUSE");

        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == "[Preset] could not apply: PAUSE cannot run from a preset"));
        Assert.Empty(engine.FindAircraft("UAL1")!.DeferredDispatches);
    }

    [Fact]
    public void Typed_InvalidHeadingThenSqall_LeavesEveryTransponderUnchanged()
    {
        SimulationEngine engine = AirborneEngine();
        Dictionary<string, uint> before = Codes(engine);

        CommandResult result = Issue(engine, "UAL1", "FH 999; SQALL");

        Assert.False(result.Success);
        Tick(engine, 1);
        Assert.Equal(before, Codes(engine));
    }

    [Fact]
    public void Typed_SqallBesideACommandTheDryRunRefuses_LeavesEveryTransponderUnchanged()
    {
        SimulationEngine engine = AirborneEngine();
        Dictionary<string, uint> before = Codes(engine);

        CommandResult result = Issue(engine, "UAL1", "SQALL, CTO");

        Assert.False(result.Success);
        Assert.Equal(before, Codes(engine));
    }

    [Fact]
    public void DispatchCompound_WithoutTheScriptedRoute_RefusesAChainedSqall()
    {
        SimulationEngine engine = AirborneEngine();
        AircraftState ac = engine.FindAircraft("UAL1")!;
        Dictionary<string, uint> before = Codes(engine);
        CompoundCommand compound = CommandParser.ParseCompound("FH 090; SQALL").Value!;
        DispatchContext ctx = engine.BuildDispatchContext(ac, isScenarioScripted: false, facilityHint: null) with { ScriptedArm = null };

        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, ctx);

        Assert.False(result.Success);
        Assert.Equal("SQALL cannot be part of a chained command", result.Message);
        Assert.Equal(before, Codes(engine));
    }

    [Theory]
    [InlineData("TRACK; SQALL")]
    [InlineData("RDACK; SQALL")]
    public void Typed_GlobalBesideAScopedSpecial_KeepsTheChainRefusal(string command)
    {
        SimulationEngine engine = AirborneEngine();
        Dictionary<string, uint> before = Codes(engine);

        CommandResult result = Issue(engine, "UAL1", command);

        Assert.False(result.Success);
        Assert.Equal("SQALL cannot be part of a chained command", result.Message);
        Assert.Equal(before, Codes(engine));
    }

    [Theory]
    [InlineData("WAIT 5 SQALL")]
    [InlineData("WAITD 1 SNALL")]
    public void Typed_GatesAndGlobals_OnAnAirborneShadow_RefuseAndLeaveItLive(string command)
    {
        SimulationEngine engine = AirborneEngine();
        AircraftState shadow = LiveTrafficKinematics.CreateShadow(
            ShadowCallsign,
            "B738",
            new LiveTrafficSample(0, 37.9, -121.3, 11_000, 300, 90, 0, LiveTrafficSource.Stars, 4521),
            new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSFO",
                Destination = "KSMF",
            }
        );
        shadow.ClearToFiledAltitude();
        engine.World.AddAircraft(shadow);

        CommandResult result = Issue(engine, ShadowCallsign, command);

        Assert.False(result.Success);
        Assert.Equal($"ASSUME {ShadowCallsign} first — live traffic is not controllable", result.Message);
        Assert.True(shadow.IsShadow);
        Assert.Empty(shadow.DeferredDispatches);
    }

    [Fact]
    public void Typed_WaitdSqall_ResetsEveryCodeOnceTheDistanceIsFlown()
    {
        SimulationEngine engine = AirborneEngine();

        CommandResult result = Issue(engine, "UAL1", "WAITD 1 SQALL");

        Assert.True(result.Success, result.Message);
        Tick(engine, 5);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.NotEqual(ac.Transponder.AssignedCode, ac.Transponder.Code));
        Tick(engine, 25);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal(ac.Transponder.AssignedCode, ac.Transponder.Code));
    }

    [Fact]
    public void Typed_SsallThenFh_SetsStandbyAtOnce()
    {
        SimulationEngine engine = AirborneEngine();
        SetModes(engine, "C");

        CommandResult result = Issue(engine, "UAL1", "SSALL; FH 090");

        Assert.True(result.Success, result.Message);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal("Standby", ac.Transponder.Mode));
        Tick(engine, 1);
        Assert.Equal(90, engine.FindAircraft("UAL1")!.Targets.AssignedMagneticHeading?.Degrees);
    }

    [Fact]
    public void Typed_FhThenSnall_SetsModeCWhenItsBlockRuns()
    {
        SimulationEngine engine = AirborneEngine();
        SetModes(engine, "Standby");

        CommandResult result = Issue(engine, "UAL1", "FH 090; SNALL");

        Assert.True(result.Success, result.Message);
        Assert.Equal(90, engine.FindAircraft("UAL1")!.Targets.AssignedMagneticHeading?.Degrees);
        Tick(engine, 20);
        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal("C", ac.Transponder.Mode));
    }

    [Theory]
    [InlineData("TRACK; SQALL")]
    [InlineData("RDACK; SQALL")]
    public void ClientPreSendPredicate_RefusesAGlobalBesideAScopedSpecial(string command) =>
        Assert.IsType<SquawkAllCommand>(CompoundPolicy.FindNonCompoundableInChain(command));

    [Theory]
    [InlineData("WAIT 5 SQALL")]
    [InlineData("FH 090; SNALL")]
    [InlineData("SSALL; FH 090")]
    public void ClientPreSendPredicate_LetsAChainedGlobalSquawkThrough(string command) =>
        Assert.Null(CompoundPolicy.FindNonCompoundableInChain(command));

    [Fact]
    public void ClientPreSendPredicate_StillRefusesAChainedPause() =>
        Assert.IsType<PauseCommand>(CompoundPolicy.FindNonCompoundableInChain("FH 090; PAUSE"));

    /// <summary>Three airborne aircraft, each squawking 1200 off a distinct assigned code, with no pilot reaction delay.</summary>
    private static SimulationEngine AirborneEngine()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                CommandRunDelayMinSeconds = 0,
                CommandRunDelayMaxSeconds = 0,
            },
        };
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        uint assigned = 4301;
        double latitude = 37.6;
        foreach (string callsign in Callsigns)
        {
            var ac = new AircraftState
            {
                Callsign = callsign,
                AircraftType = "B738",
                Position = new LatLon(latitude, -122.2),
                TrueHeading = new TrueHeading(090),
                Altitude = 5000,
                IndicatedAirspeed = 250,
                IsOnGround = false,
                FlightPlan = new AircraftFlightPlan(),
            };
            ac.Transponder.AssignCode(assigned++, null, null);
            ac.Transponder.Code = 1200;
            engine.World.AddAircraft(ac);
            latitude += 0.1;
        }

        return engine;
    }

    private static bool HoldsAPendingGlobal(CommandBlock block) =>
        (!block.IsApplied) && (block.ParsedCommands?.Exists(CompoundPolicy.IsChainableGlobal) == true);

    private static CommandResult Issue(SimulationEngine engine, string callsign, string command) =>
        engine.Actions.Issue(new ActionInput(callsign, command, "conn-1", "XX", Baked: null)).Result;

    private static void DispatchPreset(SimulationEngine engine, string callsign, string command)
    {
        AircraftState ac = engine.FindAircraft(callsign)!;
        engine.DispatchPresetCommands(new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = command, TimeOffset = 0 }] });
    }

    private static void SetModes(SimulationEngine engine, string mode)
    {
        foreach (AircraftState ac in engine.World.GetSnapshot())
        {
            ac.Transponder.Mode = mode;
        }
    }

    private static Dictionary<string, uint> Codes(SimulationEngine engine) =>
        engine.World.GetSnapshot().ToDictionary(ac => ac.Callsign, ac => ac.Transponder.Code);

    private static List<string> Transponders(SimulationEngine engine) =>
        [.. engine.World.GetSnapshot().Select(ac => $"{ac.Callsign} {ac.Transponder.Code:D4} {ac.Transponder.Mode}").Order(StringComparer.Ordinal)];

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static List<TerminalEntry> Capture(SimulationEngine engine)
    {
        var entries = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entries.Add;
        return entries;
    }

    /// <summary>A fresh engine replaying <paramref name="live"/>'s action log over the same scenario to <paramref name="seconds"/>.</summary>
    private SimulationEngine Replayed(SimulationEngine live, int seconds, List<TerminalEntry> entries)
    {
        var recording = new SessionRecording
        {
            ScenarioJson = ThreeParkedAtOak,
            RngSeed = 7,
            Actions = [.. live.Scenario!.ActionLog],
            TotalElapsedSeconds = seconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
        };
        var replay = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replay.TerminalEntryEmitted += entries.Add;
        replay.Replay(recording, seconds);
        return replay;
    }
}
