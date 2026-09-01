using System.Text.Json;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The magnetic-model evaluation day is session state (<see cref="SimScenarioState.MagneticModelDateUtc"/>): it is
/// applied to every aircraft's declination, carried through snapshots and recording manifests, and restored on
/// replay — so a recording made this year computes the same declinations when replayed next year.
/// </summary>
public class MagneticModelDateTests
{
    private static readonly DateTime Day2024 = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2026 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
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

    public MagneticModelDateTests()
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
    public void Snapshot_RoundTripsTheModelDate_AndOlderSnapshotsKeepTheLoadedDate()
    {
        var engine = LoadAndTick(Day2024);
        var snapshot = engine.CaptureSnapshot(0);
        Assert.Equal(Day2024, snapshot.Scenario.MagneticModelDateUtc);

        var restored = new SimulationEngine(new TestAirportGroundData());
        restored.LoadScenario(ScenarioJson, 42, Day2026);
        restored.RestoreFromSnapshot(snapshot);
        Assert.Equal(Day2024, restored.Scenario!.MagneticModelDateUtc);

        var legacyJson = JsonSerializer
            .Serialize(snapshot)
            .Replace("\"MagneticModelDateUtc\":\"2024-06-01T00:00:00Z\"", "\"MagneticModelDateUtc\":null");
        Assert.NotEqual(JsonSerializer.Serialize(snapshot), legacyJson);
        var legacy = JsonSerializer.Deserialize<StateSnapshotDto>(legacyJson)!;
        var legacyEngine = new SimulationEngine(new TestAirportGroundData());
        legacyEngine.LoadScenario(ScenarioJson, 42, Day2026);
        legacyEngine.RestoreFromSnapshot(legacy);
        Assert.Equal(Day2026, legacyEngine.Scenario!.MagneticModelDateUtc);
    }

    [Fact]
    public void Manifest_ResolvesTheModelDate_FromRecordedDate_ForOlderArchives()
    {
        var explicitDate = Manifest(Day2024, Day2026.AddHours(13));
        var recordedOnly = Manifest(null, Day2026.AddHours(13));
        var neither = Manifest(null, null);

        Assert.Equal(Day2024, explicitDate.ResolveMagneticModelDateUtc());
        Assert.Equal(Day2026, recordedOnly.ResolveMagneticModelDateUtc());
        Assert.Equal(MagneticDeclination.EvaluationDateUtc, neither.ResolveMagneticModelDateUtc());
    }

    private static RecordingManifest Manifest(DateTime? magneticModelDateUtc, DateTime? recordedAtUtc) =>
        new()
        {
            Version = 4,
            RngSeed = 1,
            TotalElapsedSeconds = 0,
            ActionCount = 0,
            Snapshots = [],
            MagneticModelDateUtc = magneticModelDateUtc,
            RecordedAtUtc = recordedAtUtc,
        };

    private static SimulationEngine LoadAndTick(DateTime magneticModelDateUtc)
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(ScenarioJson, 42, magneticModelDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        engine.TickOneSecond();
        return engine;
    }
}
