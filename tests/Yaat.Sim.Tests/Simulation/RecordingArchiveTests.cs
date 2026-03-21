using System.IO.Compression;
using Xunit;
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

        Assert.Equal(3, manifest.Version);
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
        Assert.Equal(3, restored.Version);
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
}
