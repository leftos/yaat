using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>One gold slot of a corpus clip: what the speaker named, its canonical value and the words they used.</summary>
/// <param name="Kind">What kind of thing the slot names.</param>
/// <param name="Value">The canonical value, as the decoder reports it.</param>
/// <param name="Spoken">The spoken form the speaker used.</param>
/// <param name="Note">Why the slot is unusual; null when nothing needs saying.</param>
public sealed record CorpusSlot(EntityKind Kind, string Value, string Spoken, string? Note);

/// <summary>
/// A corpus folder's <c>slots.json</c>: the gold slots of every clip, by clip id, for scoring a decoder's
/// entity output against what the speaker actually named.
/// </summary>
public static class CorpusSlots
{
    /// <summary>The gold file's name inside a corpus folder, beside <c>decisions.json</c>.</summary>
    public const string FileName = "slots.json";

    private static readonly (EntityKind Kind, string Label)[] Known =
    [
        (EntityKind.Callsign, "callsign"),
        (EntityKind.Fix, "fix"),
        (EntityKind.Runway, "runway"),
        (EntityKind.Taxiway, "taxiway"),
        (EntityKind.Gate, "gate"),
        (EntityKind.Landmark, "landmark"),
        (EntityKind.Procedure, "procedure"),
    ];

    private static readonly Dictionary<string, EntityKind> Kinds = Known.ToDictionary(
        entry => entry.Label,
        entry => entry.Kind,
        StringComparer.Ordinal
    );

    /// <summary>
    /// Every clip's gold slots by clip id, or null when the folder carries no <c>slots.json</c>. A clip with
    /// an empty array has no gold slots. Throws <see cref="InvalidDataException"/> on a file that is not a
    /// JSON object of clip id to slot array, on an unknown kind, on a slot with a blank value and on a slot
    /// with no spoken form; every message names the file and the clip.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>>? Read(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        Dictionary<string, List<SlotDto?>?> clips = ReadClips(path);
        var slots = new Dictionary<string, IReadOnlyList<CorpusSlot>>(StringComparer.Ordinal);
        foreach ((string id, List<SlotDto?>? dtos) in clips)
        {
            if (dtos is null)
            {
                throw new InvalidDataException($"{id}: {path} gives no slot array.");
            }
            slots[id] = [.. dtos.Select(dto => ToSlot(dto, id, path))];
        }
        return slots;
    }

    /// <summary>The lowercase label a kind carries in the gold file, the results and the reports.</summary>
    public static string KindLabel(EntityKind kind) => Known.First(entry => entry.Kind == kind).Label;

    /// <summary>The gold file's clips, still as raw slots so each can be validated against its clip id.</summary>
    private static Dictionary<string, List<SlotDto?>?> ReadClips(string path)
    {
        Dictionary<string, List<SlotDto?>?>? clips;
        try
        {
            clips = JsonSerializer.Deserialize<Dictionary<string, List<SlotDto?>?>>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{path} is not a JSON object of clip id to slot array: {error.Message}", error);
        }
        return clips ?? throw new InvalidDataException($"{path} is empty or not a JSON object of clip id to slot array.");
    }

    /// <summary>One raw slot validated into a <see cref="CorpusSlot"/>, naming the clip and the file it is wrong in.</summary>
    private static CorpusSlot ToSlot(SlotDto? dto, string id, string path)
    {
        if (dto is null)
        {
            throw new InvalidDataException($"{id}: {path} holds a null slot.");
        }
        if (string.IsNullOrWhiteSpace(dto.Kind))
        {
            throw new InvalidDataException($"{id}: a slot in {path} has no kind.");
        }
        if (!Kinds.TryGetValue(dto.Kind, out EntityKind kind))
        {
            throw new InvalidDataException($"{id}: {path} gives kind '{dto.Kind}', which is not {string.Join(", ", Kinds.Keys)}.");
        }
        if (string.IsNullOrWhiteSpace(dto.Value))
        {
            throw new InvalidDataException($"{id}: the {dto.Kind} slot in {path} has no value.");
        }
        if (dto.Spoken is null)
        {
            throw new InvalidDataException($"{id}: the {dto.Kind} slot in {path} has no spoken form.");
        }
        return new CorpusSlot(kind, dto.Value, dto.Spoken, dto.Note);
    }

    /// <summary>One slot as the gold file holds it; every member is optional so the errors above can name what is missing.</summary>
    private sealed class SlotDto
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("spoken")]
        public string? Spoken { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
