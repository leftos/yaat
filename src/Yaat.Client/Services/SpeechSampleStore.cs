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
/// samples as portable .yaat-speech-sample.zip files to attach to GitHub issues. Nothing is
/// uploaded automatically — this is local-only storage with a user-configurable MB cap.
///
/// Layout under <c>%LOCALAPPDATA%/yaat/speech-samples/</c>:
/// <code>
///   {yyyyMMdd-HHmmss}-{shortGuid}/
///     audio.wav      — 16 kHz mono 16-bit PCM (matches what Whisper consumes)
///     session.json   — serialized SpeechSession including its full SpeechSessionTrace
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
    private const int BundleSchemaVersionMulti = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly UserPreferences _preferences;
    private readonly object _ioLock = new();

    public SpeechSampleStore(UserPreferences preferences)
        : this(preferences, YaatPaths.Combine("speech-samples")) { }

    /// <summary>Test-friendly ctor: override the on-disk root so tests can write into a temp folder.</summary>
    public SpeechSampleStore(UserPreferences preferences, string rootDirectory)
    {
        _preferences = preferences;
        Entries = [];
        RootDirectory = rootDirectory;
        Rescan();
    }

    /// <summary>Absolute path of the on-disk sample directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Loaded sample entries, newest first. Mutated only on the UI thread once bound.</summary>
    public ObservableCollection<SpeechSampleEntry> Entries { get; }

    /// <summary>Sum of <see cref="SpeechSampleEntry.TotalBytes"/> for every loaded entry.</summary>
    public long TotalBytes => Entries.Sum(e => e.TotalBytes);

    /// <summary>Convenience accessor for the configured MB cap.</summary>
    public int MaxBytes => Math.Max(1, _preferences.SpeechSampleCacheMaxMb) * 1024 * 1024;

    /// <summary>
    /// Persists a single push-to-talk session: writes the WAV + session JSON, then FIFO-evicts
    /// older entries until <see cref="TotalBytes"/> ≤ <see cref="MaxBytes"/>. Returns the new
    /// sample's id (folder name) so callers can correlate it with the in-memory
    /// <see cref="SpeechSession.SampleId"/>.
    ///
    /// Caller is responsible for gating on <see cref="UserPreferences.SpeechSampleCaptureEnabled"/>
    /// — the store itself doesn't refuse writes when capture is off so tests can populate fixtures
    /// without flipping the toggle.
    /// </summary>
    public string? Add(SpeechSession session, float[] audioSamples)
    {
        if (audioSamples.Length == 0)
        {
            return null;
        }

        var id = $"{session.TimestampUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var folder = Path.Combine(RootDirectory, id);
        try
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(folder);

                using (var wav = WavHeader.WritePcm16(audioSamples, AudioCaptureService.SampleRate))
                {
                    File.WriteAllBytes(Path.Combine(folder, AudioFileName), wav.ToArray());
                }

                var sessionWithId = session with { SampleId = id };
                File.WriteAllText(Path.Combine(folder, SessionFileName), JsonSerializer.Serialize(sessionWithId, JsonOpts));
            }

            var entry = LoadEntry(folder);
            if (entry is null)
            {
                return null;
            }

            Entries.Insert(0, entry);
            EvictUntilUnderCap();
            return id;
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
        var entry = Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (entry is null)
        {
            return;
        }

        Entries.Remove(entry);
        TryDeleteFolder(entry.Folder);
    }

    /// <summary>Removes every persisted sample. Used by the Settings "Delete all saved samples" action.</summary>
    public void DeleteAll()
    {
        foreach (var entry in Entries.ToList())
        {
            TryDeleteFolder(entry.Folder);
        }
        Entries.Clear();
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
        var entries = ids.Select(id => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
            .Where(e => e is not null)
            .Cast<SpeechSampleEntry>()
            .ToList();
        if (entries.Count == 0)
        {
            return 0;
        }

        try
        {
            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            using var fs = File.Create(destinationZipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            var manifest = new SpeechSampleBundleManifest(
                SchemaVersion: BundleSchemaVersionMulti,
                YaatVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                ExportedUtc: DateTime.UtcNow,
                Samples: entries
                    .Select(e => new SpeechSampleBundleEntry(
                        Id: e.Id,
                        CapturedUtc: e.Session.TimestampUtc,
                        Outcome: e.Session.Outcome.ToString(),
                        UsedLlmFallback: e.Session.UsedLlmFallback,
                        CanonicalCommand: e.Session.CanonicalCommand
                    ))
                    .ToList()
            );
            WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOpts));

            foreach (var entry in entries)
            {
                WriteEntry(zip, $"samples/{entry.Id}/{AudioFileName}", File.ReadAllBytes(entry.AudioPath));
                WriteEntry(zip, $"samples/{entry.Id}/{SessionFileName}", File.ReadAllBytes(Path.Combine(entry.Folder, SessionFileName)));
            }

            return entries.Count;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to export speech sample bundle ({Count} ids) to {Path}", entries.Count, destinationZipPath);
            return 0;
        }
    }

    /// <summary>Re-scans <see cref="RootDirectory"/> and rebuilds <see cref="Entries"/>. Idempotent.</summary>
    public void Rescan()
    {
        Entries.Clear();
        if (!Directory.Exists(RootDirectory))
        {
            return;
        }

        var loaded = new List<SpeechSampleEntry>();
        foreach (var folder in Directory.EnumerateDirectories(RootDirectory))
        {
            var entry = LoadEntry(folder);
            if (entry is not null)
            {
                loaded.Add(entry);
            }
        }

        foreach (var entry in loaded.OrderByDescending(e => e.Session.TimestampUtc))
        {
            Entries.Add(entry);
        }
    }

    private void EvictUntilUnderCap()
    {
        var max = MaxBytes;
        if (TotalBytes <= max)
        {
            return;
        }

        // Evict oldest-first (Entries is newest-first, so walk from the end).
        for (var i = Entries.Count - 1; i >= 0 && TotalBytes > max; i--)
        {
            var entry = Entries[i];
            Entries.RemoveAt(i);
            TryDeleteFolder(entry.Folder);
        }
    }

    private static SpeechSampleEntry? LoadEntry(string folder)
    {
        try
        {
            var sessionPath = Path.Combine(folder, SessionFileName);
            var audioPath = Path.Combine(folder, AudioFileName);
            if (!File.Exists(sessionPath) || !File.Exists(audioPath))
            {
                return null;
            }

            var json = File.ReadAllText(sessionPath);
            var session = JsonSerializer.Deserialize<SpeechSession>(json, JsonOpts);
            if (session is null)
            {
                return null;
            }

            var audioBytes = new FileInfo(audioPath).Length;
            var sessionBytes = new FileInfo(sessionPath).Length;
            var id = Path.GetFileName(folder);
            return new SpeechSampleEntry(id, session.TimestampUtc, folder, audioPath, audioBytes + sessionBytes, session);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Skipping unreadable speech sample folder {Folder}", folder);
            return null;
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
        var e = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = e.Open();
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
