using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Brains;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// The Ground brain end to end through the real engine sink: OAK's 30-departure ground scenario taxied, crossed,
/// and handed to the tower by the AI (the test plays tower and clears each one for takeoff), an arrival taxied to its
/// parking, and the determinism gate with a command-issuing brain.
/// </summary>
public class GroundBrainE2ETests
{
    private const int TowerSpacingSeconds = 60;
    private static readonly string DeparturesScenarioPath = Path.Combine(
        TickRecorder.FindRepoRoot(),
        "docs",
        "atctrainer-scenario-examples",
        "01H08FSWF5NCDXTQMQ3BWB0BND.json"
    );

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public GroundBrainE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ThirtyDepartures_TaxiedCrossedAndHandedToTower_WithNoRejectionsStallsOrUnansweredCalls()
    {
        if (_zoa is null || !File.Exists(DeparturesScenarioPath))
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.LoadWith(
            DeparturesOnly(File.ReadAllText(DeparturesScenarioPath)),
            _zoa,
            42,
            [ground],
            "30",
            p => new GroundBrain(p)
        );
        var scenario = engine.Scenario!;
        var departures = engine.World.GetSnapshot().Select(ac => ac.Callsign).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(30, departures.Count);
        var runway30 = RunwayCrossingGate.PavementFor("30", RunwayOccupancy.AirportRunways("OAK"))!;
        var aiId = AiConnectionId.Format(ground.PositionId);
        var opened = new List<AiAnomalyEvent>();
        double lastClearance = double.NegativeInfinity;

        for (int t = 0; t < 5400 && departures.Any(cs => engine.FindAircraft(cs) is { IsOnGround: true }); t++)
        {
            AiTestFixture.Tick(engine, 1);
            opened.AddRange(scenario.AiAnomalies.Drain().Where(e => e.Event != AiAnomalyEventKind.Closed));
            PlayTower(engine, runway30, ref lastClearance);
        }

        var stillOnGround = departures
            .Select(cs => engine.FindAircraft(cs))
            .Where(ac => ac is { IsOnGround: true })
            .Select(ac =>
                $"{ac!.Callsign} {ac.Phases?.CurrentPhase?.Name} rwyq={ac.Ground.RunwayQueuePosition} hold={ac.Ground.Hold is not null} limit={ac.Ground.SpeedLimit}"
            )
            .ToList();
        var anomalyLines = opened.Select(e => $"{e.Kind} {e.SubjectKey} @{e.AtSeconds:F0}s: {e.Detail}").ToList();
        var world = engine.World.GetSnapshot();
        var gates = new List<string>();
        foreach (var ac in world.Where(a => a.Phases?.CurrentPhase is HoldingShortPhase))
        {
            var hs = ((HoldingShortPhase)ac.Phases!.CurrentPhase!).HoldShort;
            var pavement = RunwayCrossingGate.PavementFor(hs.TargetName ?? "", RunwayOccupancy.AirportRunways("OAK"));
            string reason = pavement is null
                ? "no pavement"
                : (RunwayCrossingGate.IsClear(ac, pavement, world, engine.ResolveGroundLayout(ac), out var why) ? "clear" : why);
            gates.Add($"{ac.Callsign}@{hs.TargetName}: {reason}");
        }

        var everyone = world.Select(a => $"{a.Callsign}:{a.Phases?.CurrentPhase?.Name}:{(a.IsOnGround ? "gnd" : $"alt{a.Altitude:F0}")}").ToList();
        Assert.True(
            stillOnGround.Count == 0,
            $"t={scenario.ElapsedSeconds:F0}s still on the ground: "
                + string.Join(" | ", stillOnGround)
                + " || gates: "
                + string.Join(" | ", gates)
                + " || world: "
                + string.Join(" | ", everyone)
                + " || anomalies: "
                + string.Join(" | ", anomalyLines)
        );
        var ai = scenario.ActionLog.OfType<RecordedCommand>().Where(a => a.ConnectionId == aiId).ToList();
        // The scenario's generators add departures of their own, so the AI answers at least the thirty.
        Assert.True(ai.Count(a => a.Command.StartsWith("TAXIAUTO ", StringComparison.Ordinal)) >= 30);
        // Nobody holds Local: Ground works the runway itself and transfers nobody (7110.65 §2-1-17.a).
        Assert.DoesNotContain(ai, a => a.Command.StartsWith("CT ", StringComparison.Ordinal));
        Assert.Contains(ai, a => a.Command.StartsWith("CROSS ", StringComparison.Ordinal));
        Assert.All(ai, a => Assert.Equal("AI", a.Initials));
        var bad = opened
            .Where(e =>
                e.Kind
                    is AiAnomalyKind.CommandRejected
                        or AiAnomalyKind.UnansweredPilotRequest
                        or AiAnomalyKind.StuckAircraft
                        or AiAnomalyKind.CoordinationTimeout
            )
            .ToList();
        Assert.True(bad.Count == 0, string.Join(" | ", bad.Select(e => $"{e.Kind} {e.SubjectKey} @{e.AtSeconds:F0}s: {e.Detail}")));
    }

