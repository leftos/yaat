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

    /// <summary>Both aircraft tracked by one ERAM sector, alongside each other at the same altitude, before any conflict pass.</summary>
    private static SimulationEngine Load()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        List<string> warnings = engine.LoadScenario(ScenarioJson, 42, SessionStart);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        engine.FindAircraft("AAL100")!.Track.Owner = Sector44;
        engine.FindAircraft("UAL200")!.Track.Owner = Sector44;
        return engine;
    }

    /// <summary>The <see cref="Load"/> pair after one conflict pass at t=0: one active alert.</summary>
    private static SimulationEngine LoadInConflict()
    {
        SimulationEngine engine = Load();

        engine.TickEramConflictAlerts();

        Assert.NotNull(engine.EramConflicts.FindPair("AAL100", "UAL200"));
        return engine;
    }

    /// <summary>Turns UAL200 into a Mode-C intruder: untracked, no flight plan, a discrete beacon code.</summary>
    private static AircraftState MakeModeCIntruder(SimulationEngine engine)
    {
        AircraftState ual = engine.FindAircraft("UAL200")!;
        ual.Track.Owner = null;
        ual.FlightPlan.HasFlightPlan = false;
        ual.Transponder.Code = 4521;
        return ual;
    }

    /// <summary>
    /// Puts both aircraft at <paramref name="altitudeFeet"/> with UAL200 10 nm east of AAL100 and head-on to it: outside
    /// minima now, and a conflict within the look-ahead.
    /// </summary>
    private static void PlaceHeadOn(SimulationEngine engine, double altitudeFeet)
    {
        AircraftState aal = engine.FindAircraft("AAL100")!;
        AircraftState ual = engine.FindAircraft("UAL200")!;
        aal.Altitude = altitudeFeet;
        ual.Altitude = altitudeFeet;
        double tenNmLon = 10.0 / (60.0 * Math.Cos(aal.Position.Lat * Math.PI / 180.0));
        ual.Position = new LatLon(aal.Position.Lat, aal.Position.Lon + tenNmLon);
        aal.TrueHeading = new TrueHeading(90);
        aal.TrueTrack = new TrueHeading(90);
        ual.TrueHeading = new TrueHeading(270);
        ual.TrueTrack = new TrueHeading(270);
    }

    [Fact]
    public void ConflictPass_RunsOnlyOnFiveSecondBoundaries()
    {
        SimulationEngine engine = Load();

        engine.Scenario!.ElapsedSeconds = 3;
        EramConflictAlertChanges offPass = engine.TickEramConflictAlerts();
        Assert.Empty(offPass.New);
        Assert.Null(engine.EramConflicts.FindPair("AAL100", "UAL200"));

        engine.Scenario.ElapsedSeconds = 5;
        EramConflictAlertChanges onPass = engine.TickEramConflictAlerts();
        EramActiveConflict alert = Assert.Single(onPass.New);

        // 50 nm apart: no longer a conflict, but only the next pass may clear it.
        AircraftState ual = engine.FindAircraft("UAL200")!;
        ual.Position = new LatLon(ual.Position.Lat + (50.0 / 60.0), ual.Position.Lon);

        engine.Scenario.ElapsedSeconds = 6;
        EramConflictAlertChanges offPassAfter = engine.TickEramConflictAlerts();
        Assert.Empty(offPassAfter.Cleared);
        Assert.NotNull(engine.EramConflicts.FindPair("AAL100", "UAL200"));

        engine.Scenario.ElapsedSeconds = 10;
        EramConflictAlertChanges nextPass = engine.TickEramConflictAlerts();
        Assert.Equal([alert.Id], nextPass.Cleared);
        Assert.Null(engine.EramConflicts.FindPair("AAL100", "UAL200"));
    }

    [Fact]
    public void DeletingOneAircraftOfAnActivePair_OffPass_DropsTheAlertTheSameSecond()
    {
        SimulationEngine engine = LoadInConflict();
        string id = engine.EramConflicts.FindPair("AAL100", "UAL200")!.Id;

        engine.Scenario!.ElapsedSeconds = 3;
        engine.DeleteAircraft("UAL200", "DEL");

        Assert.Empty(engine.EramConflicts.Conflicts);
        Assert.Equal([id], engine.EramConflicts.TakeRemovedWith("UAL200"));
        Assert.Empty(engine.EramConflicts.TakeRemovedWith("UAL200"));
        EramConflictAlertChanges changes = engine.TickEramConflictAlerts();
        Assert.Empty(changes.New);
        Assert.Empty(changes.Restored);
    }

    [Fact]
    public void Suppression_IsReportedOffPass()
    {
        SimulationEngine engine = LoadInConflict();
        EramActiveConflict alert = engine.EramConflicts.FindPair("AAL100", "UAL200")!;

        engine.Scenario!.ElapsedSeconds = 3;
        Assert.True(Suppress(engine).Success);
        EramConflictAlertChanges changes = engine.TickEramConflictAlerts();

        Assert.Equal([alert.Id], changes.Suppressed);
        Assert.Empty(changes.New);
        Assert.Empty(changes.Cleared);
    }

    [Fact]
    public void SnapshotRestore_DropsTheIdsHeldForARemovedAircraft()
    {
        SimulationEngine engine = LoadInConflict();
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        engine.DeleteAircraft("UAL200", "DEL");

        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.EramConflicts.TakeRemovedWith("UAL200"));
    }

    [Fact]
    public void ModeCIntruderInsideMinimaAtFirstDetection_Alerts()
    {
        // Alongside, same altitude: already inside minima, an ordinary current alert (only immediate alerts are exempt).
        SimulationEngine engine = Load();
        MakeModeCIntruder(engine);

        EramActiveConflict alert = Assert.Single(engine.TickEramConflictAlerts().New);

        Assert.Equal("UAL200", alert.IntruderCallsign);
    }

    [Fact]
    public void ModeCIntruderLeavingTheBand_ClearsTheActiveAlert()
    {
        SimulationEngine engine = Load();
        MakeModeCIntruder(engine);
        PlaceHeadOn(engine, 13000);
        EramActiveConflict alert = Assert.Single(engine.TickEramConflictAlerts().New);

        // Still converging, but now below the 12,500 ft default floor.
        engine.FindAircraft("UAL200")!.Altitude = 12000;
        engine.Scenario!.ElapsedSeconds = 5;
        EramConflictAlertChanges changes = engine.TickEramConflictAlerts();

        Assert.Equal([alert.Id], changes.Cleared);
        Assert.Null(engine.EramConflicts.FindPair("AAL100", "UAL200"));
    }

    [Fact]
    public void ModeCIntruderBelowDefaultFloor_DoesNotAlert()
    {
        SimulationEngine engine = Load();
        MakeModeCIntruder(engine);
        PlaceHeadOn(engine, 12000);

        Assert.Empty(engine.TickEramConflictAlerts().New);
        Assert.Null(engine.EramConflicts.FindPair("AAL100", "UAL200"));

        // The same geometry above the 12,500 ft default floor alerts, so the floor is what dropped it.
        PlaceHeadOn(engine, 13000);
        engine.Scenario!.ElapsedSeconds = 5;
        EramActiveConflict alert = Assert.Single(engine.TickEramConflictAlerts().New);
        Assert.Equal("UAL200", alert.IntruderCallsign);
    }

    /// <summary>UAL200 untracked and squawking 1200 with its filed flight plan: 1200 never correlates, so it is a Mode-C intruder.</summary>
    private static void MakeCode1200Intruder(SimulationEngine engine)
    {
        AircraftState ual = engine.FindAircraft("UAL200")!;
        ual.Track.Owner = null;
        ual.Transponder.Code = 1200;
        Assert.True(ual.FlightPlan.HasFlightPlan);
    }

    [Fact]
    public void Code1200Intruder_AlertsInsideTheMciBand_NotBelowTheFloor()
    {
        SimulationEngine inBand = Load();
        MakeCode1200Intruder(inBand);
        EramActiveConflict alert = Assert.Single(inBand.TickEramConflictAlerts().New);
        Assert.Equal("UAL200", alert.IntruderCallsign);

        SimulationEngine belowFloor = Load();
        MakeCode1200Intruder(belowFloor);
        PlaceHeadOn(belowFloor, 12000);
        Assert.Empty(belowFloor.TickEramConflictAlerts().New);
        Assert.Null(belowFloor.EramConflicts.FindPair("AAL100", "UAL200"));
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
