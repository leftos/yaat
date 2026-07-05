using System.IO.Compression;
using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The surgical upgrader migrates recorded snapshots to the current schema by transforming JSON in
/// place — never by re-simulating. Each test asserts the schema bumps AND a distinctive recorded field
/// (aircraft altitude) is preserved byte-for-byte, which is what proves no re-simulation happened.
/// </summary>
public class RecordingSchemaUpgraderTests
{
    private const double DistinctiveAltitude = 12345.0;
    private const int CurrentVersion = SnapshotSchemaMigrator.CurrentSchemaVersion;

    // A snapshot pinned at schema 3 with an empty filed aircraft type — the 3→4 migration seeds the
    // filed type from the top-level type, so a correct surgical upgrade produces FlightPlan.AircraftType
    // == "B738" while leaving Altitude untouched.
    private static StateSnapshotDto SnapshotAtV3()
    {
        var aircraft = new AircraftState
        {
            Callsign = "AAL1",
            AircraftType = "B738",
            Altitude = DistinctiveAltitude,
        }.ToSnapshot();

        return new StateSnapshotDto
        {
            SchemaVersion = 3,
            ElapsedSeconds = 5,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [aircraft],
            Scenario = MinimalScenario(5),
        };
    }

    private static ScenarioSnapshotDto MinimalScenario(double elapsed) =>
        new()
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
        };

    private static SessionRecording RecordingWith(StateSnapshotDto state) =>
        new()
        {
            Version = 2,
            ScenarioJson = "{}",
            RngSeed = 42,
            Actions = [],
            TotalElapsedSeconds = 5,
            Snapshots =
            [
                new TimedSnapshot
                {
                    ElapsedSeconds = state.ElapsedSeconds,
                    ActionIndex = 0,
                    State = state,
                },
            ],
        };

    private static void AssertMigratedAircraft(StateSnapshotDto snapshot)
    {
        Assert.Equal(CurrentVersion, snapshot.SchemaVersion);
        var aircraft = Assert.Single(snapshot.Aircraft);
        Assert.Equal("B738", aircraft.FlightPlan.AircraftType); // 3→4 seed applied
        Assert.Equal(DistinctiveAltitude, aircraft.Altitude); // preserved — NOT re-simulated
    }

    [Fact]
    public void Upgrade_NonZipSessionRecording_MigratesSnapshotsInPlace()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(RecordingWith(SnapshotAtV3()), RecordingJsonOptions.Default);
        var input = RecordingCompression.Compress(json);

        var result = RecordingSchemaUpgrader.Upgrade(input);

        Assert.True(result.Changed);
        Assert.False(result.NeedsResimulation);
        var upgraded = JsonSerializer.Deserialize<SessionRecording>(RecordingCompression.Decompress(result.Output), RecordingJsonOptions.Default);
        AssertMigratedAircraft(upgraded!.Snapshots![0].State);
    }

    [Fact]
    public void Upgrade_V4Archive_MigratesSnapshotsAndPreservesOtherEntries()
    {
        var input = BuildArchiveWithLayout(SnapshotAtV3());

        var result = RecordingSchemaUpgrader.Upgrade(input);

        Assert.True(result.Changed);
        Assert.False(result.NeedsResimulation);
        using var archive = RecordingArchive.Open(new MemoryStream(result.Output));
        AssertMigratedAircraft(archive.ReadSnapshot(0));
        // The unrelated layout entry survives the surgical rewrite verbatim.
        Assert.Equal("KOAK", archive.ReadLayout("KOAK").AirportId);
    }

    [Fact]
    public void Upgrade_NestedBugReportBundle_MigratesInnerArchiveAndPreservesLogs()
    {
        var innerArchive = BuildArchiveWithLayout(SnapshotAtV3());
        var bundle = BuildBundle(innerArchive, ("logs/yaat-server.log", "room-scoped log text"));

        var result = RecordingSchemaUpgrader.Upgrade(bundle);

        Assert.True(result.Changed);
        Assert.False(result.NeedsResimulation);
        var newInner = ReadEntry(result.Output, "recording.yaat-recording.zip");
        using var archive = RecordingArchive.Open(new MemoryStream(newInner));
        AssertMigratedAircraft(archive.ReadSnapshot(0));
        // The bug-bundle log entry is preserved untouched.
        Assert.Equal("room-scoped log text", System.Text.Encoding.UTF8.GetString(ReadEntry(result.Output, "logs/yaat-server.log")));
    }

    [Fact]
    public void Upgrade_AlreadyCurrentArchive_ReturnsUnchanged()
    {
        var current = new StateSnapshotDto
        {
            ElapsedSeconds = 5,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = MinimalScenario(5),
        };
        Assert.Equal(CurrentVersion, current.SchemaVersion); // default is current
        var input = RecordingArchiveWriter.WriteToBytes(RecordingWith(current));

        var result = RecordingSchemaUpgrader.Upgrade(input);

        Assert.False(result.Changed);
        Assert.False(result.NeedsResimulation);
        Assert.Same(input, result.Output);
    }

    [Fact]
    public void Upgrade_V1RecordingWithoutSnapshots_ReportsNeedsResimulation()
    {
        var v1 = new SessionRecording
        {
            Version = 1,
            ScenarioJson = "{}",
            RngSeed = 42,
            Actions = [],
            TotalElapsedSeconds = 0,
            Snapshots = null,
        };
        var input = RecordingCompression.Compress(JsonSerializer.SerializeToUtf8Bytes(v1, RecordingJsonOptions.Default));

        var result = RecordingSchemaUpgrader.Upgrade(input);

        Assert.True(result.NeedsResimulation);
        Assert.False(result.Changed);
    }

    // --- fixtures ---

    private static byte[] BuildArchiveWithLayout(StateSnapshotDto snapshot)
    {
        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            writer.WriteScenario("{}");
            writer.WriteActions([]);
            writer.WriteSnapshot(0, snapshot.ElapsedSeconds, 0, snapshot);
            writer.WriteLayout(new AirportGroundLayout { AirportId = "KOAK" });
            writer.Finish(
                new RecordingMetadata
                {
                    RngSeed = 42,
                    TotalElapsedSeconds = 5,
                    ScenarioName = "t",
                    ScenarioId = "t-1",
                    ArtccId = "ZOA",
                }
            );
        }

        return ms.ToArray();
    }

    private static byte[] BuildBundle(byte[] innerArchive, params (string Name, string Text)[] extraEntries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var inner = zip.CreateEntry("recording.yaat-recording.zip", CompressionLevel.Optimal);
            using (var s = inner.Open())
            {
                s.Write(innerArchive);
            }

            foreach (var (name, text) in extraEntries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes(text));
            }
        }

        return ms.ToArray();
    }

    private static byte[] ReadEntry(byte[] zipBytes, string entryName)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Entry not found: {entryName}");
        using var es = entry.Open();
        using var outMs = new MemoryStream();
        es.CopyTo(outMs);
        return outMs.ToArray();
    }
}
