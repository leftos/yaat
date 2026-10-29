using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Reader for v4 recording archives. Reads the manifest eagerly on construction;
/// scenario, actions, weather, and individual snapshots are loaded on demand.
/// Holds the underlying <see cref="ZipArchive"/> open until disposed.
/// </summary>
public sealed class RecordingArchive : IDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger("RecordingArchive");

    /// <summary>The most characters of a manifest's grid hash a refusal message shows.</summary>
    private const int RefusalHashChars = 16;

    private readonly ZipArchive _zip;
    private readonly Stream? _ownedStream;
    private TimedSnapshot? _firstSnapshot;

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

    public string ReadScenarioJson() => ReadBrotliEntry("scenario.json.br");

    public string? ReadWeatherJson()
    {
        if (!Manifest.HasWeather)
        {
            return null;
        }

        ZipArchiveEntry? entry = _zip.GetEntry("weather.json");
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

        ZipArchiveEntry? entry = _zip.GetEntry("artcc-config.json.br");
        if (entry is null)
        {
            return null;
        }

        return ReadBrotliEntry("artcc-config.json.br");
    }

    /// <summary>
    /// Reads the bundled towered airport ids, or null when the archive does not include them (recordings made before the
    /// entry existed).
    /// </summary>
    public List<string>? ReadToweredAirportIds() =>
        (Manifest.HasToweredAirports && (_zip.GetEntry("towered-airports.json.br") is not null))
            ? DeserializeBrotliEntry<List<string>>("towered-airports.json.br")
            : null;

    /// <summary>
    /// Convenience: reads <see cref="ReadArtccConfigJson"/> and deserializes into
    /// an <see cref="Yaat.Sim.Data.Vnas.ArtccConfigRoot"/>. Returns null when the
    /// archive doesn't include a config or deserialization fails.
    /// </summary>
    public Yaat.Sim.Data.Vnas.ArtccConfigRoot? DeserializeArtccConfig()
    {
        string? json = ReadArtccConfigJson();
        if (json is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Yaat.Sim.Data.Vnas.ArtccConfigRoot>(json, RecordingJsonOptions.Default);
    }

    public List<RecordedAction> ReadActions()
    {
        return DeserializeBrotliEntry<List<RecordedAction>>("actions.json.br")
            ?? throw new InvalidOperationException("Failed to deserialize actions from recording archive.");
    }

    /// <summary>
    /// Reads the room's broadcast terminal log (<c>terminal-log.json.br</c>) — commands, responses,
    /// SAY, warnings, and chat with per-line elapsed times. Returns an empty list for archives written
    /// before the terminal-log feature (manifest flag false or entry absent).
    /// </summary>
    public List<RecordedTerminalEntry> ReadTerminalLog()
    {
        if (!Manifest.HasTerminalLog)
        {
            return [];
        }

        return DeserializeBrotliEntry<List<RecordedTerminalEntry>>("terminal-log.json.br")
            ?? throw new InvalidOperationException("Failed to deserialize terminal log from recording archive.");
    }

    /// <summary>
    /// Reads user-authored timeline bookmarks from the archive's <c>bookmarks.json</c>
    /// entry. Returns an empty list when the entry is absent (older recordings or sessions
    /// where the user added none) — the entry is optional and not tracked in the manifest.
    /// </summary>
    public IReadOnlyList<TimelineBookmark> ReadBookmarks()
    {
        ZipArchiveEntry? entry = _zip.GetEntry("bookmarks.json");
        if (entry is null)
        {
            return [];
        }

        string json = ReadUtf8Entry(entry);
        RecordingBookmarks? parsed = JsonSerializer.Deserialize<RecordingBookmarks>(json, RecordingJsonOptions.Default);
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
            foreach (ZipArchiveEntry sourceEntry in sourceZip.Entries)
            {
                if (string.Equals(sourceEntry.FullName, "bookmarks.json", StringComparison.Ordinal))
                {
                    continue;
                }

                ZipArchiveEntry destEntry = destZip.CreateEntry(sourceEntry.FullName, CompressionLevel.NoCompression);
                using Stream sourceStream = sourceEntry.Open();
                using Stream destStream = destEntry.Open();
                sourceStream.CopyTo(destStream);
            }

            byte[] bookmarksJson = JsonSerializer.SerializeToUtf8Bytes(new RecordingBookmarks(1, bookmarks), RecordingJsonOptions.Default);
            ZipArchiveEntry bookmarksEntry = destZip.CreateEntry("bookmarks.json", CompressionLevel.NoCompression);
            using Stream bookmarksStream = bookmarksEntry.Open();
            bookmarksStream.Write(bookmarksJson);
        }

        return output.ToArray();
    }

    public List<RecordedAction> ReadActionsForReplay()
    {
        List<RecordedAction> actions = ReadActions();
        if (actions.Any(static a => a is RecordedAircraftSpawn))
        {
            return actions;
        }

        string scenarioJson = ReadScenarioJson();
        return AddSyntheticAircraftSpawnsFromSnapshots(actions, ReadScenarioAircraftCallsigns(scenarioJson));
    }

    /// <summary>
    /// Load a single snapshot by its ordinal index (0-based, matching the manifest's Snapshots list).
    /// </summary>
    public StateSnapshotDto ReadSnapshot(int index)
    {
        return DeserializeBrotliEntry<StateSnapshotDto>($"snapshots/{index:D3}.json.br")
            ?? throw new InvalidOperationException($"Failed to deserialize snapshot {index} from recording archive.");
    }

    /// <summary>
    /// Load a single snapshot and combine it with its manifest index entry
    /// to produce a <see cref="TimedSnapshot"/>.
    /// </summary>
    public TimedSnapshot ReadTimedSnapshot(int index)
    {
        SnapshotIndexEntry indexEntry = Manifest.Snapshots[index];
        StateSnapshotDto state = ReadSnapshot(index);
        return new TimedSnapshot
        {
            ElapsedSeconds = indexEntry.ElapsedSeconds,
            ActionIndex = indexEntry.ActionIndex,
            State = state,
        };
    }

    // --- Layout reading ---

    /// <summary>
    /// The format bundled layouts are written in, recorded in the manifest. Format 1 carries the runway coordinates and edge
    /// shapes (tuples, written as fields) and the runway-end dictionaries; a layout without it lacks them. A format-1
    /// layout written before parking nodes carried <see cref="Data.Airport.GroundNode.StandDeparture"/> lacks that field,
    /// and its stands read as <see cref="Data.Airport.StandDeparture.PushBack"/>. One written before runways carried
    /// <see cref="Data.Airport.GroundRunway.HoldShortStandoffFt"/> lacks that field, and its runways read their hold line
    /// from the authored <c>holdShortDistance</c>, else the width table.
    /// </summary>
    public const int CurrentLayoutFormatVersion = 1;

    /// <summary>The entry a ground layout is stored under, by the airport ID it was written with.</summary>
    public static string LayoutEntryName(string airportId) => $"layouts/{airportId}.json.br";

    /// <summary>The entry an airport's source GeoJSON is stored under, by the airport ID it was written with.</summary>
    public static string AirportGeoJsonEntryName(string airportId) => $"airport-geojson/{airportId}.geojson.br";

    /// <summary>The entry a GFS wind grid's raw GRIB2 bytes are stored under (Brotli), by the grid's hash.</summary>
    public static string WindGridEntryName(string hash) => $"wind-grids/{hash}.grib2.br";

    /// <summary>
    /// Registers and pins every GFS wind grid the manifest lists in <see cref="WindGridRegistry"/>, so the weather text the
    /// archive replays resolves its grid however many grids it carries and whatever registers elsewhere meanwhile. An entry
    /// that is missing, unreadable, whose bytes do not hash to its name, or that does not decode is skipped with a warning,
    /// and the weather naming it flies the FD wind layers. Every loader calls this before the weather text is parsed. A
    /// recording past a grid limit (<see cref="WindGridLimitRefusal"/>) is refused as a whole, and nothing stays pinned.
    /// </summary>
    /// <returns>The pins on the grids registered; the caller owns them and disposes them when it no longer replays the archive.</returns>
    /// <exception cref="WindGridLimitException">
    /// The manifest lists more than <see cref="WindGridRegistry.MaxRoomGrids"/> distinct grids, or a grid entry decompresses
    /// past <see cref="WindGridRegistry.MaxGridBytes"/>.
    /// </exception>
    public WindGridPins RegisterWindGrids()
    {
        List<string> hashes = DistinctWindGridHashes();
        if (hashes.Count == 0)
        {
            return new WindGridPins([], []);
        }

        if (hashes.Count > WindGridRegistry.MaxRoomGrids)
        {
            throw new WindGridLimitException(CountRefusal(hashes.Count));
        }

        return RegisterWindGridEntries(
            hashes,
            ReadWindGrid,
            (hash, ex) =>
                Log.LogWarning(ex, "Recorded GFS wind grid {Hash} cannot be read or decoded; skipped, its weather flies the FD wind layers", hash)
        );
    }

    /// <summary>
    /// Registers and pins each of <paramref name="hashes"/> whose bytes <paramref name="readGrid"/> returns, the loop a
    /// recording load and a checkpoint restore share. A null from <paramref name="readGrid"/> skips the hash; a grid that
    /// cannot be read or decoded is skipped and handed to <paramref name="onUnreadable"/>; any other exception, a limit
    /// refusal among them, releases every pin taken so far and propagates.
    /// </summary>
    /// <returns>The pins on the grids registered; the caller owns them.</returns>
    public static WindGridPins RegisterWindGridEntries(
        IReadOnlyList<string> hashes,
        Func<string, byte[]?> readGrid,
        Action<string, Exception> onUnreadable
    )
    {
        var registered = new List<string>(hashes.Count);
        var pins = new List<IDisposable>(hashes.Count);
        try
        {
            foreach (string hash in hashes)
            {
                try
                {
                    if (readGrid(hash) is { } grib2)
                    {
                        pins.Add(WindGridRegistry.RegisterAndPin(grib2, out _));
                        registered.Add(hash);
                    }
                }
                catch (Exception ex)
                    when (ex
                            is (Grib2FormatException or InvalidOperationException or InvalidDataException or IOException)
                                and not ObjectDisposedException
                    )
                {
                    onUnreadable(hash, ex);
                }
            }
        }
        catch
        {
            foreach (IDisposable pin in pins)
            {
                pin.Dispose();
            }

            throw;
        }

        return new WindGridPins(registered, pins);
    }

    /// <summary>
    /// Reads the wind-grid entry <paramref name="hash"/> from <paramref name="zip"/>, as <see cref="RecordingArchiveWriter.WriteWindGridEntry"/>
    /// stored it: its bytes when the entry is there, within <see cref="WindGridRegistry.MaxGridBytes"/> once decompressed and
    /// hashing to its name, else why not. An entry that is not Brotli throws, for the register loop to skip.
    /// </summary>
    public static WindGridEntryRead ReadWindGridEntry(ZipArchive zip, string hash)
    {
        string entryName = WindGridEntryName(hash);
        if (zip.GetEntry(entryName) is not { } entry)
        {
            return new WindGridEntryRead(null, WindGridEntryProblem.Missing, entryName, null);
        }

        if (ReadEntryWithinLimit(entry) is not { } grib2)
        {
            return new WindGridEntryRead(null, WindGridEntryProblem.TooLarge, entryName, null);
        }

        string actual = WindGridRegistry.HashOf(grib2);
        return string.Equals(actual, hash, StringComparison.Ordinal)
            ? new WindGridEntryRead(grib2, WindGridEntryProblem.None, entryName, null)
            : new WindGridEntryRead(null, WindGridEntryProblem.WrongHash, entryName, actual);
    }

    /// <summary>The bytes of the grid entry <paramref name="hash"/>, or null (with a warning) when it is missing or holds other bytes.</summary>
    /// <exception cref="WindGridLimitException">The entry decompresses past <see cref="WindGridRegistry.MaxGridBytes"/>.</exception>
    private byte[]? ReadWindGrid(string hash)
    {
        WindGridEntryRead read = ReadWindGridEntry(_zip, hash);
        switch (read.Problem)
        {
            case WindGridEntryProblem.Missing:
                Log.LogWarning(
                    "Recording lists GFS wind grid {Hash} but has no {Entry} entry; skipped, its weather flies the FD wind layers",
                    hash,
                    read.EntryName
                );
                return null;
            case WindGridEntryProblem.TooLarge:
                throw new WindGridLimitException(SizeRefusal(hash));
            case WindGridEntryProblem.WrongHash:
                Log.LogWarning(
                    "Recorded GFS wind grid entry {Entry} holds bytes hashing to {Actual}, not {Hash}; skipped, its weather flies the FD wind layers",
                    read.EntryName,
                    read.ActualHash,
                    hash
                );
                return null;
            default:
                return read.Grib2;
        }
    }

    /// <summary>
    /// Why the recording is refused as a whole for its GFS wind grids, or null when it is not: it lists more than
    /// <see cref="WindGridRegistry.MaxRoomGrids"/> distinct grids (checked before any entry is opened), or one of its grid
    /// entries decompresses past <see cref="WindGridRegistry.MaxGridBytes"/>. Registers nothing, so a loader runs it before it
    /// changes anything; an entry that is missing or damaged is no refusal and is left for <see cref="RegisterWindGrids"/> to
    /// skip with a warning.
    /// </summary>
    public string? WindGridLimitRefusal()
    {
        List<string> hashes = DistinctWindGridHashes();
        if (hashes.Count > WindGridRegistry.MaxRoomGrids)
        {
            return CountRefusal(hashes.Count);
        }

        foreach (string hash in hashes)
        {
            if ((_zip.GetEntry(WindGridEntryName(hash)) is { } entry) && IsPastByteLimit(entry, hash))
            {
                return SizeRefusal(hash);
            }
        }

        return null;
    }

    /// <summary>The manifest's grid hashes with repeats removed, in order of first listing.</summary>
    private List<string> DistinctWindGridHashes() => [.. (Manifest.WindGridHashes ?? []).Distinct(StringComparer.Ordinal)];

    private static string CountRefusal(int count) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Recording refused: it carries {count} wind grids; the limit is {WindGridRegistry.MaxRoomGrids}."
        );

    /// <summary>
    /// The size refusal of grid <paramref name="hash"/>, a manifest-supplied string of any length, so it shows at most its
    /// first <see cref="RefusalHashChars"/> characters.
    /// </summary>
    private static string SizeRefusal(string hash)
    {
        string shown = hash.Length > RefusalHashChars ? string.Concat(hash.AsSpan(0, RefusalHashChars), "…") : hash;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Recording refused: wind grid {shown} is larger than {WindGridRegistry.MaxGridBytes:N0} bytes."
        );
    }

    /// <summary>The entry's GRIB2 bytes, or null when they pass <see cref="WindGridRegistry.MaxGridBytes"/>.</summary>
    private static byte[]? ReadEntryWithinLimit(ZipArchiveEntry entry)
    {
        using Stream entryStream = entry.Open();
        return WindGridRegistry.ReadBrotliWithinLimit(entryStream);
    }

    /// <summary>True when the grid entry decompresses past the byte limit; a damaged entry is not, and is left for the load to skip.</summary>
    private static bool IsPastByteLimit(ZipArchiveEntry entry, string hash)
    {
        try
        {
            return ReadEntryWithinLimit(entry) is null;
        }
        catch (Exception ex) when (ex is (InvalidOperationException or InvalidDataException or IOException) and not ObjectDisposedException)
        {
            Log.LogDebug(ex, "Recorded GFS wind grid {Hash} cannot be read for its size check; the load skips it with a warning", hash);
            return false;
        }
    }

    /// <summary>
    /// Read a ground layout stored in the archive by airport ID.
    /// </summary>
    public AirportGroundLayout ReadLayout(string airportId) => ReadLayoutEntry(_zip, airportId);

    /// <summary>
    /// Reads the ground layout <see cref="RecordingArchiveWriter.WriteLayoutEntry"/> stored in <paramref name="zip"/> under
    /// <paramref name="airportId"/> (the ID it was written with), rebuilding what serialization leaves out.
    /// </summary>
    public static AirportGroundLayout ReadLayoutEntry(ZipArchive zip, string airportId)
    {
        using Stream entryStream = OpenEntry(zip, LayoutEntryName(airportId));
        using var brotli = new BrotliStream(entryStream, CompressionMode.Decompress);
        return GroundLayoutSerializer.Deserialize(brotli, airportId);
    }

    /// <summary>
    /// Reads the source GeoJSON <see cref="RecordingArchiveWriter.WriteAirportGeoJsonEntry"/> stored in
    /// <paramref name="zip"/> under <paramref name="airportId"/> (the ID it was written with).
    /// </summary>
    public static string ReadAirportGeoJsonEntry(ZipArchive zip, string airportId) => ReadBrotliEntry(zip, AirportGeoJsonEntryName(airportId));

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

        foreach (string airportId in Manifest.LayoutAirportIds)
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

        string? declaredId = ids.FirstOrDefault(id => id.Equals(airportId, StringComparison.OrdinalIgnoreCase));
        return declaredId is null ? null : ReadAirportGeoJsonEntry(_zip, declaredId);
    }

    public Dictionary<string, string> ReadAllAirportGeoJsons()
    {
        var geoJsons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Manifest.AirportGeoJsonIds is not { Count: > 0 })
        {
            return geoJsons;
        }

        foreach (string airportId in Manifest.AirportGeoJsonIds)
        {
            geoJsons[airportId] = ReadAirportGeoJsonEntry(_zip, airportId);
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
        List<SnapshotIndexEntry> snapshots = Manifest.Snapshots;
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
        int? index = FindNearestSnapshotIndex(targetSeconds);
        return index.HasValue ? ReadTimedSnapshot(index.Value) : null;
    }

    // --- SessionRecording materialization ---

    /// <summary>
    /// Load the recording without any snapshots: actions, scenario, weather, and metadata only.
    /// Use this for replay and tests that don't need snapshot data.
    /// </summary>
    /// <exception cref="WindGridLimitException">The recording is past a wind-grid limit (<see cref="RegisterWindGrids"/>).</exception>
    public SessionRecording ToBaseSessionRecording()
    {
        WindGridPins windGridPins = RegisterWindGrids();
        try
        {
            List<RecordedAction> actions = ReadActionsForReplay();
            string scenarioJson = ReadScenarioJson();
            return new SessionRecording
            {
                Version = Manifest.Version,
                ScenarioJson = scenarioJson,
                RngSeed = Manifest.RngSeed,
                WeatherJson = ReadWeatherJson(),
                MetarReissuanceEnabled = Manifest.MetarReissuanceEnabled,
                ArtccConfigJson = ReadArtccConfigJson(),
                ToweredAirportIds = ReadToweredAirportIds(),
                Actions = actions,
                TerminalLog = ReadTerminalLog(),
                TotalElapsedSeconds = Manifest.TotalElapsedSeconds,
                Snapshots = null,
                ScenarioName = Manifest.ScenarioName,
                ScenarioId = Manifest.ScenarioId,
                ArtccId = Manifest.ArtccId,
                RecordedAtUtc = Manifest.RecordedAtUtc,
                RecordedBy = Manifest.RecordedBy,
                SessionStartUtc = Manifest.ResolveSessionStartUtc(),
                InitialActiveRunways = Manifest.InitialActiveRunways,
                StudentPositionState = ReadInitialStudentPosition(),
                InitialAtcPositions = ReadInitialAtcPositions(),
                FinalApproachSpeedVarietyEnabled = ReadInitialFinalApproachSpeedVariety(),
                WindGridPins = windGridPins,
            };
        }
        catch
        {
            windGridPins.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read the resolved ATC roster from the first snapshot's scenario block, mirroring
    /// <see cref="ReadInitialStudentPosition"/> — the scenario JSON's <c>atc</c> entries are unresolved, so this is
    /// how a Sim-side replay recovers the roster the deferred autotrack pass matches departures against. Returns null
    /// when the recording has no snapshots or the snapshot predates the roster.
    /// </summary>
    private IReadOnlyList<AtcPositionDto>? ReadInitialAtcPositions() =>
        Manifest.Snapshots.Count == 0 ? null : FirstSnapshot.State.Scenario.AtcPositions;

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

        ScenarioSnapshotDto scenario = FirstSnapshot.State.Scenario;
        return new ReplayStudentPosition(
            scenario.StudentPosition is not null ? TrackOwner.FromSnapshot(scenario.StudentPosition) : null,
            scenario.StudentTcp is not null ? Tcp.FromSnapshot(scenario.StudentTcp) : null,
            scenario.StudentPositionType,
            scenario.IsStudentTowerPosition
        );
    }

    /// <summary>
    /// Read whether FAS-reduction variety was enabled from the first snapshot's scenario block,
    /// mirroring <see cref="ReadInitialStudentPosition"/>. Replay uses it to reproduce the same
    /// varied slow-down distances. Returns false when the recording has no snapshots (legacy v1).
    /// </summary>
    private bool ReadInitialFinalApproachSpeedVariety()
    {
        if (Manifest.Snapshots.Count == 0)
        {
            return false;
        }

        return FirstSnapshot.State.Scenario.FinalApproachSpeedVarietyEnabled;
    }

    /// <summary>
    /// Snapshot 0, decoded once per archive. The student position, the FAS-variety flag, and spawn
    /// synthesis all read it; each decode is a full Brotli + JSON pass over every aircraft.
    /// </summary>
    private TimedSnapshot FirstSnapshot => _firstSnapshot ??= ReadTimedSnapshot(0);

    /// <summary>
    /// Materialize the full <see cref="SessionRecording"/> by reading all entries.
    /// Reattaches ground layouts from archive entries to delayed spawn aircraft.
    /// Use sparingly — this defeats the purpose of on-demand loading.
    /// </summary>
    /// <exception cref="WindGridLimitException">The recording is past a wind-grid limit (<see cref="RegisterWindGrids"/>).</exception>
    public SessionRecording ToSessionRecording()
    {
        WindGridPins windGridPins = RegisterWindGrids();
        try
        {
            Dictionary<string, AirportGroundLayout> layouts = ReadAllLayouts();
            List<RecordedAction> actions = ReadActions();
            string scenarioJson = ReadScenarioJson();

            List<TimedSnapshot>? snapshots = null;
            if (Manifest.Snapshots.Count > 0)
            {
                snapshots = new List<TimedSnapshot>(Manifest.Snapshots.Count);
                for (int i = 0; i < Manifest.Snapshots.Count; i++)
                {
                    TimedSnapshot timed = ReadTimedSnapshot(i);
                    ReattachDelayedSpawnLayouts(timed.State, layouts);
                    snapshots.Add(timed);
                }
            }
            actions = AddSyntheticAircraftSpawnsFromSnapshots(actions, ReadScenarioAircraftCallsigns(scenarioJson));

            return new SessionRecording
            {
                Version = Manifest.Version,
                ScenarioJson = scenarioJson,
                RngSeed = Manifest.RngSeed,
                WeatherJson = ReadWeatherJson(),
                MetarReissuanceEnabled = Manifest.MetarReissuanceEnabled,
                ArtccConfigJson = ReadArtccConfigJson(),
                ToweredAirportIds = ReadToweredAirportIds(),
                Actions = actions,
                TerminalLog = ReadTerminalLog(),
                TotalElapsedSeconds = Manifest.TotalElapsedSeconds,
                Snapshots = snapshots,
                ScenarioName = Manifest.ScenarioName,
                ScenarioId = Manifest.ScenarioId,
                ArtccId = Manifest.ArtccId,
                RecordedAtUtc = Manifest.RecordedAtUtc,
                RecordedBy = Manifest.RecordedBy,
                SessionStartUtc = Manifest.ResolveSessionStartUtc(),
                InitialActiveRunways = Manifest.InitialActiveRunways,
                StudentPositionState = ReadInitialStudentPosition(),
                InitialAtcPositions = ReadInitialAtcPositions(),
                FinalApproachSpeedVarietyEnabled = ReadInitialFinalApproachSpeedVariety(),
                WindGridPins = windGridPins,
            };
        }
        catch
        {
            windGridPins.Dispose();
            throw;
        }
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

        foreach (DelayedSpawnDto delayed in queue)
        {
            LoadedAircraft? aircraft = JsonSerializer.Deserialize<LoadedAircraft>(delayed.AircraftJson, RecordingJsonOptions.Default);
            if (aircraft?.State.Ground.LayoutAirportId is { } airportId && layouts.TryGetValue(airportId, out AirportGroundLayout? layout))
            {
                aircraft.State.Ground.Layout = layout;
            }
        }
    }

    /// <summary>
    /// Recordings made before aircraft spawns were logged as actions have no way to replay
    /// generator/late-spawned traffic, so synthesize a spawn at the first snapshot in which each
    /// unknown callsign appears. Only those aircraft are deserialized: every other snapshot entry
    /// is skipped at the JSON-element level (a full <see cref="StateSnapshotDto"/> decode per
    /// snapshot dominated recording load time and allocation).
    /// </summary>
    private List<RecordedAction> AddSyntheticAircraftSpawnsFromSnapshots(List<RecordedAction> actions, IReadOnlySet<string> scenarioAircraftCallsigns)
    {
        if (actions.Any(static a => a is RecordedAircraftSpawn) || Manifest.Snapshots.Count < 2)
        {
            return actions;
        }

        var result = actions.ToList();
        var seen = FirstSnapshot.State.Aircraft.Select(static a => a.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < Manifest.Snapshots.Count; i++)
        {
            double elapsedSeconds = Manifest.Snapshots[i].ElapsedSeconds;
            foreach (AircraftSnapshotDto aircraft in ReadUnseenSnapshotAircraft(i, seen))
            {
                if (!scenarioAircraftCallsigns.Contains(aircraft.Callsign))
                {
                    result.Add(new RecordedAircraftSpawn(elapsedSeconds, aircraft) { IsSynthetic = true });
                }
            }
        }

        return
        [
            .. result
                .Select((action, index) => new { Action = action, Index = index })
                .OrderBy(static entry => entry.Action.ElapsedSeconds)
                .ThenBy(static entry => entry.Action is RecordedAircraftSpawn ? 0 : 1)
                .ThenBy(static entry => entry.Index)
                .Select(static entry => entry.Action),
        ];
    }

    private static HashSet<string> ReadScenarioAircraftCallsigns(string scenarioJson)
    {
        var callsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(scenarioJson);
            if (!doc.RootElement.TryGetProperty("aircraft", out JsonElement aircraftElement) || aircraftElement.ValueKind is not JsonValueKind.Array)
            {
                return callsigns;
            }

            foreach (JsonElement aircraft in aircraftElement.EnumerateArray())
            {
                if (aircraft.TryGetProperty("aircraftId", out JsonElement idElement) && idElement.ValueKind is JsonValueKind.String)
                {
                    string? callsign = idElement.GetString();
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
        ZipArchiveEntry entry =
            _zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Recording archive does not contain manifest.json — not a valid recording archive.");
        string json = ReadUtf8Entry(entry);
        return JsonSerializer.Deserialize<RecordingManifest>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.json from recording archive.");
    }

    /// <summary>
    /// Deserialize the aircraft of snapshot <paramref name="index"/> whose callsigns are not yet in
    /// <paramref name="seen"/>, adding them to it. Every other aircraft element is skipped without
    /// being materialized.
    /// </summary>
    private List<AircraftSnapshotDto> ReadUnseenSnapshotAircraft(int index, HashSet<string> seen)
    {
        string entryName = $"snapshots/{index:D3}.json.br";
        ZipArchiveEntry entry = _zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Recording archive missing entry: {entryName}");
        using Stream entryStream = entry.Open();
        using var brotli = new BrotliStream(entryStream, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(brotli);

        var unseen = new List<AircraftSnapshotDto>();
        if (
            !TryGetPropertyIgnoreCase(document.RootElement, nameof(StateSnapshotDto.Aircraft), out JsonElement aircraftArray)
            || aircraftArray.ValueKind is not JsonValueKind.Array
        )
        {
            return unseen;
        }

        foreach (JsonElement element in aircraftArray.EnumerateArray())
        {
            if (
                !TryGetPropertyIgnoreCase(element, nameof(AircraftSnapshotDto.Callsign), out JsonElement callsignElement)
                || callsignElement.GetString() is not { } callsign
            )
            {
                continue;
            }

            if (seen.Add(callsign))
            {
                AircraftSnapshotDto aircraft =
                    element.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default)
                    ?? throw new InvalidOperationException($"Failed to deserialize aircraft {callsign} from snapshot {index}.");
                unseen.Add(aircraft);
            }
        }

        return unseen;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(name) || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private T? DeserializeBrotliEntry<T>(string entryName) => DeserializeBrotliEntry<T>(_zip, entryName, RecordingJsonOptions.Default);

    private string ReadBrotliEntry(string entryName) => ReadBrotliEntry(_zip, entryName);

    private static T? DeserializeBrotliEntry<T>(ZipArchive zip, string entryName, JsonSerializerOptions options)
    {
        using Stream entryStream = OpenEntry(zip, entryName);
        using var brotli = new BrotliStream(entryStream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(brotli, options);
    }

    private static string ReadBrotliEntry(ZipArchive zip, string entryName)
    {
        using Stream entryStream = OpenEntry(zip, entryName);
        using var brotli = new BrotliStream(entryStream, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        return reader.ReadToEnd();
    }

    private static Stream OpenEntry(ZipArchive zip, string entryName) =>
        (zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive missing entry: {entryName}")).Open();

    private static string ReadUtf8Entry(ZipArchiveEntry entry)
    {
        using Stream entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        return reader.ReadToEnd();
    }
}
