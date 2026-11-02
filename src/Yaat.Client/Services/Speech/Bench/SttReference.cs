using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>The reference accuracy of one model in one mode, measured once on the CI natives.</summary>
/// <param name="Engine">The model.</param>
/// <param name="Mode">The transcription mode.</param>
/// <param name="OwnerWer">The word error rate over the owner-checked corpus; null when not measured.</param>
/// <param name="CallsignRecall">The share of callsigns heard right; null when not measured.</param>
public sealed record SttReferenceEntry(SttEngine Engine, SttMode Mode, double? OwnerWer, double? CallsignRecall);

/// <summary>The raw shape of <c>stt-reference.json</c>, before the engine names are checked.</summary>
/// <param name="Schema">The file's schema version; only 1 is read.</param>
/// <param name="Corpus">The corpus the figures were measured on; null before any were.</param>
/// <param name="SttNative">The sherpa native build the figures were measured with; null before any were.</param>
/// <param name="Entries">One entry per model and mode.</param>
public sealed record SttReferenceFile(int Schema, string? Corpus, string? SttNative, List<SttReferenceFileEntry>? Entries);

/// <summary>One raw entry of <c>stt-reference.json</c>, its engine still a name.</summary>
/// <param name="Engine">The <see cref="SttEngine"/> name.</param>
/// <param name="Mode">The transcription mode.</param>
/// <param name="OwnerWer">The owner-checked word error rate, or null.</param>
/// <param name="CallsignRecall">The callsign recall, or null.</param>
public sealed record SttReferenceFileEntry(string? Engine, SttMode Mode, double? OwnerWer, double? CallsignRecall);

/// <summary>
/// The reference WER and callsign recall the speech benchmark shows beside this machine's figures, read from the
/// committed <c>Speech/stt-reference.json</c>.
/// </summary>
public sealed class SttReference
{
    /// <summary>The schema version this reader understands.</summary>
    public const int SchemaVersion = 1;

    private static readonly ILogger Log = AppLog.CreateLogger<SttReference>();

    private SttReference(int schema, string? corpus, string? sttNative, IReadOnlyList<SttReferenceEntry> entries)
    {
        Schema = schema;
        Corpus = corpus;
        SttNative = sttNative;
        Entries = entries;
    }

    /// <summary>The file's schema version.</summary>
    public int Schema { get; }

    /// <summary>The corpus the figures were measured on; null before any were.</summary>
    public string? Corpus { get; }

    /// <summary>The sherpa native build the figures were measured with; null before any were.</summary>
    public string? SttNative { get; }

    /// <summary>Every entry whose engine this build knows.</summary>
    public IReadOnlyList<SttReferenceEntry> Entries { get; }

    /// <summary>Reads the file; an entry naming an engine this build does not know is skipped with a warning.</summary>
    /// <param name="path">The reference file.</param>
    /// <exception cref="InvalidDataException">The file is not valid JSON of schema 1; the message names the path.</exception>
    public static SttReference Load(string path)
    {
        (SttReferenceFile file, List<SttReferenceFileEntry> rawEntries) = ReadFile(path);
        var entries = new List<SttReferenceEntry>();
        foreach (SttReferenceFileEntry raw in rawEntries)
        {
            if (string.IsNullOrEmpty(raw.Engine))
            {
                throw new InvalidDataException($"{path} has an entry with no engine");
            }
            if (!Enum.GetNames<SttEngine>().Contains(raw.Engine, StringComparer.Ordinal))
            {
                Log.LogWarning("{Path} names an unknown engine {Engine}; its entry is skipped", path, raw.Engine);
                continue;
            }
            entries.Add(new SttReferenceEntry(Enum.Parse<SttEngine>(raw.Engine), raw.Mode, raw.OwnerWer, raw.CallsignRecall));
        }
        return new SttReference(file.Schema, file.Corpus, file.SttNative, entries);
    }

    /// <summary>The entry for <paramref name="engine"/> in <paramref name="mode"/>; null when the file has none.</summary>
    public SttReferenceEntry? Find(SttEngine engine, SttMode mode) => Entries.FirstOrDefault(e => (e.Engine == engine) && (e.Mode == mode));

    /// <summary>The file and its entries list, checked to be present and of the schema this build reads.</summary>
    private static (SttReferenceFile File, List<SttReferenceFileEntry> Entries) ReadFile(string path)
    {
        SttReferenceFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), SttBenchJsonContext.Default.SttReferenceFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a valid STT reference file: {ex.Message}", ex);
        }
        if (file is null)
        {
            throw new InvalidDataException($"{path} is not a valid STT reference file: it holds null");
        }
        if (file.Schema != SchemaVersion)
        {
            throw new InvalidDataException($"{path} has schema {file.Schema}; this build reads schema {SchemaVersion}");
        }
        return file.Entries is { } entries ? (file, entries) : throw new InvalidDataException($"{path} has no entries list");
    }
}
