using System.IO.Compression;
using System.Text.Json;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Reader for v3 recording archives. Reads the manifest eagerly on construction;
/// scenario, actions, weather, and individual snapshots are loaded on demand.
/// Holds the underlying <see cref="ZipArchive"/> open until disposed.
/// </summary>
public sealed class RecordingArchive : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Stream? _ownedStream;

    public RecordingManifest Manifest { get; }

    private RecordingArchive(ZipArchive zip, Stream? ownedStream)
    {
        _zip = zip;
        _ownedStream = ownedStream;
        Manifest = ReadManifest();
    }

    /// <summary>
    /// Open an archive from an already-open stream.
    /// The stream must remain readable for the lifetime of this archive.
    /// </summary>
    public static RecordingArchive Open(Stream stream)
    {
        var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return new RecordingArchive(zip, ownedStream: null);
    }

    /// <summary>
    /// Open an archive from a file path. The file is kept open until <see cref="Dispose"/>.
    /// </summary>
    public static RecordingArchive Open(string filePath)
    {
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        return new RecordingArchive(zip, ownedStream: fs);
    }

    public string ReadScenarioJson()
    {
        return ReadBrotliEntry("scenario.json.br");
    }

    public string? ReadWeatherJson()
    {
        if (!Manifest.HasWeather)
        {
            return null;
        }

        var entry = _zip.GetEntry("weather.json");
        if (entry is null)
        {
            return null;
        }

        return ReadUtf8Entry(entry);
    }

    public List<RecordedAction> ReadActions()
    {
        var json = ReadBrotliEntry("actions.json.br");
        return JsonSerializer.Deserialize<List<RecordedAction>>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize actions from recording archive.");
    }

    /// <summary>
    /// Load a single snapshot by its ordinal index (0-based, matching the manifest's Snapshots list).
    /// </summary>
    public StateSnapshotDto ReadSnapshot(int index)
    {
        var json = ReadBrotliEntry($"snapshots/{index:D3}.json.br");
        return JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize snapshot {index} from recording archive.");
    }

    /// <summary>
    /// Load a single snapshot and combine it with its manifest index entry
    /// to produce a <see cref="TimedSnapshot"/>.
    /// </summary>
    public TimedSnapshot ReadTimedSnapshot(int index)
    {
        var indexEntry = Manifest.Snapshots[index];
        var state = ReadSnapshot(index);
        return new TimedSnapshot
        {
            ElapsedSeconds = indexEntry.ElapsedSeconds,
            ActionIndex = indexEntry.ActionIndex,
            State = state,
        };
    }

    // --- Layout reading ---

    /// <summary>
    /// Read a ground layout stored in the archive by airport ID.
    /// </summary>
    public AirportGroundLayout ReadLayout(string airportId)
    {
        var json = ReadBrotliEntry($"layouts/{airportId}.json.br");
        return JsonSerializer.Deserialize<AirportGroundLayout>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize layout for {airportId}.");
    }

    /// <summary>
    /// Read all ground layouts declared in the manifest.
    /// </summary>
    public Dictionary<string, AirportGroundLayout> ReadAllLayouts()
    {
        var layouts = new Dictionary<string, AirportGroundLayout>(StringComparer.OrdinalIgnoreCase);
        if (Manifest.LayoutAirportIds is not { Count: > 0 })
        {
            return layouts;
        }

        foreach (var airportId in Manifest.LayoutAirportIds)
        {
            layouts[airportId] = ReadLayout(airportId);
        }

        return layouts;
    }

    // --- Snapshot seek API ---

    /// <summary>
    /// Available snapshot timestamps from the manifest index. Each entry contains
    /// the snapshot's elapsed seconds and action index — no snapshot data is loaded.
    /// </summary>
    public IReadOnlyList<SnapshotIndexEntry> SnapshotTimestamps => Manifest.Snapshots;

    /// <summary>
    /// Find the index of the snapshot whose ElapsedSeconds is closest to (but not after)
    /// <paramref name="targetSeconds"/>. Returns null if no snapshots exist or all are
    /// after the target time. Uses binary search.
    /// </summary>
    public int? FindNearestSnapshotIndex(double targetSeconds)
    {
        var snapshots = Manifest.Snapshots;
        if (snapshots.Count == 0)
        {
            return null;
        }

        int lo = 0;
        int hi = snapshots.Count - 1;
        int result = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (snapshots[mid].ElapsedSeconds <= targetSeconds)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result >= 0 ? result : null;
    }

    /// <summary>
    /// Load the snapshot closest to (but not after) <paramref name="targetSeconds"/>.
    /// Returns null if no suitable snapshot exists.
    /// </summary>
    public TimedSnapshot? ReadSnapshotAt(double targetSeconds)
    {
        var index = FindNearestSnapshotIndex(targetSeconds);
        return index.HasValue ? ReadTimedSnapshot(index.Value) : null;
    }

    // --- SessionRecording materialization ---

    /// <summary>
    /// Load the recording without any snapshots: actions, scenario, weather, and metadata only.
    /// Use this for replay and tests that don't need snapshot data.
    /// </summary>
    public SessionRecording ToBaseSessionRecording()
    {
        var actions = ReadActions();
        return new SessionRecording
        {
            Version = Manifest.Version,
            ScenarioJson = ReadScenarioJson(),
            RngSeed = Manifest.RngSeed,
            WeatherJson = ReadWeatherJson(),
            Actions = actions,
            TotalElapsedSeconds = Manifest.TotalElapsedSeconds,
            Snapshots = null,
            ScenarioName = Manifest.ScenarioName,
            ScenarioId = Manifest.ScenarioId,
            ArtccId = Manifest.ArtccId,
            RecordedAtUtc = Manifest.RecordedAtUtc,
            RecordedBy = Manifest.RecordedBy,
        };
    }

    /// <summary>
    /// Materialize the full <see cref="SessionRecording"/> by reading all entries.
    /// Reattaches ground layouts from archive entries to delayed spawn aircraft.
    /// Use sparingly — this defeats the purpose of on-demand loading.
    /// </summary>
    public SessionRecording ToSessionRecording()
    {
        var layouts = ReadAllLayouts();
        var actions = ReadActions();

        List<TimedSnapshot>? snapshots = null;
        if (Manifest.Snapshots.Count > 0)
        {
            snapshots = new List<TimedSnapshot>(Manifest.Snapshots.Count);
            for (int i = 0; i < Manifest.Snapshots.Count; i++)
            {
                var timed = ReadTimedSnapshot(i);
                ReattachDelayedSpawnLayouts(timed.State, layouts);
                snapshots.Add(timed);
            }
        }

        return new SessionRecording
        {
            Version = Manifest.Version,
            ScenarioJson = ReadScenarioJson(),
            RngSeed = Manifest.RngSeed,
            WeatherJson = ReadWeatherJson(),
            Actions = actions,
            TotalElapsedSeconds = Manifest.TotalElapsedSeconds,
            Snapshots = snapshots,
            ScenarioName = Manifest.ScenarioName,
            ScenarioId = Manifest.ScenarioId,
            ArtccId = Manifest.ArtccId,
            RecordedAtUtc = Manifest.RecordedAtUtc,
            RecordedBy = Manifest.RecordedBy,
        };
    }

    /// <summary>
    /// Reattach ground layouts to delayed spawn aircraft within a snapshot.
    /// After deserialization, LoadedAircraft.State.GroundLayout is null but
    /// GroundLayoutAirportId is set — look it up in the layout dictionary.
    /// </summary>
    internal static void ReattachDelayedSpawnLayouts(StateSnapshotDto snapshot, Dictionary<string, AirportGroundLayout> layouts)
    {
        if (layouts.Count == 0 || snapshot.Scenario.DelayedQueue is not { Count: > 0 } queue)
        {
            return;
        }

        foreach (var delayed in queue)
        {
            var aircraft = JsonSerializer.Deserialize<LoadedAircraft>(delayed.AircraftJson, RecordingJsonOptions.Default);
            if (aircraft?.State.GroundLayoutAirportId is { } airportId && layouts.TryGetValue(airportId, out var layout))
            {
                aircraft.State.GroundLayout = layout;
            }
        }
    }

    public void Dispose()
    {
        _zip.Dispose();
        _ownedStream?.Dispose();
    }

    private RecordingManifest ReadManifest()
    {
        var entry =
            _zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Recording archive does not contain manifest.json — not a v3 archive.");
        var json = ReadUtf8Entry(entry);
        return JsonSerializer.Deserialize<RecordingManifest>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.json from recording archive.");
    }

    private string ReadBrotliEntry(string entryName)
    {
        var entry = _zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Recording archive missing entry: {entryName}");
        using var entryStream = entry.Open();
        using var brotli = new BrotliStream(entryStream, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        return reader.ReadToEnd();
    }

    private static string ReadUtf8Entry(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        return reader.ReadToEnd();
    }
}
