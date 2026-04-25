using System.IO.Compression;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

public class RecordingArchiveTests
{
    private static SessionRecording CreateTestRecording(int snapshotCount)
    {
        var actions = new List<RecordedAction>
        {
            new RecordedCommand(0, "AAL100", "H270", "LF", "conn1"),
            new RecordedCommand(3, "AAL100", "D050", "LF", "conn1"),
            new RecordedCommand(8, "UAL200", "CVA28R", "LF", "conn1"),
        };

        var snapshots = new List<TimedSnapshot>();
        for (int i = 0; i < snapshotCount; i++)
        {
            snapshots.Add(
                new TimedSnapshot
                {
                    ElapsedSeconds = i * 5,
                    ActionIndex = Math.Min(i, actions.Count - 1),
                    State = CreateMinimalSnapshot(i * 5),
                }
            );
        }

        return new SessionRecording
        {
            Version = 2,
            ScenarioJson = """{"scenarioId":"test-123","name":"Test Scenario"}""",
            RngSeed = 42,
            WeatherJson = """{"metar":"KOAK 010000Z 27010KT 10SM FEW250 20/10 A2992"}""",
            Actions = actions,
            TotalElapsedSeconds = (snapshotCount - 1) * 5,
            Snapshots = snapshots,
            ScenarioName = "Test Scenario",
            ScenarioId = "test-123",
            ArtccId = "ZOA",
            RecordedAtUtc = new DateTime(2026, 3, 21, 12, 0, 0, DateTimeKind.Utc),
            RecordedBy = "unit-test",
        };
    }

