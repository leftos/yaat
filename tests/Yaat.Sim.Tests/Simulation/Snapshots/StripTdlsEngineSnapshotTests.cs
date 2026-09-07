using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// Strips and the vTDLS session are engine state (<see cref="SimulationEngine.Strips"/> /
/// <see cref="SimulationEngine.Tdls"/>): a fresh engine starts empty, <see cref="SimulationEngine.CaptureSnapshot"/>
/// carries both in the snapshot's server section, and a restore replaces whatever the target engine held —
/// including from a pre-feature snapshot, which restores empty.
/// </summary>
public class StripTdlsEngineSnapshotTests
{
    private static readonly DateTime SessionStart = new(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);

    private const string ScenarioJson = """
        {
          "id": "strips",
          "name": "Strips and TDLS",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "AAL100",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.80, "lon": -122.30 }, "altitude": 3500, "heading": 90, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 250, "route": "", "remarks": "", "aircraftType": "B738" }
            }
          ]
        }
        """;

    private static readonly TdlsClearance Clearance = new()
    {
        Expect = "10 MIN AFT DP",
        Sid = "OAKLAND4",
        Transition = "ALTAM",
        Climbvia = "CLIMB VIA SID",
        InitialAlt = "5000",
        DepFreq = "120.9",
    };

    public StripTdlsEngineSnapshotTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine Load()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(ScenarioJson, 42, SessionStart);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        return engine;
    }

    private static void Seed(SimulationEngine engine)
    {
        lock (engine.Strips.Gate)
        {
            engine.Strips.Items["STRIP_AAL100"] = new StripItemRecord(
                "STRIP_AAL100",
                "AAL100",
                0,
                false,
                ["AAL100", "B738", "", "", "", "", "", "KLAX", ""],
                "OAK",
                "bay-ground",
                0,
                0
            );
            engine.Strips.Bays["bay-ground"] = new Dictionary<string, List<string>[]> { ["0"] = [new List<string> { "STRIP_AAL100" }] };
            engine.Strips.DeparturePrinterQueue.Add("STRIP_AAL100");
            engine.Strips.NextBlankId = 4;
        }

        lock (engine.Tdls.Gate)
        {
            engine.Tdls.Items["TDLS_1"] = new TdlsItemRecord(
                Id: "TDLS_1",
                AircraftId: "AAL100",
                Cid: null,
                FacilityId: "OAK",
                Status: TdlsItemStatus.Sent,
                Sequence: 1,
                CreatedUtc: SessionStart,
                SentUtc: SessionStart.AddSeconds(5),
                WilcoUtc: null,
                ExpiresUtc: SessionStart.AddHours(2),
                SentPayload: Clearance
            );
            engine.Tdls.ScheduledWilcoAt["TDLS_1"] = SessionStart.AddSeconds(8);
            engine.Tdls.NextItemId = 2;
        }
    }

    [Fact]
    public void AFreshEngine_StartsWithNoStripsAndNoPdcs()
    {
        var engine = Load();

        Assert.Empty(engine.Strips.Items);
        Assert.Empty(engine.Strips.Bays);
        Assert.Empty(engine.Tdls.Items);
        Assert.Equal(1, engine.Tdls.NextItemId);
    }

    [Fact]
    public void ASnapshot_CarriesTheStripsAndThePdc_ToAFreshEngine()
    {
        var engine = Load();
        Seed(engine);

        var snapshot = engine.CaptureSnapshot(actionIndex: 0);
        var serialized = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;

        var restored = Load();
        restored.RestoreFromSnapshot(serialized);

        Assert.True(restored.Strips.Items.ContainsKey("STRIP_AAL100"));
        Assert.Equal("AAL100", restored.Strips.Items["STRIP_AAL100"].AircraftId);
        Assert.Equal(["STRIP_AAL100"], restored.Strips.Bays["bay-ground"]["0"][0]);
        Assert.Equal(["STRIP_AAL100"], restored.Strips.DeparturePrinterQueue);
        Assert.Equal(4, restored.Strips.NextBlankId);

        Assert.True(restored.Tdls.Items.ContainsKey("TDLS_1"));
        var pdc = restored.Tdls.Items["TDLS_1"];
        Assert.Equal(TdlsItemStatus.Sent, pdc.Status);
        Assert.Equal(Clearance, pdc.SentPayload);
        Assert.Equal(SessionStart.AddSeconds(8), restored.Tdls.ScheduledWilcoAt["TDLS_1"]);
        Assert.Equal(2, restored.Tdls.NextItemId);
    }

    [Fact]
    public void ASnapshotWithNoStripOrTdlsSection_RestoresEmpty()
    {
        var engine = Load();
        Seed(engine);
        var snapshot = engine.CaptureSnapshot(actionIndex: 0);

        // A pre-feature snapshot: the server section is there, its strip and TDLS slices are not.
        var preFeature = new StateSnapshotDto
        {
            SchemaVersion = snapshot.SchemaVersion,
            ElapsedSeconds = snapshot.ElapsedSeconds,
            Rng = snapshot.Rng,
            WeatherJson = snapshot.WeatherJson,
            Aircraft = snapshot.Aircraft,
            Scenario = snapshot.Scenario,
            Server = new ServerSnapshotDto { AttendedPositionIds = [] },
        };

        engine.RestoreFromSnapshot(preFeature);

        Assert.Empty(engine.Strips.Items);
        Assert.Empty(engine.Strips.DeparturePrinterQueue);
        Assert.Equal(1, engine.Strips.NextBlankId);
        Assert.Empty(engine.Tdls.Items);
        Assert.Empty(engine.Tdls.ScheduledWilcoAt);
        Assert.Equal(1, engine.Tdls.NextItemId);
    }

    /// <summary>
    /// The rack skeleton is the ARTCC bay configuration's, re-derived by the load that runs before a restore — so a
    /// snapshot with no strip section clears the strips and leaves the racks to print into.
    /// </summary>
    [Fact]
    public void ASnapshotWithNoStripSection_KeepsTheBaysRackSlots()
    {
        var engine = Load();
        Seed(engine);
        // The load re-derives the rack skeleton before the restore runs, exactly as PopulateRoom does.
        engine.Strips.InitializeFromArtcc([
            new StripBayConfig
            {
                Id = "bay-ground",
                Name = "Ground",
                NumberOfRacks = 2,
            },
        ]);

        var snapshot = engine.CaptureSnapshot(actionIndex: 0);
        var preFeature = new StateSnapshotDto
        {
            SchemaVersion = snapshot.SchemaVersion,
            ElapsedSeconds = snapshot.ElapsedSeconds,
            Rng = snapshot.Rng,
            WeatherJson = snapshot.WeatherJson,
            Aircraft = snapshot.Aircraft,
            Scenario = snapshot.Scenario,
            Server = new ServerSnapshotDto { AttendedPositionIds = [] },
        };

        engine.RestoreFromSnapshot(preFeature);

        Assert.Empty(engine.Strips.Items);
        Assert.Equal(["0", "1"], engine.Strips.Bays["bay-ground"].Keys.Order());
    }

    /// <summary>A schema-21 snapshot has no <c>Server.Strips</c>; the migrator adds no data and the restore reads that as empty.</summary>
    [Fact]
    public void ASchema21Snapshot_MigratesAndRestoresEmptyStrips()
    {
        const string json = """
            {
              "SchemaVersion": 21,
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "Aircraft": [],
              "Scenario": { "ScenarioId": "strips", "ScenarioName": "Strips and TDLS", "RngSeed": 42, "ElapsedSeconds": 10, "SimRate": 1, "PrimaryAirportId": "OAK" },
              "Server": { "AttendedPositionIds": [] }
            }
            """;
        var snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Null(snapshot.Server!.Strips);
        Assert.Null(snapshot.Server.Tdls);

        var engine = Load();
        Seed(engine);
        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.Strips.Items);
        Assert.Empty(engine.Tdls.Items);
    }

    /// <summary>A replay from second 0 on a reused engine is a fresh run: it must not inherit the strips or PDCs of the last one.</summary>
    [Fact]
    public void AReplayFromSecondZero_StartsWithNoStripsAndNoPdcs()
    {
        var engine = Load();
        Seed(engine);
        engine.Tdls.Configs["OAK"] = new TdlsConfig { MandatoryExpect = true };

        engine.ReplayFromStartTo(0, []);

        Assert.Empty(engine.Strips.Items);
        Assert.Empty(engine.Tdls.Items);
        Assert.Empty(engine.Tdls.ScheduledWilcoAt);
        Assert.Equal(1, engine.Tdls.NextItemId);

        // The facility configuration is the scenario load's, not the run's — a replay reset keeps it.
        Assert.True(engine.Tdls.Configs.ContainsKey("OAK"));
    }
}
