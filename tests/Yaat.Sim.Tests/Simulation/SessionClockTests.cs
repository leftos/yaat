using System.Text.Json;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The session clock is one pinned instant (<see cref="SimScenarioState.SessionStartUtc"/>): t=0 of the session's
/// elapsed seconds, carried through snapshots and recording manifests and restored on replay. Everything that needs a
/// session time of day derives from it — the running clock (<see cref="SimScenarioState.SimTimeUtc"/>) and the
/// magnetic-model day (<see cref="SimScenarioState.MagneticModelDateUtc"/>), which is applied to every aircraft's
/// declination so a recording made this year computes the same declinations when replayed next year.
/// </summary>
public class SessionClockTests
{
    private static readonly DateTime Day2024 = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2026 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Instant = new DateTime(2026, 6, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_891_234);
    private static readonly LatLon Oak = new(37.7213, -122.2208);

    private const string ScenarioJson = """
        {
          "id": "magdate",
          "name": "Magnetic model date",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N1",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.80, "lon": -122.30 }, "altitude": 3500, "heading": 90, "speed": 100 },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 3500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    public SessionClockTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void DifferentModelDates_GiveDifferentDeclinations_AndSameDateIsStable()
    {
        double a = MagneticDeclination.GetDeclination(Oak, Day2024);
        double b = MagneticDeclination.GetDeclination(Oak, Day2026);

        Assert.NotEqual(a, b);
        Assert.Equal(a, MagneticDeclination.GetDeclination(Oak, Day2024));
        Assert.InRange(Math.Abs(a - b), 0.01, 1.0);
    }

    [Fact]
    public void LoadScenario_AppliesTheModelDate_ToAircraftDeclination()
    {
        var engine2024 = LoadAndTick(Day2024);
        var engine2026 = LoadAndTick(Day2026);
        var engine2024Again = LoadAndTick(Day2024);

        var ac2024 = engine2024.World.GetSnapshot()[0];
        var ac2026 = engine2026.World.GetSnapshot()[0];
        Assert.Equal(Day2024, engine2024.Scenario!.MagneticModelDateUtc);
        Assert.NotEqual(ac2024.Declination, ac2026.Declination);
        Assert.Equal(ac2024.Declination, engine2024Again.World.GetSnapshot()[0].Declination);
        Assert.Equal(MagneticDeclination.GetDeclination(ac2024.Position, Day2024), ac2024.Declination);
        // The scenario's magnetic heading of 090 converts to a different true heading under each model date.
        Assert.NotEqual(ac2024.TrueHeading.Degrees, ac2026.TrueHeading.Degrees);
    }

    [Fact]
    public void LoadScenario_PinsTheInstant_AndSimTimeAdvancesWithElapsed()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(ScenarioJson, 42, Instant);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));

        var scenario = engine.Scenario!;
        Assert.Equal(Instant, scenario.SessionStartUtc);
        Assert.Equal(Instant.Date, scenario.MagneticModelDateUtc);
        Assert.Equal(Instant, scenario.SimTimeUtc);

        engine.TickOneSecond();
        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.Equal(Instant.AddSeconds(3), scenario.SimTimeUtc);
    }

    [Fact]
    public void Snapshot_RoundTripsTheModelDate_AndOlderSnapshotsKeepTheLoadedDate()
    {
        var engine = LoadAndTick(Day2024);
        var snapshot = engine.CaptureSnapshot(0);
        Assert.Equal(Day2024, snapshot.Scenario.SessionStartUtc);

        var restored = new SimulationEngine(new TestAirportGroundData());
        restored.LoadScenario(ScenarioJson, 42, Day2026);
        restored.RestoreFromSnapshot(snapshot);
        Assert.Equal(Day2024, restored.Scenario!.MagneticModelDateUtc);

        var legacyJson = JsonSerializer.Serialize(snapshot).Replace("\"SessionStartUtc\":\"2024-06-01T00:00:00Z\"", "\"SessionStartUtc\":null");
        Assert.NotEqual(JsonSerializer.Serialize(snapshot), legacyJson);
        var legacy = JsonSerializer.Deserialize<StateSnapshotDto>(legacyJson)!;
        var legacyEngine = new SimulationEngine(new TestAirportGroundData());
        legacyEngine.LoadScenario(ScenarioJson, 42, Day2026);
        legacyEngine.RestoreFromSnapshot(legacy);
        Assert.Equal(Day2026, legacyEngine.Scenario!.MagneticModelDateUtc);
    }

    [Fact]
    public void Snapshot_RoundTripsTheInstant_TickPrecise()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(ScenarioJson, 42, Instant);
        var snapshot = engine.CaptureSnapshot(0);
        Assert.Equal(Instant, snapshot.Scenario.SessionStartUtc);

        var serialized = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(Instant, serialized.Scenario.SessionStartUtc);

        var restored = new SimulationEngine(new TestAirportGroundData());
        restored.LoadScenario(ScenarioJson, 42, Day2026);
        restored.RestoreFromSnapshot(serialized);
        Assert.Equal(Instant, restored.Scenario!.SessionStartUtc);
    }

    [Fact]
    public void Manifest_ResolvesTheSessionStart_FromRecordedDate_ForOlderArchives()
    {
        var explicitStart = Manifest(Day2024, Day2026.AddHours(13));
        var recordedOnly = Manifest(null, Day2026.AddHours(13));
        var neither = Manifest(null, null);

        Assert.Equal(Day2024, explicitStart.ResolveSessionStartUtc());
        Assert.Equal(Day2026, recordedOnly.ResolveSessionStartUtc());
        Assert.Equal(SimScenarioState.ProcessDayUtc, neither.ResolveSessionStartUtc());
    }

    [Fact]
    public void Replay_RestoresTheInstant_FromTheManifest()
    {
        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            writer.WriteScenario(ScenarioJson);
            writer.WriteActions([]);
            writer.Finish(
                new RecordingMetadata
                {
                    RngSeed = 42,
                    TotalElapsedSeconds = 0,
                    ScenarioName = "Session clock",
                    ScenarioId = "magdate",
                    RecordedAtUtc = Day2024,
                    SessionStartUtc = Instant,
                }
            );
        }

        ms.Position = 0;
        using var archive = RecordingArchive.Open(ms);
        Assert.Equal(Instant, archive.Manifest.SessionStartUtc);

        var recording = archive.ToBaseSessionRecording();
        Assert.Equal(Instant, recording.SessionStartUtc);

        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 0);

        Assert.Equal(Instant, engine.Scenario!.SessionStartUtc);
    }

    private static RecordingManifest Manifest(DateTime? sessionStartUtc, DateTime? recordedAtUtc) =>
        new()
        {
            Version = 4,
            RngSeed = 1,
            TotalElapsedSeconds = 0,
            ActionCount = 0,
            Snapshots = [],
            SessionStartUtc = sessionStartUtc,
            RecordedAtUtc = recordedAtUtc,
        };

    private static SimulationEngine LoadAndTick(DateTime sessionStartUtc)
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(ScenarioJson, 42, sessionStartUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        engine.TickOneSecond();
        return engine;
    }
}
