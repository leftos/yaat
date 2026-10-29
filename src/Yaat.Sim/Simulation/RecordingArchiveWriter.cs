using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Streaming writer for v3 recording archives. Snapshots are serialized and flushed
/// to individual ZIP entries one at a time, keeping memory usage at O(world-state)
/// rather than O(snapshots * world-state).
/// </summary>
public sealed class RecordingArchiveWriter(Stream output) : ILayoutBundleWriter, IDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger("RecordingArchiveWriter");

    private readonly ZipArchive _zip = new(output, ZipArchiveMode.Create, leaveOpen: true);
    private readonly List<string> _windGridHashes = [];
    private readonly List<SnapshotIndexEntry> _snapshotIndex = [];
    private readonly List<string> _layoutAirportIds = [];
    private readonly List<string> _airportGeoJsonIds = [];
    private readonly List<string> _missingLayoutAirportIds = [];
    private readonly HashSet<string> _airportGeoJsonIdSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _finished;
    private int _actionCount;
    private bool _hasWeather;
    private bool _metarReissuanceEnabled;
    private bool _hasArtccConfig;
    private bool _hasToweredAirports;
    private bool _hasTerminalLog;

    public void WriteScenario(string scenarioJson) => WriteBrotliEntry("scenario.json.br", scenarioJson);

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

    /// <summary>
    /// Writes the room's towered airport ids (<see cref="SimScenarioState.ToweredAirports"/>, built from its own, roster and
    /// neighbour ARTCC configs, of which the archive bundles only the own one) into <c>towered-airports.json.br</c>, so a
    /// replay judges an airport-level visual clearance against the same set. No-op when null.
    /// </summary>
    public void WriteToweredAirports(IReadOnlyList<string>? toweredAirportIds)
    {
        if (toweredAirportIds is null)
        {
            return;
        }

        _hasToweredAirports = true;
        WriteBrotliEntry("towered-airports.json.br", JsonSerializer.SerializeToUtf8Bytes(toweredAirportIds, RecordingJsonOptions.Default));
    }

    public void WriteActions(List<RecordedAction> actions)
    {
        _actionCount = actions.Count;
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(actions, RecordingJsonOptions.Default);
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
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(terminalLog, RecordingJsonOptions.Default);
        WriteBrotliEntry("terminal-log.json.br", jsonBytes);
    }

    /// <summary>
    /// Write a single snapshot to the archive. Call once per snapshot during replay.
    /// The <paramref name="state"/> is serialized immediately and can be GC'd after this returns.
    /// </summary>
    public void WriteSnapshot(int index, double elapsedSeconds, int actionIndex, StateSnapshotDto state)
    {
        _snapshotIndex.Add(new SnapshotIndexEntry { ElapsedSeconds = elapsedSeconds, ActionIndex = actionIndex });

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state, RecordingJsonOptions.Default);
        WriteBrotliEntry($"snapshots/{index:D3}.json.br", jsonBytes);
    }

    /// <summary>
    /// Write a ground layout to the archive. Call once per unique airport.
    /// </summary>
    public void WriteLayout(AirportGroundLayout layout)
    {
        _layoutAirportIds.Add(layout.AirportId);
        WriteLayoutEntry(_zip, layout);
    }

    /// <summary>
    /// Writes <paramref name="layout"/> into <paramref name="zip"/> under its airport ID, as a recording archive stores it;
    /// <see cref="RecordingArchive.ReadLayoutEntry"/> reads it back. The caller lists the ID in its manifest.
    /// </summary>
    public static void WriteLayoutEntry(ZipArchive zip, AirportGroundLayout layout) =>
        WriteBrotliEntry(zip, RecordingArchive.LayoutEntryName(layout.AirportId), GroundLayoutSerializer.Serialize(layout));

    /// <summary>
    /// Writes an airport's source GeoJSON into <paramref name="zip"/> under <paramref name="airportId"/>, as a recording
    /// archive stores it; <see cref="RecordingArchive.ReadAirportGeoJsonEntry"/> reads it back. The caller lists the ID in its
    /// manifest.
    /// </summary>
    public static void WriteAirportGeoJsonEntry(ZipArchive zip, string airportId, string geoJson) =>
        WriteBrotliEntry(zip, RecordingArchive.AirportGeoJsonEntryName(airportId), System.Text.Encoding.UTF8.GetBytes(geoJson));

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
        WriteAirportGeoJsonEntry(_zip, airportId, geoJson);
    }

    /// <summary>
    /// Writes a GFS wind grid's raw GRIB2 bytes under its hash (<see cref="RecordingArchive.WindGridEntryName"/>, Brotli) and
    /// lists the hash in the manifest. A hash already written is skipped, so a grid loaded several times is stored once.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="grib2"/> does not hash to <paramref name="hash"/>.</exception>
    public void WriteWindGrid(string hash, ReadOnlySpan<byte> grib2) => WriteWindGridEntry(_zip, _windGridHashes, hash, grib2);

    /// <summary>
    /// Writes a GFS wind grid's raw GRIB2 bytes into <paramref name="zip"/> under its hash
    /// (<see cref="RecordingArchive.WindGridEntryName"/>, Brotli) and adds the hash to <paramref name="writtenHashes"/>, the
    /// list the caller's manifest names, unless that list already holds it; <see cref="RecordingArchive.ReadWindGridEntry"/>
    /// reads it back. A recording and a room checkpoint store their grids this way.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="grib2"/> does not hash to <paramref name="hash"/>.</exception>
    public static void WriteWindGridEntry(ZipArchive zip, List<string> writtenHashes, string hash, ReadOnlySpan<byte> grib2)
    {
        string actual = WindGridRegistry.HashOf(grib2);
        if (!string.Equals(actual, hash, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The wind grid's bytes hash to {actual}, not to {hash}, the name it was to be written under", nameof(hash));
        }

        if (writtenHashes.Contains(hash))
        {
            return;
        }

        writtenHashes.Add(hash);
        WriteBrotliEntry(zip, RecordingArchive.WindGridEntryName(hash), grib2);
    }

    /// <summary>Records the airports (by FAA code) the room had pinned with no map, so that a load pins them with none.</summary>
    public void WriteMissingLayoutAirportIds(IEnumerable<string> faaCodes) => _missingLayoutAirportIds.AddRange(faaCodes);

    /// <summary>
    /// Write an arbitrary entry to the archive (e.g., log files from bug report bundles).
    /// </summary>
    public void WriteExtraEntry(string entryName, byte[] data)
    {
        ZipArchiveEntry entry = _zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
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
            HasToweredAirports = _hasToweredAirports,
            HasTerminalLog = _hasTerminalLog,
            ScenarioName = metadata.ScenarioName,
            ScenarioId = metadata.ScenarioId,
            ArtccId = metadata.ArtccId,
            RecordedAtUtc = metadata.RecordedAtUtc,
            RecordedBy = metadata.RecordedBy,
            SessionStartUtc = metadata.SessionStartUtc,
            InitialActiveRunways = metadata.InitialActiveRunways,
            ClientVersion = metadata.ClientVersion,
            ClientBuildKind = metadata.ClientBuildKind,
            ServerVersion = metadata.ServerVersion,
            ServerName = metadata.ServerName,
            Snapshots = _snapshotIndex,
            LayoutAirportIds = _layoutAirportIds.Count > 0 ? _layoutAirportIds : null,
            AirportGeoJsonIds = _airportGeoJsonIds.Count > 0 ? _airportGeoJsonIds : null,
            LayoutFormatVersion = RecordingArchive.CurrentLayoutFormatVersion,
            MissingLayoutAirportIds = _missingLayoutAirportIds,
            WindGridHashes = _windGridHashes.Count > 0 ? _windGridHashes : null,
        };

        byte[] manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, RecordingJsonOptions.Default);
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
            writer.WriteToweredAirports(recording.ToweredAirportIds);
            writer.WriteActions(recording.Actions);
            writer.WriteTerminalLog(recording.TerminalLog);
            WriteReferencedWindGrids(writer, recording.WeatherJson, recording.Actions, recording.Snapshots?.Select(s => s.State.WeatherJson) ?? []);

            if (recording.Snapshots is { } snapshots)
            {
                for (int i = 0; i < snapshots.Count; i++)
                {
                    TimedSnapshot s = snapshots[i];
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
                    InitialActiveRunways = recording.InitialActiveRunways,
                }
            );
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Writes every GFS grid a recording's weather names (<see cref="WindGridHashes.Referenced"/>), each once, from
    /// <see cref="WindGridRegistry"/>. A grid the registry does not hold is left out with a warning; a replay of the archive
    /// then flies the FD wind layers where it was named.
    /// </summary>
    /// <param name="writer">The archive being written; call before <see cref="Finish"/>.</param>
    /// <param name="initialWeatherJson">The weather the recording starts on, or null without one.</param>
    /// <param name="actions">The recording's action log; its weather changes name grids.</param>
    /// <param name="snapshotWeatherJsons">The weather text of each snapshot the archive carries, or none when they are not at hand.</param>
    public static void WriteReferencedWindGrids(
        RecordingArchiveWriter writer,
        string? initialWeatherJson,
        IEnumerable<RecordedAction> actions,
        IEnumerable<string?> snapshotWeatherJsons
    )
    {
        foreach (string hash in WindGridHashes.Referenced(initialWeatherJson, actions, snapshotWeatherJsons))
        {
            if (WindGridRegistry.TryGetBytes(hash, out ReadOnlyMemory<byte> grib2))
            {
                writer.WriteWindGrid(hash, grib2.Span);
            }
            else
            {
                Log.LogWarning("GFS wind grid {Hash} named by the recording's weather is not registered; the archive is written without it", hash);
            }
        }
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
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        WriteBrotliEntry(entryName, bytes);
    }

    private void WriteBrotliEntry(string entryName, byte[] utf8Bytes) => WriteBrotliEntry(_zip, entryName, utf8Bytes);

    private static void WriteBrotliEntry(ZipArchive zip, string entryName, ReadOnlySpan<byte> utf8Bytes)
    {
        // ZIP-level compression is Store; we handle compression ourselves with Brotli
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        using Stream entryStream = entry.Open();
        using var brotli = new BrotliStream(entryStream, CompressionLevel.Optimal);
        brotli.Write(utf8Bytes);
    }

    private void WriteUtf8Entry(string entryName, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        WriteUtf8Entry(entryName, bytes);
    }

    private void WriteUtf8Entry(string entryName, byte[] utf8Bytes)
    {
        ZipArchiveEntry entry = _zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        using Stream entryStream = entry.Open();
        entryStream.Write(utf8Bytes);
    }
}
