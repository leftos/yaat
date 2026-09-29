using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services;

/// <summary>
/// Disk-backed ring of push-to-talk speech samples. Used by the opt-in "help improve speech
/// recognition" pipeline: every session whose audio + trace was captured lands here, the Speech
/// Debug window binds <see cref="Entries"/> for review/playback, and users export individual
/// samples as portable .yaat-speech-sample.zip files to attach to GitHub issues. Storage is
/// local-only unless speech telemetry is on: each sample captured while it is gets an empty
/// upload-pending marker, and <see cref="SpeechTelemetryUploader"/> ships the marked samples to
/// the connected server and clears their markers.
///
/// Layout under <c>%LOCALAPPDATA%/yaat/speech-samples/</c>:
/// <code>
///   {yyyyMMdd-HHmmss}-{shortGuid}/
///     audio.wav        — 16 kHz mono 16-bit PCM (matches what Whisper consumes)
///     session.json     — serialized SpeechSession including its full SpeechSessionTrace
///     upload-pending   — empty marker; present while the sample awaits telemetry upload
/// </code>
/// Eviction is FIFO by folder modification time, applied after every <see cref="Add"/> until
/// total bytes ≤ <see cref="UserPreferences.SpeechSampleCacheMaxMb"/>. The capture toggle gates
/// only <see cref="Add"/>; existing on-disk samples remain visible and exportable after the
/// toggle goes off (use <see cref="DeleteAll"/> if the user wants to clear them).
/// </summary>
public sealed class SpeechSampleStore
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechSampleStore>();
    private const string AudioFileName = "audio.wav";
    private const string SessionFileName = "session.json";
    private const string UploadPendingMarkerName = "upload-pending";
    private const int BundleSchemaVersionMulti = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly UserPreferences _preferences;
    private readonly Action<Action> _uiDispatch;

    // Guards _entries and the on-disk sample folders. The speech pipeline adds from a thread-pool
    // thread while the telemetry uploader reads from another, so _entries (newest first) is the
    // source of truth and Entries is only its UI-thread mirror.
    private readonly Lock _ioLock = new();
    private readonly List<SpeechSampleEntry> _entries = [];

    /// <param name="preferences">Supplies the MB cap.</param>
    /// <param name="uiDispatch">Runs an <see cref="Entries"/> update on the thread that owns the binding.</param>
    public SpeechSampleStore(UserPreferences preferences, Action<Action> uiDispatch)
        : this(preferences, YaatPaths.Combine("speech-samples"), uiDispatch) { }

    /// <summary>Test-friendly ctor: override the on-disk root so tests can write into a temp folder.</summary>
    /// <param name="preferences">Supplies the MB cap.</param>
    /// <param name="rootDirectory">Folder holding one sub-folder per sample.</param>
    /// <param name="uiDispatch">Runs an <see cref="Entries"/> update on the thread that owns the binding.</param>
    public SpeechSampleStore(UserPreferences preferences, string rootDirectory, Action<Action> uiDispatch)
    {
        _preferences = preferences;
        _uiDispatch = uiDispatch;
        Entries = [];
        RootDirectory = rootDirectory;
        Rescan();
    }

    /// <summary>Absolute path of the on-disk sample directory.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Loaded sample entries, newest first, for UI binding. Every change reaches it through the
    /// constructor's <c>uiDispatch</c>, in the order the store made it, so it may trail the store by
    /// the dispatcher's queue; the store's own reads never use it.
    /// </summary>
    public ObservableCollection<SpeechSampleEntry> Entries { get; }

    /// <summary>Sum of <see cref="SpeechSampleEntry.TotalBytes"/> for every loaded entry.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_ioLock)
            {
                return _entries.Sum(e => e.TotalBytes);
            }
        }
    }

    /// <summary>Convenience accessor for the configured MB cap.</summary>
    public int MaxBytes => Math.Max(1, _preferences.SpeechSampleCacheMaxMb) * 1024 * 1024;

    /// <summary>
    /// Persists a single push-to-talk session: writes the WAV + session JSON, then FIFO-evicts
    /// older entries until <see cref="TotalBytes"/> ≤ <see cref="MaxBytes"/>. Returns the new
    /// sample's id (folder name) so callers can correlate it with the in-memory
    /// <see cref="SpeechSession.SampleId"/>. When <paramref name="queueForUpload"/> is true the
    /// sample also gets an empty upload-pending marker, which is what lets
    /// <see cref="SpeechTelemetryUploader"/> find it later.
    ///
    /// Caller is responsible for gating on <see cref="UserPreferences.SpeechSampleCaptureEnabled"/>
    /// — the store itself doesn't refuse writes when capture is off so tests can populate fixtures
    /// without flipping the toggle.
    /// </summary>
    public string? Add(SpeechSession session, float[] audioSamples, bool queueForUpload)
    {
        if (audioSamples.Length == 0)
        {
            return null;
        }

        string id = $"{session.TimestampUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        string folder = Path.Combine(RootDirectory, id);
        try
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(folder);

                using (MemoryStream wav = WavHeader.WritePcm16(audioSamples, AudioCaptureService.SampleRate))
                {
                    File.WriteAllBytes(Path.Combine(folder, AudioFileName), wav.ToArray());
                }

                SpeechSession sessionWithId = session with { SampleId = id };
                File.WriteAllText(Path.Combine(folder, SessionFileName), JsonSerializer.Serialize(sessionWithId, JsonOpts));

                if (queueForUpload)
                {
                    File.WriteAllBytes(Path.Combine(folder, UploadPendingMarkerName), []);
                }

                SpeechSampleEntry? entry = LoadEntry(folder);
                if (entry is null)
                {
                    return null;
                }

                _entries.Insert(0, entry);
                _uiDispatch(() => Entries.Insert(0, entry));
                EvictUntilUnderCapLocked();
                return id;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to persist speech sample to {Folder}", folder);
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception cleanupEx)
            {
                Log.LogWarning(cleanupEx, "Failed to clean up partial speech sample at {Folder}", folder);
            }
            return null;
        }
    }

    /// <summary>Removes one persisted sample by id. No-op when the id isn't loaded.</summary>
    public void Delete(string id)
    {
        lock (_ioLock)
        {
            SpeechSampleEntry? entry = FindLocked(id);
            if (entry is null)
            {
                return;
            }

            _entries.Remove(entry);
            _uiDispatch(() => Entries.Remove(entry));
            TryDeleteFolder(entry.Folder);
        }
    }

    /// <summary>Removes every persisted sample. Used by the Settings "Delete all saved samples" action.</summary>
    public void DeleteAll()
    {
        lock (_ioLock)
        {
            foreach (SpeechSampleEntry entry in _entries)
            {
                TryDeleteFolder(entry.Folder);
            }

            _entries.Clear();
            _uiDispatch(() => Entries.Clear());
        }
    }

    /// <summary>
    /// Ids of loaded samples still awaiting telemetry upload (those carrying an upload-pending
    /// marker), oldest first, so an uploader drains the queue in capture order.
    /// </summary>
    public IReadOnlyList<string> PendingUploadIds()
    {
        lock (_ioLock)
        {
            return [.. _entries.Where(e => File.Exists(MarkerPath(e.Folder))).OrderBy(e => e.Session.TimestampUtc).Select(e => e.Id)];
        }
    }

    /// <summary>True while the sample is loaded and still carries its upload-pending marker.</summary>
    public bool IsPendingUpload(string id)
    {
        lock (_ioLock)
        {
            SpeechSampleEntry? entry = FindLocked(id);
            return (entry is not null) && File.Exists(MarkerPath(entry.Folder));
        }
    }

    /// <summary>
    /// Clears one sample's upload-pending marker, marking it as uploaded. No-op when the id isn't
    /// loaded or already has no marker; an IO failure is logged, never thrown, since the next
    /// upload pass would simply retry the sample.
    /// </summary>
    public void MarkUploaded(string id)
    {
        lock (_ioLock)
        {
            SpeechSampleEntry? entry = FindLocked(id);
            if (entry is null)
            {
                return;
            }

            TryDeleteMarker(entry.Folder);
        }
    }

    /// <summary>Clears every loaded sample's upload-pending marker. Used when telemetry is turned off.</summary>
    public void ClearPendingUploads()
    {
        lock (_ioLock)
        {
            foreach (SpeechSampleEntry entry in _entries)
            {
                TryDeleteMarker(entry.Folder);
            }
        }
    }

    /// <summary>
    /// Packages one or more samples as a portable bundle zip. Layout:
    /// <list type="bullet">
    ///   <item><c>manifest.json</c> — yaat version, export timestamp, schema version, and a
    ///   <c>samples</c> array with per-sample metadata (id, capturedUtc, outcome, canonical,
    ///   usedLlmFallback). Lets a reviewer skim the bundle without opening every session.json.</item>
    ///   <item><c>samples/{id}/audio.wav</c> — copy of each on-disk WAV.</item>
    ///   <item><c>samples/{id}/session.json</c> — full <see cref="SpeechSession"/> including its trace.</item>
    /// </list>
    /// Skips any id that isn't currently loaded. Returns the count of samples actually written —
    /// zero means nothing was exported (no matching ids; no file is created).
    /// </summary>
    public int ExportBundle(IReadOnlyCollection<string> ids, string destinationZipPath)
    {
        if (ResolveEntries(ids).Count == 0)
        {
            return 0;
        }

        try
        {
            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            using FileStream fs = File.Create(destinationZipPath);
            return WriteBundle(ids, fs);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to export speech sample bundle ({Count} ids) to {Path}", ids.Count, destinationZipPath);
            return 0;
        }
    }

    /// <summary>
    /// Writes the same bundle layout as <see cref="ExportBundle"/> to an arbitrary stream and
    /// returns the number of samples written (zero when no id is currently loaded). The stream is
    /// left open for the caller; IO failures propagate, so <see cref="ExportBundle"/> can log
    /// them while the telemetry uploader sees them as a failed upload.
    /// </summary>
    public int WriteBundle(IReadOnlyCollection<string> ids, Stream destination)
    {
        List<SpeechSampleEntry> entries = ResolveEntries(ids);
        if (entries.Count == 0)
        {
            return 0;
        }

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var manifest = new SpeechSampleBundleManifest(
            SchemaVersion: BundleSchemaVersionMulti,
            YaatVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            ExportedUtc: DateTime.UtcNow,
            Samples:
            [
                .. entries.Select(e => new SpeechSampleBundleEntry(
                    Id: e.Id,
                    CapturedUtc: e.Session.TimestampUtc,
                    Outcome: e.Session.Outcome.ToString(),
                    UsedLlmFallback: e.Session.UsedLlmFallback,
                    CanonicalCommand: e.Session.CanonicalCommand
                )),
            ]
        );
        WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOpts));

        foreach (SpeechSampleEntry entry in entries)
        {
            WriteEntry(zip, $"samples/{entry.Id}/{AudioFileName}", File.ReadAllBytes(entry.AudioPath));
            WriteEntry(zip, $"samples/{entry.Id}/{SessionFileName}", File.ReadAllBytes(Path.Combine(entry.Folder, SessionFileName)));
        }

        return entries.Count;
    }

    /// <summary>Re-scans <see cref="RootDirectory"/> and rebuilds the loaded entries. Idempotent.</summary>
    public void Rescan()
    {
        lock (_ioLock)
        {
            var loaded = new List<SpeechSampleEntry>();
            if (Directory.Exists(RootDirectory))
            {
                foreach (string folder in Directory.EnumerateDirectories(RootDirectory))
                {
                    SpeechSampleEntry? entry = LoadEntry(folder);
                    if (entry is not null)
                    {
                        loaded.Add(entry);
                    }
                }
            }

            _entries.Clear();
            _entries.AddRange(loaded.OrderByDescending(e => e.Session.TimestampUtc));
            List<SpeechSampleEntry> snapshot = [.. _entries];
            _uiDispatch(() =>
            {
                Entries.Clear();
                foreach (SpeechSampleEntry entry in snapshot)
                {
                    Entries.Add(entry);
                }
            });
        }
    }

    // Caller holds _ioLock. Evicts oldest-first: _entries is newest-first, so walk from the end.
    private void EvictUntilUnderCapLocked()
    {
        int max = MaxBytes;
        long total = _entries.Sum(e => e.TotalBytes);
        for (int i = _entries.Count - 1; (i >= 0) && (total > max); i--)
        {
            SpeechSampleEntry entry = _entries[i];
            _entries.RemoveAt(i);
            total -= entry.TotalBytes;
            _uiDispatch(() => Entries.Remove(entry));
            TryDeleteFolder(entry.Folder);
        }
    }

    // Caller holds _ioLock.
    private SpeechSampleEntry? FindLocked(string id) => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static SpeechSampleEntry? LoadEntry(string folder)
    {
        try
        {
            string sessionPath = Path.Combine(folder, SessionFileName);
            string audioPath = Path.Combine(folder, AudioFileName);
            if (!File.Exists(sessionPath) || !File.Exists(audioPath))
            {
                return null;
            }

            string json = File.ReadAllText(sessionPath);
            SpeechSession? session = JsonSerializer.Deserialize<SpeechSession>(json, JsonOpts);
            if (session is null)
            {
                return null;
            }

            long audioBytes = new FileInfo(audioPath).Length;
            long sessionBytes = new FileInfo(sessionPath).Length;
            string id = Path.GetFileName(folder);
            return new SpeechSampleEntry(id, session.TimestampUtc, folder, audioPath, audioBytes + sessionBytes, session);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Skipping unreadable speech sample folder {Folder}", folder);
            return null;
        }
    }

    private static string MarkerPath(string folder) => Path.Combine(folder, UploadPendingMarkerName);

    private static void TryDeleteMarker(string folder)
    {
        try
        {
            string marker = MarkerPath(folder);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to clear speech upload marker in {Folder}", folder);
        }
    }

    // A snapshot taken under the lock; the caller reads the sample files outside it.
    private List<SpeechSampleEntry> ResolveEntries(IReadOnlyCollection<string> ids)
    {
        lock (_ioLock)
        {
            return [.. ids.Select(FindLocked).OfType<SpeechSampleEntry>()];
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to delete speech sample folder {Folder}", folder);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] payload)
    {
        ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Fastest);
        using Stream s = e.Open();
        s.Write(payload);
    }
}

