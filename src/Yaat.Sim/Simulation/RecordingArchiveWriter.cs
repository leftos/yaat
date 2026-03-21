using System.IO.Compression;
using System.Text.Json;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Streaming writer for v3 recording archives. Snapshots are serialized and flushed
/// to individual ZIP entries one at a time, keeping memory usage at O(world-state)
/// rather than O(snapshots * world-state).
/// </summary>
public sealed class RecordingArchiveWriter : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<SnapshotIndexEntry> _snapshotIndex = [];
    private bool _finished;
    private int _actionCount;
    private bool _hasWeather;

    public RecordingArchiveWriter(Stream output)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    }

    public void WriteScenario(string scenarioJson)
    {
        WriteBrotliEntry("scenario.json.br", scenarioJson);
    }

    public void WriteWeather(string? weatherJson)
    {
        if (weatherJson is null)
        {
            return;
        }

        _hasWeather = true;
        WriteUtf8Entry("weather.json", weatherJson);
    }

    public void WriteActions(List<RecordedAction> actions)
    {
        _actionCount = actions.Count;
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(actions, RecordingJsonOptions.Default);
        WriteBrotliEntry("actions.json.br", jsonBytes);
    }

    /// <summary>
    /// Write a single snapshot to the archive. Call once per snapshot during replay.
    /// The <paramref name="state"/> is serialized immediately and can be GC'd after this returns.
    /// </summary>
    public void WriteSnapshot(int index, double elapsedSeconds, int actionIndex, StateSnapshotDto state)
    {
        _snapshotIndex.Add(new SnapshotIndexEntry { ElapsedSeconds = elapsedSeconds, ActionIndex = actionIndex });

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state, RecordingJsonOptions.Default);
        WriteBrotliEntry($"snapshots/{index:D3}.json.br", jsonBytes);
    }

    /// <summary>
    /// Writes <c>manifest.json</c> as the final entry and closes the archive.
    /// </summary>
    public void Finish(
        string? scenarioName,
        string? scenarioId,
        string? artccId,
        int rngSeed,
        double totalElapsedSeconds,
        DateTime? recordedAtUtc,
        string? recordedBy
    )
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        var manifest = new RecordingManifest
        {
            Version = 3,
            RngSeed = rngSeed,
            TotalElapsedSeconds = totalElapsedSeconds,
            ActionCount = _actionCount,
            HasWeather = _hasWeather,
            ScenarioName = scenarioName,
            ScenarioId = scenarioId,
            ArtccId = artccId,
            RecordedAtUtc = recordedAtUtc,
            RecordedBy = recordedBy,
            Snapshots = _snapshotIndex,
        };

        var manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, RecordingJsonOptions.Default);
        WriteUtf8Entry("manifest.json", manifestJson);

        _zip.Dispose();
    }

    /// <summary>
    /// Convenience method: write a fully-materialized <see cref="SessionRecording"/> to a byte array.
    /// Useful for tests and migration where all data is already in memory.
    /// </summary>
    public static byte[] WriteToBytes(SessionRecording recording)
    {
        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            writer.WriteScenario(recording.ScenarioJson);
            writer.WriteWeather(recording.WeatherJson);
            writer.WriteActions(recording.Actions);

            if (recording.Snapshots is { } snapshots)
            {
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var s = snapshots[i];
                    writer.WriteSnapshot(i, s.ElapsedSeconds, s.ActionIndex, s.State);
                }
            }

            writer.Finish(
                recording.ScenarioName,
                recording.ScenarioId,
                recording.ArtccId,
                recording.RngSeed,
                recording.TotalElapsedSeconds,
                recording.RecordedAtUtc,
                recording.RecordedBy
            );
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _zip.Dispose();
        }
    }

    private void WriteBrotliEntry(string entryName, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        WriteBrotliEntry(entryName, bytes);
    }

    private void WriteBrotliEntry(string entryName, byte[] utf8Bytes)
    {
        // ZIP-level compression is Store; we handle compression ourselves with Brotli
        var entry = _zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var entryStream = entry.Open();
        using var brotli = new BrotliStream(entryStream, CompressionLevel.Optimal);
        brotli.Write(utf8Bytes);
    }

    private void WriteUtf8Entry(string entryName, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        WriteUtf8Entry(entryName, bytes);
    }

    private void WriteUtf8Entry(string entryName, byte[] utf8Bytes)
    {
        var entry = _zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var entryStream = entry.Open();
        entryStream.Write(utf8Bytes);
    }
}
