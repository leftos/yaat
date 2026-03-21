using System.IO.Compression;
using System.Text.Json;
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

    /// <summary>
    /// Materialize the full <see cref="SessionRecording"/> by reading all entries.
    /// Use sparingly — this defeats the purpose of on-demand loading.
    /// </summary>
    public SessionRecording ToSessionRecording()
    {
        var actions = ReadActions();

        List<TimedSnapshot>? snapshots = null;
        if (Manifest.Snapshots.Count > 0)
        {
            snapshots = new List<TimedSnapshot>(Manifest.Snapshots.Count);
            for (int i = 0; i < Manifest.Snapshots.Count; i++)
            {
                snapshots.Add(ReadTimedSnapshot(i));
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