/// <summary>One persisted sample as surfaced to UI and tests.</summary>
/// <param name="Id">Folder name (matches <see cref="SpeechSession.SampleId"/>).</param>
/// <param name="TimestampUtc">Capture timestamp from the underlying <see cref="SpeechSession"/>.</param>
/// <param name="Folder">Absolute path to the per-sample folder.</param>
/// <param name="AudioPath">Absolute path to <c>audio.wav</c> — used by the debug-window audio player.</param>
/// <param name="TotalBytes">Combined size of audio + session JSON. Drives FIFO eviction.</param>
/// <param name="Session">Deserialized session including its trace.</param>
public sealed record SpeechSampleEntry(string Id, DateTime TimestampUtc, string Folder, string AudioPath, long TotalBytes, SpeechSession Session);

/// <summary>
/// Bundle metadata written to <c>manifest.json</c> at export time. Schema 2 supports one-or-more
/// samples per bundle; single-sample exports are just <c>Samples.Count == 1</c>.
/// </summary>
public sealed record SpeechSampleBundleManifest(
    int SchemaVersion,
    string YaatVersion,
    DateTime ExportedUtc,
    IReadOnlyList<SpeechSampleBundleEntry> Samples
);

/// <summary>One sample's summary inside a bundle manifest. Lets a reviewer skim the bundle without opening every session.json.</summary>
public sealed record SpeechSampleBundleEntry(string Id, DateTime CapturedUtc, string Outcome, bool UsedLlmFallback, string? CanonicalCommand);