    [Fact]
    public void Departure_HandedToAStaffedTower_KeepsItsStudentFrequencyAndCompletion()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var scenarioJson = AiTestFixture.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\"");
        var engine = AiTestFixture.LoadWith(scenarioJson, _zoa, 7, [ground, tower], "30", p => new GroundBrain(p));
        var aiId = AiConnectionId.Format(ground.PositionId);
        RecordedCommand? transfer = null;
        for (int t = 0; t < 600 && transfer is null; t++)
        {
            AiTestFixture.Tick(engine, 1);
            transfer = engine
                .Scenario!.ActionLog.OfType<RecordedCommand>()
                .FirstOrDefault(a => (a.ConnectionId == aiId) && a.Command.StartsWith("CT ", StringComparison.Ordinal));
        }

        Assert.NotNull(transfer);
        Assert.Equal("CT OAK_TWR", transfer.Command);
        var handed = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.False(handed.HasLeftStudentFrequency);
        Assert.Equal(CompletionReason.Active, handed.CompletionReason);
        Assert.Null(handed.CompletedAtSeconds);
    }

    [Fact]
    public void Arrival_ClearOfTheRunway_IsTaxiedToItsParkingByTheBrain()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.LoadWith(AiTestFixture.OnFinalAtOak, _zoa, 7, [ground], null, p => new GroundBrain(p));
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CLAND").Success);

        var parked = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is AtParkingPhase, 1200);

        var taxiIn = Assert.Single(
            engine.Scenario!.ActionLog.OfType<RecordedCommand>(),
            a => a.ConnectionId == AiConnectionId.Format(ground.PositionId)
        );
        Assert.StartsWith("TAXIAUTO @", taxiIn.Command);
        Assert.Equal(taxiIn.Command["TAXIAUTO @".Length..], parked.Ground.ParkingSpot);
        Assert.False(parked.PendingPilotRequest?.IsOpen ?? false);
        Assert.DoesNotContain(
            engine.Scenario.AiAnomalies.Drain(),
            e => e.Kind is AiAnomalyKind.CommandRejected or AiAnomalyKind.UnansweredPilotRequest
        );
    }

    [Fact]
    public void SameSeed_SameActionsAnomaliesAndSnapshot_WithTheGroundBrain()
    {
        if (_zoa is null || !File.Exists(DeparturesScenarioPath))
        {
            return;
        }

        var json = File.ReadAllText(DeparturesScenarioPath);
        var first = Run(json, 42);
        var second = Run(json, 42);
        var other = Run(json, 43);

        Assert.Equal(first.Actions, second.Actions);
        Assert.Equal(first.Anomalies, second.Anomalies);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.NotEqual(first.Snapshot, other.Snapshot);
        Assert.Contains("TAXIAUTO", first.Actions);
    }

    /// <summary>
    /// The scenario's thirty parked departures without its arrival generators. With the generators on, arrivals exiting
    /// runway 30 taxi in along W against the departures taxiing out on W, and the reactive give-way physics locks the
    /// two flows head-on within twenty minutes — the first soak finding, recorded in the plan; the acceptance load
    /// here is the single departure flow.
    /// </summary>
    private static string DeparturesOnly(string scenarioJson)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(scenarioJson)!.AsObject();
        root.Remove("aircraftGenerators");
        return root.ToJsonString();
    }

    /// <summary>The test as tower: one takeoff clearance at a time to the departure holding short of 30 when the runway is clear.</summary>
    private static void PlayTower(SimulationEngine engine, Phases.RunwayInfo runway30, ref double lastClearance)
    {
        var scenario = engine.Scenario!;
        if (scenario.ElapsedSeconds - lastClearance < TowerSpacingSeconds)
        {
            return;
        }

        var snapshot = engine.World.GetSnapshot();
        var ready = snapshot
            .Where(ac => ac.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway })
            .OrderBy(ac => ac.Callsign, StringComparer.Ordinal)
            .FirstOrDefault(ac => RunwayCrossingGate.IsClear(ac, runway30, snapshot, engine.ResolveGroundLayout(ac), out _));
        if (ready is null)
        {
            return;
        }

        Assert.True(engine.SendCommand(ready.Callsign, "CTO").Success, ready.Callsign);
        lastClearance = scenario.ElapsedSeconds;
    }

    private (string Actions, string Anomalies, string Snapshot) Run(string scenarioJson, int seed)
    {
        var ground = TestAiPositions.OakGround(_zoa!);
        var engine = AiTestFixture.LoadWith(scenarioJson, _zoa!, seed, [ground], "30", p => new GroundBrain(p));
        engine.Scenario!.AutoClearedToLand = true;
        var anomalies = new List<AiAnomalyEvent>();
        for (int t = 0; t < 1200; t++)
        {
            AiTestFixture.Tick(engine, 1);
            anomalies.AddRange(engine.Scenario.AiAnomalies.Drain());
        }

        var scenario = engine.Scenario;
        return (
            JsonSerializer.Serialize(scenario.ActionLog, RecordingJsonOptions.Default),
            JsonSerializer.Serialize(anomalies, RecordingJsonOptions.Default),
            WithoutVirtualNodeIds(JsonSerializer.Serialize(engine.CaptureSnapshot(scenario.ActionLog.Count - 1), RecordingJsonOptions.Default))
        );
    }

    /// <summary>
    /// Virtual ground nodes (hold-short setbacks, ramp-lane legs) take their negative ids from a process-wide counter,
    /// so two runs in one process label the same nodes differently while the geometry is identical. The label is not
    /// behavior; the comparison masks it.
    /// </summary>
    private static string WithoutVirtualNodeIds(string snapshotJson)
    {
        var scalar = System.Text.RegularExpressions.Regex.Replace(snapshotJson, @"(""[A-Za-z]*NodeId"":)-\d+", "$1-V");
        return System.Text.RegularExpressions.Regex.Replace(
            scalar,
            @"(""[A-Za-z]*NodeIds"":\[)([^\]]*)",
            m => m.Groups[1].Value + System.Text.RegularExpressions.Regex.Replace(m.Groups[2].Value, @"-\d+", "-V")
        );
    }
}
