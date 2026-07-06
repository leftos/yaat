using System.IO.Compression;
using System.Text.Json;
using Yaat.Sim.Data.Airport;
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
    private readonly List<string> _layoutAirportIds = [];
    private readonly List<string> _airportGeoJsonIds = [];
    private readonly HashSet<string> _airportGeoJsonIdSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _finished;
    private int _actionCount;
    private bool _hasWeather;
    private bool _metarReissuanceEnabled;
    private bool _hasArtccConfig;
    private bool _hasTerminalLog;

    public RecordingArchiveWriter(Stream output)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    }

    public void WriteScenario(string scenarioJson)
    {
        WriteBrotliEntry("scenario.json.br", scenarioJson);
    }

    public void WriteWeather(string? weatherJson, bool metarReissuanceEnabled)
    {
        if (weatherJson is null)
        {
            return;
        }

        _hasWeather = true;
        _metarReissuanceEnabled = metarReissuanceEnabled;
        WriteUtf8Entry("weather.json", weatherJson);
    }

    /// <summary>
    /// Writes the ARTCC configuration JSON used at record time. Bundling the
    /// config makes recordings self-contained — replay no longer depends on the
    /// live ArtccConfigService having the same ARTCC version loaded. No-op
    /// when <paramref name="artccConfigJson"/> is null.
    /// </summary>
    public void WriteArtccConfig(string? artccConfigJson)
    {
        if (artccConfigJson is null)
        {
            return;
        }

        _hasArtccConfig = true;
        WriteBrotliEntry("artcc-config.json.br", artccConfigJson);
    }

    public void WriteActions(List<RecordedAction> actions)
    {
        _actionCount = actions.Count;
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(actions, RecordingJsonOptions.Default);
        WriteBrotliEntry("actions.json.br", jsonBytes);
    }

    /// <summary>
    /// Writes the room's broadcast terminal log (commands, responses, SAY, warnings, chat, …) so a
    /// loaded recording repopulates the full terminal and every line is a replay-scrub target. No-op
    /// when empty; the manifest flag stays false so older/empty archives read back an empty log.
    /// </summary>
    public void WriteTerminalLog(IReadOnlyList<RecordedTerminalEntry> terminalLog)
    {
        if (terminalLog.Count == 0)
        {
            return;
        }

        _hasTerminalLog = true;
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(terminalLog, RecordingJsonOptions.Default);
        WriteBrotliEntry("terminal-log.json.br", jsonBytes);
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
    /// Write a ground layout to the archive. Call once per unique airport.
    /// </summary>
    public void WriteLayout(AirportGroundLayout layout)
    {
        _layoutAirportIds.Add(layout.AirportId);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(layout, RecordingJsonOptions.Default);
        WriteBrotliEntry($"layouts/{layout.AirportId}.json.br", jsonBytes);
    }

    public void WriteAirportGeoJson(string airportId, string geoJson)
    {
        if (string.IsNullOrWhiteSpace(airportId))
        {
            throw new ArgumentException("Airport ID is required.", nameof(airportId));
        }

        if (string.IsNullOrWhiteSpace(geoJson))
        {
            throw new ArgumentException("Airport source GeoJSON is required.", nameof(geoJson));
        }

        if (!_airportGeoJsonIdSet.Add(airportId))
        {
            return;
        }

        _airportGeoJsonIds.Add(airportId);
        WriteBrotliEntry($"airport-geojson/{airportId}.geojson.br", geoJson);
    }

    /// <summary>
    /// Write an arbitrary entry to the archive (e.g., log files from bug report bundles).
    /// </summary>
    public void WriteExtraEntry(string entryName, byte[] data)
    {
        var entry = _zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(data);
    }

    /// <summary>
    /// Writes <c>manifest.json</c> as the final entry and closes the archive.
    /// </summary>
    public void Finish(RecordingMetadata metadata)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        var manifest = new RecordingManifest
        {
            Version = 4,
            RngSeed = metadata.RngSeed,
            TotalElapsedSeconds = metadata.TotalElapsedSeconds,
            ActionCount = _actionCount,
            HasWeather = _hasWeather,
            MetarReissuanceEnabled = _metarReissuanceEnabled,
            HasArtccConfig = _hasArtccConfig,
            HasTerminalLog = _hasTerminalLog,
            ScenarioName = metadata.ScenarioName,
            ScenarioId = metadata.ScenarioId,
            ArtccId = metadata.ArtccId,
            RecordedAtUtc = metadata.RecordedAtUtc,
            RecordedBy = metadata.RecordedBy,
            ClientVersion = metadata.ClientVersion,
            ClientBuildKind = metadata.ClientBuildKind,
            ServerVersion = metadata.ServerVersion,
            Snapshots = _snapshotIndex,
            LayoutAirportIds = _layoutAirportIds.Count > 0 ? _layoutAirportIds : null,
            AirportGeoJsonIds = _airportGeoJsonIds.Count > 0 ? _airportGeoJsonIds : null,
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
            writer.WriteWeather(recording.WeatherJson, recording.MetarReissuanceEnabled);
            writer.WriteArtccConfig(recording.ArtccConfigJson);
            writer.WriteActions(recording.Actions);
            writer.WriteTerminalLog(recording.TerminalLog);

            if (recording.Snapshots is { } snapshots)
            {
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var s = snapshots[i];
                    writer.WriteSnapshot(i, s.ElapsedSeconds, s.ActionIndex, s.State);
                }
            }

            writer.Finish(
                new RecordingMetadata
                {
                    RngSeed = recording.RngSeed,
                    TotalElapsedSeconds = recording.TotalElapsedSeconds,
                    ScenarioName = recording.ScenarioName,
                    ScenarioId = recording.ScenarioId,
                    ArtccId = recording.ArtccId,
                    RecordedAtUtc = recording.RecordedAtUtc,
                    RecordedBy = recording.RecordedBy,
                }
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
