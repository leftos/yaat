using Xunit;
using Yaat.Sim.Commands;
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

    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

    private readonly Data.Vnas.ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AutoTrackStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The bare engine over the parked-at-OAK fixture with the student and the two NCT positions resolved the way
    /// the server's load resolves them. <paramref name="autoTrackAirportIds"/> goes on the 4U position, which is
    /// what the deferred autotrack pass matches a departure against.
    /// </summary>
    private SimulationEngine? Engine(List<string> autoTrackAirportIds)
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
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

        var scenario = engine.Scenario!;
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

        var queued = Assert.Single(scenario.DelayedHandoffQueue);
        Assert.Equal(AiTestFixture.Callsign, queued.Callsign);
        Assert.True(queued.Target.MatchesPosition(Student));
        Assert.Equal(30, queued.FireAtSeconds);
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

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(File.ReadAllText(OakArrivalGeneratorsScenario), rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);

        // The generators in this scenario carry an autoTrackConfiguration whose position id resolves through the
        // room's ARTCC config — the fallback the scenario's own (unresolved) ATC roster leaves to the config.
        var scenario = engine.Scenario!;
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

        var record = scenario
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

        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        var lines = CaptureTerminal(engine);

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

        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        var lines = CaptureTerminal(engine);
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
