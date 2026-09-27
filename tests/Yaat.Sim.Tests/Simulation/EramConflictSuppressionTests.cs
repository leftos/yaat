using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The ERAM Conflict Suppress entry (<c>CO</c>) marks one active alert suppressed (7110.65 §5-13-1c.1). The suppression
/// belongs to that alert: once the pair stops conflicting the alert is gone, and a new conflict between the same pair
/// alerts again. The flag is engine state, so it rides the snapshot.
/// </summary>
public class EramConflictSuppressionTests
{
    private static readonly DateTime SessionStart = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private const string ScenarioJson = """
        {
          "id": "eram-co",
          "name": "ERAM conflict suppress",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "AAL100",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.80, "lon": -122.30 }, "altitude": 20000, "heading": 90, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 20000, "cruiseSpeed": 250, "route": "", "remarks": "", "aircraftType": "B738" }
            },
            {
              "id": "a2",
              "aircraftId": "UAL200",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.80, "lon": -122.29 }, "altitude": 20000, "heading": 90, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 20000, "cruiseSpeed": 250, "route": "", "remarks": "", "aircraftType": "B738" }
            }
          ]
        }
        """;

    private static readonly TrackOwner Sector44 = TrackOwner.CreateEram("ZOA_44_CTR", "ZOA", "44");

    public EramConflictSuppressionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>Both aircraft tracked by one ERAM sector, alongside each other at the same altitude: one active alert.</summary>
    private static SimulationEngine LoadInConflict()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        List<string> warnings = engine.LoadScenario(ScenarioJson, 42, SessionStart);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        engine.FindAircraft("AAL100")!.Track.Owner = Sector44;
        engine.FindAircraft("UAL200")!.Track.Owner = Sector44;

        engine.TickEramConflictAlerts();

        Assert.NotNull(engine.EramConflicts.FindPair("AAL100", "UAL200"));
        return engine;
    }

    private static CommandResult Suppress(SimulationEngine engine) =>
        engine.Actions.IssueDerived(new RecordedEramEntry(0, "AAL100", "CO UAL200", null));

    [Fact]
    public void SuppressedAlert_ThatClearsAndReopens_ComesBackUnsuppressed()
    {
        SimulationEngine engine = LoadInConflict();
        CommandResult suppressed = Suppress(engine);
        Assert.True(suppressed.Success, suppressed.Message);
        Assert.True(engine.EramConflicts.FindPair("AAL100", "UAL200")!.Suppressed);

        AircraftState ual = engine.FindAircraft("UAL200")!;
        LatLon alongside = ual.Position;
        ual.Position = new LatLon(38.80, -122.29);
        engine.TickEramConflictAlerts();
        Assert.Null(engine.EramConflicts.FindPair("AAL100", "UAL200"));

        ual.Position = alongside;
        engine.TickEramConflictAlerts();

        EramActiveConflict reopened = Assert.IsType<EramActiveConflict>(engine.EramConflicts.FindPair("AAL100", "UAL200"));
        Assert.False(reopened.Suppressed);
    }

    [Fact]
    public void ConflictPass_ReportsTheSuppressionOnce_ThenTheRestore()
    {
        SimulationEngine engine = LoadInConflict();
        EramActiveConflict alert = engine.EramConflicts.FindPair("AAL100", "UAL200")!;
        Assert.True(Suppress(engine).Success);

        EramConflictAlertChanges suppressed = engine.TickEramConflictAlerts();
        EramConflictAlertChanges quiet = engine.TickEramConflictAlerts();
        Assert.True(Suppress(engine).Success);
        EramConflictAlertChanges restored = engine.TickEramConflictAlerts();

        Assert.Equal([alert.Id], suppressed.Suppressed);
        Assert.Empty(suppressed.Restored);
        Assert.Empty(quiet.Suppressed);
        Assert.Empty(quiet.Restored);
        Assert.Empty(restored.Suppressed);
        Assert.Same(alert, Assert.Single(restored.Restored));
        Assert.Empty(restored.New);
        Assert.Empty(restored.Cleared);
    }

    [Fact]
    public void Snapshot_CarriesTheSuppression_ToAFreshEngine()
    {
        SimulationEngine engine = LoadInConflict();
        Assert.True(Suppress(engine).Success);
        engine.TickEramConflictAlerts();

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        StateSnapshotDto serialized = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;

        var restored = new SimulationEngine(new TestAirportGroundData());
        restored.LoadScenario(ScenarioJson, 42, SessionStart);
        restored.RestoreFromSnapshot(serialized);

        EramActiveConflict alert = restored.EramConflicts.FindPair("AAL100", "UAL200")!;
        Assert.True(alert.Suppressed);
        Assert.True(alert.PublishedSuppressed);
    }
}
