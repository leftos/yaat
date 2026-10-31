using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The autotrack bodies on the bare engine: the load-time conditions a scenario aircraft carries, the generator
/// spawn that is owned before its spawn record is written, and the two post-physics passes — the airport-based
/// deferred autotrack and the flight-plan-creator autotrack. They decide from engine state alone (the scenario's
/// ATC roster, its ARTCC config, the world), so a plain <see cref="SimulationEngine.TickOneSecond"/> over the real
/// OAK data reaches every gate the live room reaches. Real NCT positions from the ZOA config; the student works 2B.
/// </summary>
public class AutoTrackStepTests
{
    private const string OakArrivalGeneratorsScenario = "TestData/oak-arrival-generators-scenario.json";
    private const string Say = "SAY REQUEST FULL STOP OAKLAND";
    private const string HandoffJson = """{ "positionId": "POS-4U", "handoffDelay": 5, "scratchPad": "OAK" }""";
    private const string NoHandoffJson = """{ "positionId": "POS-4U", "scratchPad": "OAK" }""";

    private static readonly AutoTrackConditions Handoff = new()
    {
        PositionId = "POS-4U",
        HandoffDelay = 5,
        ScratchPad = "OAK",
    };

    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

    private readonly Yaat.Sim.Data.Vnas.ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AutoTrackStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The bare engine over the parked-at-OAK fixture with the student and the two NCT positions resolved the way
    /// the server's load resolves them. <paramref name="autoTrackAirportIds"/> goes on the 4U position, which is
    /// what the deferred autotrack pass matches a departure against.
    /// </summary>
    private SimulationEngine? Engine(List<string> autoTrackAirportIds) => Engine(AiTestFixture.ParkedAtOak, autoTrackAirportIds);

