using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.SpeechSandbox;

/// <summary>One corpus feed's room: the airport its controller works, the facility, and the seed its distractors are drawn from.</summary>
/// <param name="Airport">The airport's ICAO id ("KOAK").</param>
/// <param name="Facility">The controller's facility: <c>gnd</c>, <c>twr</c> or <c>app</c>.</param>
/// <param name="Seed">The seed of the padded room's distractor draws.</param>
public sealed record CorpusRoomFeed(string Airport, string Facility, int Seed);

/// <summary>
/// A corpus folder's <c>rooms.json</c>: the room each feed's clips are decoded against, plus the telephony and aircraft types
/// the built-in tables do not know.
/// </summary>
/// <param name="Telephony">Telephony by airline prefix, consulted before the built-in table; keys compared ignoring case.</param>
/// <param name="Types">Aircraft type designator by callsign; keys compared ignoring case.</param>
/// <param name="Feeds">Each feed's room by feed name ("koak-app"); keys compared ordinally.</param>
public sealed record CorpusRooms(
    IReadOnlyDictionary<string, string> Telephony,
    IReadOnlyDictionary<string, string> Types,
    IReadOnlyDictionary<string, CorpusRoomFeed> Feeds
)
{
    /// <summary>The rooms file's name inside a corpus folder, beside <c>slots.json</c>.</summary>
    public const string FileName = "rooms.json";

    private static readonly string[] Facilities = ["gnd", "twr", "app"];

    /// <summary>
    /// The folder's rooms, or null when it carries no <c>rooms.json</c>. <c>telephony</c> and <c>types</c> are optional;
    /// <c>feeds</c> is required and non-empty. Throws <see cref="InvalidDataException"/> on a file that is not a JSON object of
    /// that shape, on no feeds, and on a feed with a blank airport, a facility other than <c>gnd</c>, <c>twr</c> or <c>app</c>,
    /// or a seed that is not an integer; every message names the file, and the feed where there is one.
    /// </summary>
    public static CorpusRooms? Read(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        RoomsDto dto = ReadDto(path);
        if ((dto.Feeds is null) || (dto.Feeds.Count == 0))
        {
            throw new InvalidDataException($"{path} has no feeds; it needs a \"feeds\" object of feed name to airport, facility and seed.");
        }
        var feeds = new Dictionary<string, CorpusRoomFeed>(StringComparer.Ordinal);
        foreach ((string name, FeedDto feed) in dto.Feeds)
        {
            feeds[name] = ToFeed(feed, name, path);
        }
        return new CorpusRooms(IgnoringCase(dto.Telephony), IgnoringCase(dto.Types), feeds);
    }

    private static RoomsDto ReadDto(string path)
    {
        RoomsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RoomsDto>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{path} is not a JSON object of telephony, types and feeds: {error.Message}", error);
        }
        return dto ?? throw new InvalidDataException($"{path} is empty or not a JSON object of telephony, types and feeds.");
    }

    private static Dictionary<string, string> IgnoringCase(Dictionary<string, string>? map) =>
        map is null ? new(StringComparer.OrdinalIgnoreCase) : new(map, StringComparer.OrdinalIgnoreCase);

    /// <summary>One raw feed validated into a <see cref="CorpusRoomFeed"/>, naming the feed and the file it is wrong in.</summary>
    private static CorpusRoomFeed ToFeed(FeedDto? dto, string name, string path)
    {
        if ((dto is null) || string.IsNullOrWhiteSpace(dto.Airport))
        {
            throw new InvalidDataException($"{name}: the feed in {path} has no airport.");
        }
        if (dto.Facility is not ("gnd" or "twr" or "app"))
        {
            throw new InvalidDataException($"{name}: {path} gives facility '{dto.Facility}', which is not {string.Join(", ", Facilities)}.");
        }
        if ((dto.Seed is not { ValueKind: JsonValueKind.Number } seed) || !seed.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"{name}: the feed in {path} has no integer seed.");
        }
        return new CorpusRoomFeed(dto.Airport, dto.Facility, value);
    }

    /// <summary>The rooms file as written; every member is optional so the errors above can name what is missing.</summary>
    private sealed class RoomsDto
    {
        [JsonPropertyName("telephony")]
        public Dictionary<string, string>? Telephony { get; set; }

        [JsonPropertyName("types")]
        public Dictionary<string, string>? Types { get; set; }

        [JsonPropertyName("feeds")]
        public Dictionary<string, FeedDto>? Feeds { get; set; }
    }

    /// <summary>One feed as the rooms file holds it.</summary>
    private sealed class FeedDto
    {
        [JsonPropertyName("airport")]
        public string? Airport { get; set; }

        [JsonPropertyName("facility")]
        public string? Facility { get; set; }

        [JsonPropertyName("seed")]
        public JsonElement? Seed { get; set; }
    }
}
