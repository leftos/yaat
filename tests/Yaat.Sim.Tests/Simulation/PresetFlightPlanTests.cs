using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Scenario presets that carry a flight-plan verb (DA, FP, VP, REMARKS) or a global squawk verb (SQALL): the engine runs
/// them through the scripted route (the verb's ArmTable body, recording nothing), immediately, on a timed preset and as a
/// WAIT payload, over the real ZOA data and the OAK layout. The session-clock and position verbs stay refused.
/// </summary>
public class PresetFlightPlanTests
{
    private const string Filed = "N152SP";
    private const string Cold = "N123AB";

    /// <summary>ZOA's OAK_TWR ("LC1"), whose STARS TCP is 3O.</summary>
    private const string OakTwrPositionId = "01GEAMB98RKCPP9HCNPW5AVDA5";

    private const string CouldNotApply = "[Preset] could not apply";

    private const string DeferredCouldNotApply = "[Deferred] could not apply";

    private const string ColdPresets = "\"presetCommands\": [],";

    /// <summary>A VFR C172 with a filed plan and a cold call with none, both parked at OAK.</summary>
    private const string FiledAndColdAtOak = """
        {
          "id": "preset-flight-plan",
          "name": "Filed and cold at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100,
                "route": "", "remarks": "", "aircraftType": "C172"
              }
            },
            {
              "id": "a2",
              "aircraftId": "N123AB",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "NEW7" },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK"
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PresetFlightPlanTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ImmediatePreset_Da_OnAColdCall_FilesTheAbbreviatedPlan()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Cold, "DA C172 045");

        AircraftFlightPlan plan = engine.FindAircraft(Cold)!.FlightPlan;
        Assert.True(plan.HasFlightPlan);
        Assert.Equal("C172", plan.AircraftType);
        Assert.Equal(4500, plan.Altitude.CruiseFeet);
        Assert.True(plan.IsVfr);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_Da_OnAFiledPlan_RefusesDupNewId()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Filed, "DA C172 045");

        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == $"{CouldNotApply}: DUP NEW ID"));
        Assert.Equal(1500, engine.FindAircraft(Filed)!.FlightPlan.Altitude.CruiseFeet);
    }

    [Fact]
    public void ImmediatePreset_AsTcp_Da_TagsThePlanCreator()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        TrackOwner? oakTower = TrackResolver.ResolveTcpToOwner(scenario, "3O", scenario.StudentPosition?.FacilityId);
        Assert.NotNull(oakTower);
        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Cold, "AS 3O DA C172 045");

        AircraftFlightPlan plan = engine.FindAircraft(Cold)!.FlightPlan;
        Assert.True(plan.HasFlightPlan);
        Assert.Equal(oakTower, plan.CreatedByOwner);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_AsUnknownTcp_Da_RefusesWithUnknownPosition()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Cold, "AS 2A DA C172");

        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == $"{CouldNotApply}: Unknown position: 2A"));
        Assert.False(engine.FindAircraft(Cold)!.FlightPlan.HasFlightPlan);
    }

    [Fact]
    public void TimedPreset_Da_FilesAtItsOffsetAndNotBefore()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        AircraftState ac = engine.FindAircraft(Cold)!;
        engine.DispatchPresetCommands(
            new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = "DA C172", TimeOffset = 5 }] }
        );

        Tick(engine, 4);
        Assert.False(ac.FlightPlan.HasFlightPlan);

        Tick(engine, 1);
        Assert.True(ac.FlightPlan.HasFlightPlan);
        Assert.Equal("C172", ac.FlightPlan.AircraftType);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitPreset_Da_FilesWhenTheWaitEnds()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        AircraftState ac = engine.FindAircraft(Cold)!;
        DispatchPresets(engine, Cold, "WAIT 5 DA C172");

        Tick(engine, 4);
        Assert.False(ac.FlightPlan.HasFlightPlan);

        Tick(engine, 1);
        Assert.True(ac.FlightPlan.HasFlightPlan);
        Assert.Equal("C172", ac.FlightPlan.AircraftType);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(DeferredCouldNotApply, StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FP B738 220 KOAK KSFO", "B738", "IFR")]
    [InlineData("VP C172 5500 KOAK KSFO", "C172", "VFR")]
    public void ImmediatePreset_FpAndVp_OnAColdCall_FileThePlan(string preset, string type, string rules)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Cold, preset);

        AircraftFlightPlan plan = engine.FindAircraft(Cold)!.FlightPlan;
        Assert.True(plan.HasFlightPlan);
        Assert.Equal(type, plan.AircraftType);
        Assert.Equal(rules, plan.FlightRules);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_Remarks_SetsTheRemarks()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Filed, "REMARKS text");

        Assert.Equal("text", engine.FindAircraft(Filed)!.FlightPlan.Remarks);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_Sqall_SquawksEveryAircraftsAssignedCode()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        uint assigned = 4301;
        foreach (AircraftState aircraft in engine.World.GetSnapshot())
        {
            aircraft.Transponder.AssignCode(assigned++, null, null);
            aircraft.Transponder.Code = 1200;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Filed, "SQALL");

        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal(ac.Transponder.AssignedCode, ac.Transponder.Code));
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_Pause_RefusesInAPreset()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Filed, "PAUSE");

        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == $"{CouldNotApply}: PAUSE cannot run from a preset"));
    }

    [Fact]
    public void RecordedRunWithADaPreset_ReplaysToTheSamePlan_AppliedOnce()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        if (_zoa is null)
        {
            return;
        }

        string json = WithColdPreset("DA C172 045", timeOffset: 0);
        SimulationEngine live = AiTestFixture.Load(json, _zoa, 7, []);
        Tick(live, 3);
        AircraftFlightPlan livePlan = live.FindAircraft(Cold)!.FlightPlan;
        Assert.True(livePlan.HasFlightPlan);
        Assert.Equal("C172", livePlan.AircraftType);
        Assert.DoesNotContain(live.Scenario!.ActionLog, a => a is RecordedAmendFlightPlan);
        Assert.Equal(["STRIP_N123AB_1_0"], StripIdsFor(live, Cold));

        SimulationEngine replay = Replayed(json, live, 3);

        AircraftFlightPlan replayPlan = replay.FindAircraft(Cold)!.FlightPlan;
        Assert.Equal(JsonSerializer.Serialize(livePlan.ToSnapshot()), JsonSerializer.Serialize(replayPlan.ToSnapshot()));
        Assert.Equal(live.Strips.Items.Count, replay.Strips.Items.Count);
        Assert.Equal(StripIds(live), StripIds(replay));
        Assert.DoesNotContain(tap.Drain(), r => r.Message.Contains("replay-fidelity", StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_Da_PrintsTheDepartureStripAtTheNextTick()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        DispatchPresets(engine, Cold, "DA C172 045");
        Assert.True(engine.FindAircraft(Cold)!.FlightPlan.HasFlightPlan);
        Assert.Equal(0, StripsFor(engine, Cold));

        Tick(engine, 1);

        Assert.Equal(["STRIP_N123AB_1_0"], StripIdsFor(engine, Cold));
    }

    [Fact]
    public void QueuedStripReprint_SurvivesASnapshotRestore()
    {
        if (Engine() is not { } live || Engine() is not { } restored)
        {
            return;
        }

        DispatchPresets(live, Cold, "DA C172 045");
        restored.RestoreFromSnapshot(live.CaptureSnapshot());

        Tick(live, 1);
        Tick(restored, 1);

        Assert.Equal(["STRIP_N123AB_1_0"], StripIdsFor(live, Cold));
        Assert.Equal(StripIdsFor(live, Cold), StripIdsFor(restored, Cold));
    }

    [Theory]
    [InlineData("SQALL, PAUSE")]
    [InlineData("PAUSE, SQALL")]
    public void ImmediatePreset_ScriptedVerbBesideARefusedOne_AppliesNothing(string preset)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        foreach (AircraftState aircraft in engine.World.GetSnapshot())
        {
            aircraft.Transponder.AssignCode(4301, null, null);
            aircraft.Transponder.Code = 1200;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Filed, preset);

        Assert.All(engine.World.GetSnapshot(), ac => Assert.Equal(1200u, ac.Transponder.Code));
        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == $"{CouldNotApply}: PAUSE cannot run from a preset"));
    }

    [Fact]
    public void WaitPreset_Pause_RefusesAtOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, Filed, "WAIT 5 PAUSE");

        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == $"{CouldNotApply}: PAUSE cannot run from a preset"));
        Assert.Empty(engine.FindAircraft(Filed)!.DeferredDispatches);
    }

    [Fact]
    public void WaitPreset_AsTcp_Da_TagsThePlanCreatorWhenItFires()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        TrackOwner? oakTower = TrackResolver.ResolveTcpToOwner(scenario, "3O", scenario.StudentPosition?.FacilityId);
        Assert.NotNull(oakTower);
        DispatchPresets(engine, Cold, "AS 3O WAIT 5 DA C172");

        Tick(engine, 5);

        AircraftFlightPlan plan = engine.FindAircraft(Cold)!.FlightPlan;
        Assert.True(plan.HasFlightPlan);
        Assert.Equal(oakTower, plan.CreatedByOwner);
    }

    [Theory]
    [InlineData("DA C172", "DA C172 runs from a typed line or a preset, not as an aircraft command")]
    [InlineData("SQALL", "SQALL runs from a typed line or a preset, not as an aircraft command")]
    public void DispatchCompound_RefusesAScriptedVerb(string command, string expected)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(Cold)!;
        CompoundCommand compound = CommandParser.ParseCompound(command).Value!;

        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, ScriptedContext(engine, ac));

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
    }

    [Fact]
    public void RecordedRunWithATimedDaPreset_PrintsTheStripInTheTickItFires_AndReplaysEqual()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        if (_zoa is null)
        {
            return;
        }

        string json = WithColdPreset("DA C172 045", timeOffset: 5);
        SimulationEngine live = AiTestFixture.Load(json, _zoa, 7, []);
        Tick(live, 4);
        Assert.False(live.FindAircraft(Cold)!.FlightPlan.HasFlightPlan);
        Assert.Equal(0, StripsFor(live, Cold));

        Tick(live, 1);
        AircraftFlightPlan livePlan = live.FindAircraft(Cold)!.FlightPlan;
        Assert.True(livePlan.HasFlightPlan);
        Assert.Equal(["STRIP_N123AB_5_0"], StripIdsFor(live, Cold));
        Assert.DoesNotContain(live.Scenario!.ActionLog, a => a is RecordedAmendFlightPlan);

        SimulationEngine replay = Replayed(json, live, 5);

        Assert.Equal(JsonSerializer.Serialize(livePlan.ToSnapshot()), JsonSerializer.Serialize(replay.FindAircraft(Cold)!.FlightPlan.ToSnapshot()));
        Assert.Equal(StripIds(live), StripIds(replay));
        Assert.DoesNotContain(tap.Drain(), r => r.Message.Contains("replay-fidelity", StringComparison.Ordinal));
    }

    /// <summary>The two-aircraft scenario with one preset on the cold call.</summary>
    private static string WithColdPreset(string command, int timeOffset) =>
        FiledAndColdAtOak.Replace(
            ColdPresets,
            $"\"presetCommands\": [ {{ \"id\": \"p0\", \"command\": \"{command}\", \"timeOffset\": {timeOffset} }} ],",
            StringComparison.Ordinal
        );

    /// <summary>A fresh engine replaying <paramref name="live"/>'s action log over the same scenario to <paramref name="seconds"/>.</summary>
    private SimulationEngine Replayed(string json, SimulationEngine live, int seconds)
    {
        var recording = new SessionRecording
        {
            ScenarioJson = json,
            RngSeed = 7,
            Actions = [.. live.Scenario!.ActionLog],
            TotalElapsedSeconds = seconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
        };
        var replay = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replay.Replay(recording, seconds);
        return replay;
    }

    private static int StripsFor(SimulationEngine engine, string callsign) =>
        engine.Strips.Items.Values.Count(s => string.Equals(s.AircraftId, callsign, StringComparison.OrdinalIgnoreCase));

    private static DispatchContext ScriptedContext(SimulationEngine engine, AircraftState ac) =>
        new()
        {
            GroundLayout = ac.Ground.Layout ?? engine.ResolveGroundLayout(ac),
            Rng = engine.World.Rng,
            Weather = engine.World.Weather,
            FindAircraft = engine.FindAircraft,
            ListAircraft = () => engine.World.GetSnapshot(),
            ValidateDctFixes = engine.Scenario!.ValidateDctFixes,
            AutoCrossRunwayMode = engine.Scenario.AutoCrossRunwayMode,
            SoloTrainingMode = engine.Scenario.SoloTrainingMode,
            SoloRpoCommandsAllowed = engine.Scenario.SoloRpoCommandsAllowed,
            RpoShowPilotSpeech = engine.Scenario.RpoShowPilotSpeech,
            TerminalEmitter = engine.EmitTerminalEntry,
            ArtccConfig = engine.Scenario.ArtccConfig,
            AsdexSafetyLogicConfigs = engine.Scenario.AsdexSafetyLogicConfigs,
            ToweredAirports = engine.Scenario.ToweredAirports,
            ActiveRunways = engine.Scenario.ActiveRunways,
            ScenarioElapsedSeconds = engine.Scenario.ElapsedSeconds,
            SessionStartUtc = engine.Scenario.SessionStartUtc,
            PreserveConditionals = false,
            IsScenarioScripted = true,
            FacilityHint = null,
            StudentPosition = engine.Scenario.StudentPosition,
            LayoutForAirport = null,
            SpeechContextFor = _ => PilotSpeechContext.Canonical,
        };

    private static List<string> StripIdsFor(SimulationEngine engine, string callsign) =>
        [
            .. engine
                .Strips.Items.Values.Where(s => string.Equals(s.AircraftId, callsign, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Id)
                .Order(StringComparer.Ordinal),
        ];

    private static List<string> StripIds(SimulationEngine engine) => [.. engine.Strips.Items.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// A bare engine over the ZOA configuration with the student at OAK_TWR, set the way the server's load resolves it, and
    /// the ARTCC initialisation that load runs, so an <c>AS 3O</c> preset resolves.
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(FiledAndColdAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = _zoa.ResolvePosition(OakTwrPositionId);
        scenario.StudentTcp = _zoa.GetTcpForPosition(OakTwrPositionId);
        engine.InitializeFromArtcc();
        return engine;
    }

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

    private static void DispatchPresets(SimulationEngine engine, string callsign, params string[] commands)
    {
        AircraftState ac = engine.FindAircraft(callsign)!;
        var loaded = new LoadedAircraft { State = ac, PresetCommands = [.. commands.Select(c => new PresetCommand { Command = c, TimeOffset = 0 })] };
        engine.DispatchPresetCommands(loaded);
    }
}