    private static StateSnapshotDto CreateMinimalSnapshot(double elapsed)
    {
        return new StateSnapshotDto
        {
            ElapsedSeconds = elapsed,
            Rng = new Yaat.Sim.RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test-123",
                ScenarioName = "Test Scenario",
                RngSeed = 42,
                ElapsedSeconds = elapsed,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = false,
                IsPaused = false,
                SimRate = 1.0,
                AutoAcceptDelaySeconds = 0,
                IsStudentTowerPosition = false,
            },
        };
    }

    [Fact]
    public void WriteToBytes_ThenOpen_RoundTripsManifest()
    {
        var recording = CreateTestRecording(snapshotCount: 4);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);
        var manifest = archive.Manifest;

        Assert.Equal(4, manifest.Version);
        Assert.Equal(42, manifest.RngSeed);
        Assert.Equal(15.0, manifest.TotalElapsedSeconds);
        Assert.Equal(3, manifest.ActionCount);
        Assert.True(manifest.HasWeather);
        Assert.Equal("Test Scenario", manifest.ScenarioName);
        Assert.Equal("test-123", manifest.ScenarioId);
        Assert.Equal("ZOA", manifest.ArtccId);
        Assert.Equal("unit-test", manifest.RecordedBy);
        Assert.Equal(4, manifest.Snapshots.Count);
        Assert.Equal(0.0, manifest.Snapshots[0].ElapsedSeconds);
        Assert.Equal(5.0, manifest.Snapshots[1].ElapsedSeconds);
        Assert.Equal(10.0, manifest.Snapshots[2].ElapsedSeconds);
        Assert.Equal(15.0, manifest.Snapshots[3].ElapsedSeconds);
    }

    [Fact]
    public void ManifestOnly_DoesNotLoadSnapshots()
    {
        var recording = CreateTestRecording(snapshotCount: 10);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        // Reading just the manifest should succeed without loading any snapshot entries
        Assert.Equal(10, archive.Manifest.Snapshots.Count);
        Assert.Equal(45.0, archive.Manifest.TotalElapsedSeconds);
    }

    [Fact]
    public void ReadSnapshot_LoadsSingleEntry()
    {
        var recording = CreateTestRecording(snapshotCount: 5);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        // Load only snapshot at index 2 (t=10)
        var snapshot = archive.ReadSnapshot(2);
        Assert.Equal(10.0, snapshot.ElapsedSeconds);
    }

    [Fact]
    public void ReadTimedSnapshot_CombinesIndexAndState()
    {
        var recording = CreateTestRecording(snapshotCount: 3);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var timed = archive.ReadTimedSnapshot(1);
        Assert.Equal(5.0, timed.ElapsedSeconds);
        Assert.Equal(5.0, timed.State.ElapsedSeconds);
        Assert.Equal(1, timed.ActionIndex);
    }

    [Fact]
    public void ReadScenarioJson_RoundTrips()
    {
        var recording = CreateTestRecording(snapshotCount: 1);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var scenarioJson = archive.ReadScenarioJson();
        Assert.Equal(recording.ScenarioJson, scenarioJson);
    }

    [Fact]
    public void ReadWeatherJson_RoundTrips()
    {
        var recording = CreateTestRecording(snapshotCount: 1);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var weather = archive.ReadWeatherJson();
        Assert.Equal(recording.WeatherJson, weather);
    }

    [Fact]
    public void ReadActions_RoundTrips()
    {
        var recording = CreateTestRecording(snapshotCount: 1);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var actions = archive.ReadActions();
        Assert.Equal(3, actions.Count);

        var cmd = Assert.IsType<RecordedCommand>(actions[0]);
        Assert.Equal("AAL100", cmd.Callsign);
        Assert.Equal("H270", cmd.Command);
    }

    [Fact]
    public void NoWeather_ReturnsNull()
    {
        var recording = new SessionRecording
        {
            Version = 2,
            ScenarioJson = "{}",
            RngSeed = 1,
            WeatherJson = null,
            Actions = [],
            TotalElapsedSeconds = 0,
            Snapshots = [],
        };

        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        Assert.False(archive.Manifest.HasWeather);
        Assert.Null(archive.ReadWeatherJson());
    }

    [Fact]
    public void ToSessionRecording_MaterializesEverything()
    {
        var original = CreateTestRecording(snapshotCount: 3);
        var bytes = RecordingArchiveWriter.WriteToBytes(original);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var restored = archive.ToSessionRecording();
        Assert.Equal(4, restored.Version);
        Assert.Equal(original.ScenarioJson, restored.ScenarioJson);
        Assert.Equal(original.RngSeed, restored.RngSeed);
        Assert.Equal(original.WeatherJson, restored.WeatherJson);
        Assert.Equal(original.Actions.Count, restored.Actions.Count);
        Assert.Equal(original.TotalElapsedSeconds, restored.TotalElapsedSeconds);
        Assert.NotNull(restored.Snapshots);
        Assert.Equal(3, restored.Snapshots!.Count);
        Assert.Equal(original.ScenarioName, restored.ScenarioName);
        Assert.Equal(original.ArtccId, restored.ArtccId);
    }

    [Fact]
    public void IsZipArchive_DetectsV3Format()
    {
        var recording = CreateTestRecording(snapshotCount: 1);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        Assert.True(RecordingCompression.IsZipArchive(bytes));
    }

    [Fact]
    public void IsZipArchive_RejectsBrotli()
    {
        var json = """{"Version":1,"ScenarioJson":"{}","RngSeed":1,"Actions":[],"TotalElapsedSeconds":0}"""u8.ToArray();
        var brotli = RecordingCompression.Compress(json);

        Assert.False(RecordingCompression.IsZipArchive(brotli));
    }

    [Fact]
    public void ZipEntryLayout_MatchesExpectedStructure()
    {
        var recording = CreateTestRecording(snapshotCount: 3);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var entryNames = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        Assert.Contains("manifest.json", entryNames);
        Assert.Contains("scenario.json.br", entryNames);
        Assert.Contains("weather.json", entryNames);
        Assert.Contains("actions.json.br", entryNames);
        Assert.Contains("snapshots/000.json.br", entryNames);
        Assert.Contains("snapshots/001.json.br", entryNames);
        Assert.Contains("snapshots/002.json.br", entryNames);
    }

    [Fact]
    public void ReadLayout_RoundTripsGroundLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10R/28L",
                Coordinates = [(37.72, -122.22), (37.73, -122.23)],
                WidthFt = 150.0,
            }
        );

        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            writer.WriteScenario("{}");
            writer.WriteActions([]);
            writer.WriteLayout(layout);
            writer.Finish("test", "test-1", "ZOA", 42, 0, null, null);
        }

        ms.Position = 0;
        using var archive = RecordingArchive.Open(ms);

        var restored = archive.ReadLayout("KOAK");
        Assert.Equal("KOAK", restored.AirportId);
        Assert.Single(restored.Runways);
        Assert.Equal("10R/28L", restored.Runways[0].Name);
    }

    [Fact]
    public void ReadAllLayouts_ReturnsAllLayouts()
    {
        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            writer.WriteScenario("{}");
            writer.WriteActions([]);
            writer.WriteLayout(new AirportGroundLayout { AirportId = "KOAK" });
            writer.WriteLayout(new AirportGroundLayout { AirportId = "KSFO" });
            writer.Finish("test", "test-1", "ZOA", 42, 0, null, null);
        }

        ms.Position = 0;
        using var archive = RecordingArchive.Open(ms);

        var layouts = archive.ReadAllLayouts();
        Assert.Equal(2, layouts.Count);
        Assert.True(layouts.ContainsKey("KOAK"));
        Assert.True(layouts.ContainsKey("KSFO"));
    }

    [Fact]
    public void SnapshotTimestamps_ReturnsManifestIndex()
    {
        var recording = CreateTestRecording(snapshotCount: 4);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var timestamps = archive.SnapshotTimestamps;
        Assert.Equal(4, timestamps.Count);
        Assert.Equal(0.0, timestamps[0].ElapsedSeconds);
        Assert.Equal(5.0, timestamps[1].ElapsedSeconds);
        Assert.Equal(10.0, timestamps[2].ElapsedSeconds);
        Assert.Equal(15.0, timestamps[3].ElapsedSeconds);
    }

    [Fact]
    public void FindNearestSnapshotIndex_ReturnsClosestBefore()
    {
        var recording = CreateTestRecording(snapshotCount: 4); // t=0, 5, 10, 15
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        Assert.Equal(2, archive.FindNearestSnapshotIndex(12.0)); // closest <= 12 is index 2 (t=10)
        Assert.Equal(3, archive.FindNearestSnapshotIndex(15.0)); // exact match
        Assert.Equal(0, archive.FindNearestSnapshotIndex(0.0)); // exact match at start
        Assert.Equal(3, archive.FindNearestSnapshotIndex(999.0)); // past end -> last
        Assert.Null(archive.FindNearestSnapshotIndex(-1.0)); // before all -> null
    }

    [Fact]
    public void ReadSnapshotAt_LoadsNearestSnapshot()
    {
        var recording = CreateTestRecording(snapshotCount: 4); // t=0, 5, 10, 15
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var snapshot = archive.ReadSnapshotAt(7.0);
        Assert.NotNull(snapshot);
        Assert.Equal(5.0, snapshot!.ElapsedSeconds);
    }

    [Fact]
    public void ToBaseSessionRecording_ExcludesSnapshots()
    {
        var recording = CreateTestRecording(snapshotCount: 10);
        var bytes = RecordingArchiveWriter.WriteToBytes(recording);

        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        var base_ = archive.ToBaseSessionRecording();
        Assert.Equal(recording.ScenarioJson, base_.ScenarioJson);
        Assert.Equal(recording.RngSeed, base_.RngSeed);
        Assert.Equal(recording.WeatherJson, base_.WeatherJson);
        Assert.Equal(3, base_.Actions.Count);
        Assert.Null(base_.Snapshots);
    }

    [Fact]
    public void WriteLayout_CreatesLayoutEntry()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10R/28L",
                Coordinates = [(37.72, -122.22), (37.73, -122.23)],
                WidthFt = 150.0,
            }
        );

        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            writer.WriteScenario("{}");
            writer.WriteActions([]);
            writer.WriteLayout(layout);
            writer.Finish("test", "test-1", "ZOA", 42, 0, null, null);
        }

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("layouts/KOAK.json.br"));

        // Verify manifest declares the layout
        ms.Position = 0;
        using var archive = RecordingArchive.Open(ms);
        Assert.Equal(4, archive.Manifest.Version);
        Assert.NotNull(archive.Manifest.LayoutAirportIds);
        Assert.Contains("KOAK", archive.Manifest.LayoutAirportIds);
    }

    [Fact]
    public void AirportGroundLayout_JsonRoundTrip()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };

        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.72, -122.22),
            Type = GroundNodeType.TaxiwayIntersection,
            Name = "J",
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.73, -122.23),
            Type = GroundNodeType.RunwayHoldShort,
            Name = "HS-28L",
            RunwayId = RunwayIdentifier.Parse("10R/28L"),
            TrueHeading = new TrueHeading(280.0),
        };

        var edge = new GroundEdge
        {
            Nodes = [node1, node2],
            TaxiwayName = "J",
            DistanceNm = 0.05,
            IntermediatePoints = [(37.725, -122.225)],
        };

        node1.Edges.Add(edge);
        node2.Edges.Add(edge);
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Edges.Add(edge);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10R/28L",
                Coordinates = [(37.72, -122.22), (37.73, -122.23)],
                WidthFt = 150.0,
            }
        );

        var json = JsonSerializer.Serialize(layout, RecordingJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<AirportGroundLayout>(json, RecordingJsonOptions.Default)!;
        restored.RebuildAdjacencyLists();

        Assert.Equal("KOAK", restored.AirportId);
        Assert.Equal(2, restored.Nodes.Count);
        Assert.Single(restored.Edges);
        Assert.Single(restored.Runways);

        var restoredNode1 = restored.Nodes[1];
        Assert.Equal("J", restoredNode1.Name);
        Assert.Single(restoredNode1.Edges);
        Assert.Equal("J", restoredNode1.Edges[0].TaxiwayName);
        var restoredEdge = Assert.IsType<GroundEdge>(restoredNode1.Edges[0]);
        Assert.Single(restoredEdge.IntermediatePoints);

        var restoredNode2 = restored.Nodes[2];
        Assert.Equal(GroundNodeType.RunwayHoldShort, restoredNode2.Type);
        Assert.NotNull(restoredNode2.RunwayId);
        Assert.True(restoredNode2.RunwayId.Value.Contains("28L"));
        Assert.NotNull(restoredNode2.TrueHeading);
        Assert.Equal(280.0, restoredNode2.TrueHeading.Value.Degrees, 0.01);

        Assert.Equal("10R/28L", restored.Runways[0].Name);
        Assert.Equal(150.0, restored.Runways[0].WidthFt);
    }

    [Fact]
    public void AircraftState_GroundLayout_ExcludedFromJson()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Ground = new AircraftGroundOps { Layout = layout },
        };

        var json = JsonSerializer.Serialize(ac);

        // Layout object must not appear in JSON
        Assert.DoesNotContain("\"Nodes\"", json);
        Assert.DoesNotContain("\"Edges\"", json);
        // But LayoutAirportId must be preserved
        Assert.Contains("\"LayoutAirportId\"", json);
        Assert.Contains("KOAK", json);
    }

    [Fact]
    public void AircraftState_GroundLayoutAirportId_RoundTrips()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Ground = new AircraftGroundOps { Layout = layout },
        };

        var json = JsonSerializer.Serialize(ac);
        var restored = JsonSerializer.Deserialize<AircraftState>(json)!;

        Assert.Null(restored.Ground.Layout);
        Assert.Equal("KOAK", restored.Ground.LayoutAirportId);
    }

    [Fact]
    public void AircraftState_GroundLayoutAirportId_NullWhenNoLayout()
    {
        var ac = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };

        Assert.Null(ac.Ground.LayoutAirportId);
    }
}
