using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

public class CompressionRoundTripTests
{
    [Fact]
    public void SessionRecording_BrotliRoundTrips()
    {
        var recording = new SessionRecording
        {
            Version = 2,
            ScenarioJson = "{\"id\":\"test\"}",
            RngSeed = 42,
            Actions = [new RecordedCommand(0, "AAL100", "CM 100", "CD", "conn1")],
            TotalElapsedSeconds = 30,
            Snapshots =
            [
                new TimedSnapshot
                {
                    ElapsedSeconds = 0,
                    ActionIndex = 0,
                    State = new StateSnapshotDto
                    {
                        ElapsedSeconds = 0,
                        Rng = new RngState(1, 2, 3, 4),
                        Aircraft = [],
                        Scenario = new ScenarioSnapshotDto
                        {
                            ScenarioId = "test",
                            ScenarioName = "Test",
                            RngSeed = 42,
                            ElapsedSeconds = 0,
                            AutoClearedToLand = false,
                            AutoCrossRunway = false,
                            ValidateDctFixes = true,
                            IsPaused = false,
                            SimRate = 1,
                            AutoAcceptDelaySeconds = 5,
                            IsStudentTowerPosition = false,
                        },
                    },
                },
            ],
            ScenarioName = "Test Scenario",
        };

        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // Serialize → compress
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(recording, options);
        var compressed = RecordingCompression.Compress(jsonBytes);

        // Verify compressed is smaller
        Assert.True(compressed.Length < jsonBytes.Length);

        // Decompress → deserialize
        var decompressedJson = RecordingCompression.Decompress(compressed);
        var deserialized = JsonSerializer.Deserialize<SessionRecording>(
            decompressedJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Version);
        Assert.Equal(recording.RngSeed, deserialized.RngSeed);
        Assert.Equal(recording.ScenarioName, deserialized.ScenarioName);
        Assert.True(deserialized.HasSnapshots);
        Assert.Single(deserialized.Snapshots!);
        Assert.Single(deserialized.Actions);
    }

    [Fact]
    public void PlainJson_DecompressesTransparently()
    {
        var json = "{\"Version\": 1, \"ScenarioJson\": \"{}\", \"RngSeed\": 42, \"Actions\": [], \"TotalElapsedSeconds\": 0}";
        var bytes = Encoding.UTF8.GetBytes(json);

        var result = RecordingCompression.Decompress(bytes);
        Assert.Equal(json, result);
    }

    [Fact]
    public void GzipLegacy_DecompressesTransparently()
    {
        var json = "{\"test\": true}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        // Compress with gzip
        byte[] gzipBytes;
        using (var output = new MemoryStream())
        {
            using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(jsonBytes);
            }
            gzipBytes = output.ToArray();
        }

        // RecordingCompression should detect and decompress gzip
        var result = RecordingCompression.Decompress(gzipBytes);
        Assert.Equal(json, result);
    }
}