    /// <summary>The same engine over the caller's scenario.</summary>
    private SimulationEngine? Engine(string scenarioJson, List<string> autoTrackAirportIds)
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(scenarioJson, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;

        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc { PositionId = "POS-4U", AutoTrackAirportIds = autoTrackAirportIds },
                Owner = Nct4U,
                Tcp = TrackResolver.FindTcpByCode(scenario, "4U")!,
            }
        );
        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc { PositionId = "POS-4Q" },
                Owner = Nct4Q,
                Tcp = TrackResolver.FindTcpByCode(scenario, "4Q")!,
            }
        );
        return engine;
    }

    /// <summary>
    /// The vNAS cold-call shape of N655EX: airborne near OAK with autotrack conditions and no <c>flightplan</c> block.
    /// <paramref name="autoTrackConditions"/> is the JSON object the aircraft carries.
    /// </summary>
    private static string ColdCallScenario(int spawnDelay, string preset, string autoTrackConditions) =>
        $$"""
            {
              "id": "cold-call-pre-tracked",
              "name": "Cold call pre-tracked at OAK",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "N655EX",
                  "aircraftType": "C210",
                  "transponderMode": "C",
                  "startingConditions": { "type": "FixOrFrd", "fix": "OAK160010", "altitude": 1400, "navigationPath": "OAK" },
                  "presetCommands": [ { "id": "p1", "command": "{{preset}}", "timeOffset": 0 } ],
                  "spawnDelay": {{spawnDelay}},
                  "airportId": "OAK",
                  "autoTrackConditions": {{autoTrackConditions}}
                }
              ]
            }
            """;

    private static LoadedAircraft ColdCallAircraft(SimulationEngine engine, int spawnDelay, AutoTrackConditions conditions)
    {
        if (spawnDelay > 0)
        {
            return engine.Scenario!.DelayedQueue.Single().Aircraft;
        }

        return new LoadedAircraft
        {
            State = engine.FindAircraft("N655EX")!,
            SpawnDelaySeconds = 0,
            AutoTrackConditions = conditions,
        };
    }

    /// <summary>The queued cold call with a handoff, its handing-off position in <paramref name="facilityId"/>, autotracked.</summary>
    private AircraftState? ColdCallHandedOffFrom(string facilityId)
    {
        if (Engine(ColdCallScenario(1403, Say, HandoffJson), []) is not { } engine)
        {
            return null;
        }

        engine.Scenario!.AtcPositions.Single(p => p.Source.PositionId == "POS-4U").Source.FacilityId = facilityId;
        LoadedAircraft loaded = ColdCallAircraft(engine, 1403, Handoff);
        engine.ApplyAutoTrackConditions(loaded);
        return loaded.State;
    }

    /// <summary>The lines the room would print, captured as the engine emits them.</summary>
    private static List<string> CaptureTerminal(SimulationEngine engine)
    {
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry => lines.Add(entry.Message);
        return lines;
    }

    [Fact]
    public void ScenarioAircraftWithAutoTrackConditionsIsOwnedWithItsHandoffQueued()
    {
        if (Engine([]) is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        var loaded = new LoadedAircraft
        {
            State = engine.FindAircraft(AiTestFixture.Callsign)!,
            SpawnDelaySeconds = 0,
            AutoTrackConditions = new AutoTrackConditions { PositionId = "POS-4U", HandoffDelay = 30 },
        };

        engine.ApplyAutoTrackConditions(loaded);

        Assert.NotNull(loaded.State.Track.Owner);
        Assert.True(loaded.State.Track.Owner!.MatchesPosition(Nct4U));
        Assert.Contains(loaded.AutoTrackMessages, msg => msg.Contains("[AutoTrack] Owned by", StringComparison.Ordinal));

        DelayedHandoff queued = Assert.Single(scenario.DelayedHandoffQueue);
        Assert.Equal(AiTestFixture.Callsign, queued.Callsign);
        Assert.True(queued.Target.MatchesPosition(Student));
        Assert.Equal(30, queued.FireAtSeconds);
    }

    [Theory]
    [InlineData(1403)]
    [InlineData(0)]
    public void ColdCallPreTrackedForTheStudentCarriesAPlanAndItsDiscreteCode(int spawnDelay)
    {
        if (Engine(ColdCallScenario(spawnDelay, Say, HandoffJson), []) is not { } engine)
        {
            return;
        }

        LoadedAircraft loaded = ColdCallAircraft(engine, spawnDelay, Handoff);

        engine.ApplyAutoTrackConditions(loaded);

        AircraftState state = loaded.State;
        Assert.NotNull(state.Track.Owner);
        Assert.True(state.Track.Owner!.MatchesPosition(Nct4U));
        Assert.True(state.FlightPlan.HasFlightPlan);
        Assert.Equal("VFR", state.FlightPlan.FlightRules);
        Assert.Equal("C210", state.FlightPlan.AircraftType);
        Assert.Equal("A", state.FlightPlan.EquipmentSuffix);
        Assert.NotEqual(0u, state.Transponder.AssignedCode);
        Assert.Equal(state.Transponder.AssignedCode, state.Transponder.Code);

        if (spawnDelay > 0)
        {
            AiTestFixture.Tick(engine, spawnDelay + 1);
            Assert.Same(state, engine.FindAircraft("N655EX"));
            Assert.Equal("OAK", state.FlightPlan.Destination);
        }
    }

    [Fact]
    public void HandoffPlan_SuffixedType_KeepsItsEquipment()
    {
        string scenarioJson = ColdCallScenario(0, Say, HandoffJson).Replace("\"C210\"", "\"BE36/G\"", StringComparison.Ordinal);
        if (Engine(scenarioJson, []) is not { } engine)
        {
            return;
        }

        LoadedAircraft loaded = ColdCallAircraft(engine, 0, Handoff);

        engine.ApplyAutoTrackConditions(loaded);

        Assert.Equal("BE36", loaded.State.FlightPlan.AircraftType);
        Assert.Equal("G", loaded.State.FlightPlan.EquipmentSuffix);
    }

    [Fact]
    public void HandoffPlan_PositionOutsideTheRoster_TakesTheOwnersFacility()
    {
        if (Engine(ColdCallScenario(0, Say, HandoffJson), []) is not { } engine)
        {
            return;
        }

        // A tower with no STARS configuration: its owner carries the airport itself, not the TRACON above it.
        string truckeeTowerId = _zoa!.FindPositionsByCallsign("TRK_TWR").Single().Id;
        var conditions = new AutoTrackConditions { PositionId = truckeeTowerId, HandoffDelay = 5 };
        LoadedAircraft loaded = ColdCallAircraft(engine, 0, conditions);

        engine.ApplyAutoTrackConditions(loaded);

        Assert.NotNull(loaded.State.Track.Owner);
        Assert.True(loaded.State.FlightPlan.HasFlightPlan);
        Assert.Equal("TRK", loaded.State.FlightPlan.Departure);
    }

    [Fact]
    public void AutoTrackedLabelAircraft_NoHandoff_StaysWithoutAPlan()
    {
        if (Engine(ColdCallScenario(1403, Say, NoHandoffJson), []) is not { } engine)
        {
            return;
        }

        LoadedAircraft loaded = ColdCallAircraft(engine, 1403, Handoff);

        engine.ApplyAutoTrackConditions(loaded);

        Assert.NotNull(loaded.State.Track.Owner);
        Assert.False(loaded.State.FlightPlan.HasFlightPlan);
        Assert.Equal(0u, loaded.State.Transponder.AssignedCode);
        Assert.Equal(1200u, loaded.State.Transponder.Code);
    }

    [Fact]
    public void HandoffPlan_DepartureIsTheFacilitysAirport()
    {
        if (ColdCallHandedOffFrom("KOAK") is not { } state)
        {
            return;
        }

        Assert.True(state.FlightPlan.HasFlightPlan);
        Assert.Equal("OAK", state.FlightPlan.Departure);
    }

    [Fact]
    public void HandoffPlan_FacilityIsNotAnAirport_LeavesDepartureBlank()
    {
        if (ColdCallHandedOffFrom("NCT") is not { } state)
        {
            return;
        }

        Assert.True(state.FlightPlan.HasFlightPlan);
        Assert.Equal("", state.FlightPlan.Departure);
    }

    [Fact]
    public void HandoffPlan_PresetSquawkStillWins()
    {
        if (Engine(ColdCallScenario(5, "SQV", HandoffJson), []) is not { } engine)
        {
            return;
        }

        LoadedAircraft loaded = ColdCallAircraft(engine, 5, Handoff);
        engine.ApplyAutoTrackConditions(loaded);

        AiTestFixture.Tick(engine, 6);

        Assert.Same(loaded.State, engine.FindAircraft("N655EX"));
        Assert.NotEqual(0u, loaded.State.Transponder.AssignedCode);
        Assert.Equal(1200u, loaded.State.Transponder.Code);
    }

    [Fact]
    public void AircraftWithAPlanKeepsItsCodeThroughTheHandoff()
    {
        if (Engine([]) is not { } engine)
        {
            return;
        }

        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        uint assigned = aircraft.Transponder.AssignedCode;
        uint squawked = aircraft.Transponder.Code;
        var loaded = new LoadedAircraft
        {
            State = aircraft,
            SpawnDelaySeconds = 0,
            AutoTrackConditions = Handoff,
        };

        engine.ApplyAutoTrackConditions(loaded);

        Assert.NotEqual(0u, assigned);
        Assert.Equal(assigned, aircraft.Transponder.AssignedCode);
        Assert.Equal(squawked, aircraft.Transponder.Code);
        Assert.Equal("KOAK", aircraft.FlightPlan.Departure);
    }

    [Fact]
    public void GeneratorSpawnWithAutoTrackIsOwnedBeforeItsSpawnIsRecorded()
    {
        if (_zoa is null || !File.Exists(OakArrivalGeneratorsScenario))
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(OakArrivalGeneratorsScenario), rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);

        // The generators in this scenario carry an autoTrackConfiguration whose position id resolves through the
        // room's ARTCC config — the fallback the scenario's own (unresolved) ATC roster leaves to the config.
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        Assert.NotEmpty(scenario.Generators);
        Assert.All(scenario.Generators, gen => Assert.NotNull(gen.Config.AutoTrackConfiguration));

        var known = engine.World.GetSnapshot().Select(a => a.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AircraftState? generated = null;
        for (int t = 0; t < 600 && generated is null; t++)
        {
            engine.TickOneSecond();
            generated = engine.World.GetSnapshot().FirstOrDefault(a => a.IsGeneratorArrival && !known.Contains(a.Callsign));
        }

        Assert.NotNull(generated);
        Assert.NotNull(generated!.Track.Owner);

        RecordedAircraftSpawn? record = scenario
            .ActionLog.OfType<RecordedAircraftSpawn>()
            .FirstOrDefault(r => r.Aircraft.Callsign.Equals(generated.Callsign, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(record);
        Assert.NotNull(record!.Aircraft.Track.Owner);
    }

    [Fact]
    public void DepartureCrossingTheDisplayFloorIsAutoTrackedByTheAirportsPosition()
    {
        if (Engine(["OAK"]) is not { } engine)
        {
            return;
        }

        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        List<string> lines = CaptureTerminal(engine);

        // Parked at OAK: below the acquisition floor, so the position that auto-tracks OAK departures leaves it.
        AiTestFixture.Tick(engine, 1);
        Assert.Null(aircraft.Track.Owner);

        aircraft.IsOnGround = false;
        aircraft.Altitude = 3000;
        AiTestFixture.Tick(engine, 1);

        Assert.NotNull(aircraft.Track.Owner);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Nct4U));
        Assert.Contains(lines, line => line.Contains("[AutoTrack] On STARS", StringComparison.Ordinal));
    }

    [Fact]
    public void AircraftSquawkingItsAssignedCodeIsAutoTrackedToTheFlightPlanCreator()
    {
        if (Engine([]) is not { } engine)
        {
            return;
        }

        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        List<string> lines = CaptureTerminal(engine);
        aircraft.FlightPlan.CreatedByOwner = Nct4Q;
        aircraft.Transponder.AssignedCode = 1234;
        aircraft.Transponder.Code = 1200;

        AiTestFixture.Tick(engine, 1);
        Assert.Null(aircraft.Track.Owner);

        aircraft.Transponder.Code = 1234;
        AiTestFixture.Tick(engine, 1);

        Assert.NotNull(aircraft.Track.Owner);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Nct4Q));
        Assert.Contains(lines, line => line.Contains("Squawking assigned code", StringComparison.Ordinal));
    }
}
