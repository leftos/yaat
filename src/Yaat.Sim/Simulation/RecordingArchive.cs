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

    /// <summary>
    /// Reads the bundled ARTCC configuration JSON, or null when the archive does
    /// not include one (older bundles or recordings made before bundle-config
    /// support).
    /// </summary>
    public string? ReadArtccConfigJson()
    {
        if (!Manifest.HasArtccConfig)
        {
            return null;
        }

        var entry = _zip.GetEntry("artcc-config.json.br");
        if (entry is null)
        {
            return null;
        }

        return ReadBrotliEntry("artcc-config.json.br");
    }

    /// <summary>
    /// Convenience: reads <see cref="ReadArtccConfigJson"/> and deserializes into
    /// an <see cref="Yaat.Sim.Data.Vnas.ArtccConfigRoot"/>. Returns null when the
    /// archive doesn't include a config or deserialization fails.
    /// </summary>
    public Yaat.Sim.Data.Vnas.ArtccConfigRoot? DeserializeArtccConfig()
    {
        var json = ReadArtccConfigJson();
        if (json is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Yaat.Sim.Data.Vnas.ArtccConfigRoot>(json, RecordingJsonOptions.Default);
    }

    public List<RecordedAction> ReadActions()
    {
        var json = ReadBrotliEntry("actions.json.br");
        return JsonSerializer.Deserialize<List<RecordedAction>>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize actions from recording archive.");
    }

    /// <summary>
    /// Reads user-authored timeline bookmarks from the archive's <c>bookmarks.json</c>
    /// entry. Returns an empty list when the entry is absent (older recordings or sessions
    /// where the user added none) — the entry is optional and not tracked in the manifest.
    /// </summary>
    public IReadOnlyList<TimelineBookmark> ReadBookmarks()
    {
        var entry = _zip.GetEntry("bookmarks.json");
        if (entry is null)
        {
            return [];
        }

        var json = ReadUtf8Entry(entry);
        var parsed = JsonSerializer.Deserialize<RecordingBookmarks>(json, RecordingJsonOptions.Default);
        return parsed?.Bookmarks ?? [];
    }

    /// <summary>
    /// Returns a copy of <paramref name="archiveBytes"/> with a <c>bookmarks.json</c> entry
    /// holding <paramref name="bookmarks"/>. The archive is rebuilt entry-by-entry into a
    /// fresh <see cref="ZipArchiveMode.Create"/> stream (Update mode is avoided — it buffers
    /// the whole archive and reorders the Store/Brotli entries this format relies on). Any
    /// pre-existing <c>bookmarks.json</c> is dropped so re-saving overwrites cleanly.
    /// </summary>
    public static byte[] WriteBookmarks(byte[] archiveBytes, IReadOnlyList<TimelineBookmark> bookmarks)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var sourceZip = new ZipArchive(source, ZipArchiveMode.Read);

        using var output = new MemoryStream();
        using (var destZip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var sourceEntry in sourceZip.Entries)
            {
                if (string.Equals(sourceEntry.FullName, "bookmarks.json", StringComparison.Ordinal))
                {
                    continue;
                }

                var destEntry = destZip.CreateEntry(sourceEntry.FullName, CompressionLevel.NoCompression);
                using var sourceStream = sourceEntry.Open();
                using var destStream = destEntry.Open();
                sourceStream.CopyTo(destStream);
            }

            var bookmarksJson = JsonSerializer.SerializeToUtf8Bytes(new RecordingBookmarks(1, bookmarks), RecordingJsonOptions.Default);
            var bookmarksEntry = destZip.CreateEntry("bookmarks.json", CompressionLevel.NoCompression);
            using var bookmarksStream = bookmarksEntry.Open();
            bookmarksStream.Write(bookmarksJson);
        }

        return output.ToArray();
    }

    public List<RecordedAction> ReadActionsForReplay()
    {
        var actions = ReadActions();
        if (actions.Any(static a => a is RecordedAircraftSpawn))
        {
            return actions;
        }

        var scenarioJson = ReadScenarioJson();
        return AddSyntheticAircraftSpawnsFromSnapshots(actions, LoadSnapshotAircraftForSpawnSynthesis(), ReadScenarioAircraftCallsigns(scenarioJson));
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
        var layout =
            JsonSerializer.Deserialize<AirportGroundLayout>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize layout for {airportId}.");
        layout.RebuildAdjacencyLists();
        return layout;
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

    public string? ReadAirportGeoJson(string airportId)
    {
        if (Manifest.AirportGeoJsonIds is not { Count: > 0 } ids)
        {
            return null;
        }

        var declaredId = ids.FirstOrDefault(id => id.Equals(airportId, StringComparison.OrdinalIgnoreCase));
        return declaredId is null ? null : ReadBrotliEntry($"airport-geojson/{declaredId}.geojson.br");
    }

    public Dictionary<string, string> ReadAllAirportGeoJsons()
    {
        var geoJsons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Manifest.AirportGeoJsonIds is not { Count: > 0 })
        {
            return geoJsons;
        }

        foreach (var airportId in Manifest.AirportGeoJsonIds)
        {
            geoJsons[airportId] = ReadBrotliEntry($"airport-geojson/{airportId}.geojson.br");
        }

        return geoJsons;
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
        var actions = ReadActionsForReplay();
        var scenarioJson = ReadScenarioJson();
        return new SessionRecording
        {
            Version = Manifest.Version,
            ScenarioJson = scenarioJson,
            RngSeed = Manifest.RngSeed,
            WeatherJson = ReadWeatherJson(),
            MetarReissuanceEnabled = Manifest.MetarReissuanceEnabled,
            ArtccConfigJson = ReadArtccConfigJson(),
            Actions = actions,
            TotalElapsedSeconds = Manifest.TotalElapsedSeconds,
            Snapshots = null,
            ScenarioName = Manifest.ScenarioName,
            ScenarioId = Manifest.ScenarioId,
            ArtccId = Manifest.ArtccId,
            RecordedAtUtc = Manifest.RecordedAtUtc,
            RecordedBy = Manifest.RecordedBy,
            StudentPositionState = ReadInitialStudentPosition(),
        };
    }

    /// <summary>
    /// Read the resolved student position from the first snapshot's scenario block. The scenario
    /// JSON does not carry it (the server resolves it at load via InitializeTrackPositions), so this
    /// is how Sim-side replay recovers it. Returns null when the recording has no snapshots.
    /// </summary>
    private ReplayStudentPosition? ReadInitialStudentPosition()
    {
        if (Manifest.Snapshots.Count == 0)
        {
            return null;
        }

        var scenario = ReadTimedSnapshot(0).State.Scenario;
        return new ReplayStudentPosition(
            scenario.StudentPosition is not null ? TrackOwner.FromSnapshot(scenario.StudentPosition) : null,
            scenario.StudentTcp is not null ? Tcp.FromSnapshot(scenario.StudentTcp) : null,
            scenario.StudentPositionType,
            scenario.IsStudentTowerPosition
        );
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
        var scenarioJson = ReadScenarioJson();

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
        actions = AddSyntheticAircraftSpawnsFromSnapshots(actions, snapshots, ReadScenarioAircraftCallsigns(scenarioJson));

        return new SessionRecording
        {
            Version = Manifest.Version,
            ScenarioJson = scenarioJson,
            RngSeed = Manifest.RngSeed,
            WeatherJson = ReadWeatherJson(),
            MetarReissuanceEnabled = Manifest.MetarReissuanceEnabled,
            ArtccConfigJson = ReadArtccConfigJson(),
            Actions = actions,
            TotalElapsedSeconds = Manifest.TotalElapsedSeconds,
            Snapshots = snapshots,
            ScenarioName = Manifest.ScenarioName,
            ScenarioId = Manifest.ScenarioId,
            ArtccId = Manifest.ArtccId,
            RecordedAtUtc = Manifest.RecordedAtUtc,
            RecordedBy = Manifest.RecordedBy,
            StudentPositionState = ReadInitialStudentPosition(),
        };
    }

    /// <summary>
    /// Reattach ground layouts to delayed spawn aircraft within a snapshot.
    /// After deserialization, LoadedAircraft.State.Ground.Layout is null but
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
            if (aircraft?.State.Ground.LayoutAirportId is { } airportId && layouts.TryGetValue(airportId, out var layout))
            {
                aircraft.State.Ground.Layout = layout;
            }
        }
    }

    private List<TimedSnapshot> LoadSnapshotAircraftForSpawnSynthesis()
    {
        if (Manifest.Snapshots.Count < 2)
        {
            return [];
        }

        var snapshots = new List<TimedSnapshot>(Manifest.Snapshots.Count);
        for (int i = 0; i < Manifest.Snapshots.Count; i++)
        {
            snapshots.Add(ReadTimedSnapshot(i));
        }

        return snapshots;
    }

    private static List<RecordedAction> AddSyntheticAircraftSpawnsFromSnapshots(
        List<RecordedAction> actions,
        IReadOnlyList<TimedSnapshot>? snapshots,
        IReadOnlySet<string> scenarioAircraftCallsigns
    )
    {
        if (actions.Any(static a => a is RecordedAircraftSpawn) || snapshots is not { Count: >= 2 })
        {
            return actions;
        }

        var result = actions.ToList();
        var seen = snapshots[0].State.Aircraft.Select(static a => a.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            foreach (var aircraft in snapshot.State.Aircraft)
            {
                if (seen.Add(aircraft.Callsign) && !scenarioAircraftCallsigns.Contains(aircraft.Callsign))
                {
                    result.Add(new RecordedAircraftSpawn(snapshot.ElapsedSeconds, aircraft) { IsSynthetic = true });
                }
            }
        }

        return result
            .Select((action, index) => new { Action = action, Index = index })
            .OrderBy(static entry => entry.Action.ElapsedSeconds)
            .ThenBy(static entry => entry.Action is RecordedAircraftSpawn ? 0 : 1)
            .ThenBy(static entry => entry.Index)
            .Select(static entry => entry.Action)
            .ToList();
    }

    private static HashSet<string> ReadScenarioAircraftCallsigns(string scenarioJson)
    {
        var callsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(scenarioJson);
            if (!doc.RootElement.TryGetProperty("aircraft", out var aircraftElement) || aircraftElement.ValueKind is not JsonValueKind.Array)
            {
                return callsigns;
            }

            foreach (var aircraft in aircraftElement.EnumerateArray())
            {
                if (aircraft.TryGetProperty("aircraftId", out var idElement) && idElement.ValueKind is JsonValueKind.String)
                {
                    var callsign = idElement.GetString();
                    if (!string.IsNullOrWhiteSpace(callsign))
                    {
                        callsigns.Add(callsign);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return callsigns;
        }

        return callsigns;
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
