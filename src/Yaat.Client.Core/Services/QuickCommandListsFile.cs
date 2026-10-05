using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Situation;
using QuickCommandLists = System.Collections.Generic.IReadOnlyDictionary<
    Yaat.Sim.Situation.AircraftSituation,
    System.Collections.Generic.IReadOnlyList<Yaat.Client.ContextMenus.QuickCommandEntry>
>;

namespace Yaat.Client.Services;

/// <summary>Something reading stored quick-command lists left out, and why.</summary>
/// <param name="Situation">The situation name as stored.</param>
/// <param name="Entry">The entry left out (its catalog id, else its label); null when the whole situation was left out.</param>
/// <param name="Reason">Why it was left out.</param>
public sealed record QuickCommandListDrop(string Situation, string? Entry, string Reason);

/// <summary>The result of reading a shareable quick-command list file.</summary>
/// <param name="Lists">The situations' lists the file carried, without anything this build cannot read.</param>
/// <param name="Dropped">The situations and entries left out, reported rather than thrown.</param>
public sealed record QuickCommandListsImport(QuickCommandLists Lists, IReadOnlyList<QuickCommandListDrop> Dropped);

/// <summary>How a merge import settles a situation both the current lists and the file carry.</summary>
public enum QuickCommandClashChoice
{
    /// <summary>Keep the current list.</summary>
    Skip,

    /// <summary>Take the file's list.</summary>
    Overwrite,
}

/// <summary>
/// Reads and writes the shareable quick-command list file (<c>*.yaat-quickcommands.json</c>), and the stored form the
/// preferences keep. Both carry only the situations whose list differs from <see cref="QuickCommandDefaults"/>, keyed by
/// situation name, each entry a tagged object: <c>{ "kind": "catalog", "catalogId": "heading.fly" }</c> or
/// <c>{ "kind": "custom", "label": "…", "commandText": "…", "groundCommandText": "…", "flightRules": "Both" }</c>. Reading
/// walks the JSON node by node, so one malformed situation or entry is left out and reported without losing the rest: an
/// unknown situation (and <see cref="AircraftSituation.Unknown"/>, which has no list), a situation that is not a list, an
/// entry that is not an object, has a non-text field, names a catalog id this build lacks or misses what its kind needs,
/// and a situation whose every entry was left out, which then takes its default.
/// </summary>
public static class QuickCommandListsFile
{
    /// <summary>The file extension (including the dot) used for exported quick-command list files.</summary>
    public const string Extension = ".yaat-quickcommands.json";

    /// <summary>The file format version this build writes.</summary>
    public const int CurrentVersion = 1;

    private const string CatalogKind = "catalog";

    private const string CustomKind = "custom";

    /// <summary>The entry fields read as text; any other JSON value in one of them leaves the entry out.</summary>
    private static readonly string[] TextFields = ["kind", "catalogId", "label", "commandText", "groundCommandText", "flightRules"];

    /// <summary>Serializes the overrides among <paramref name="lists"/> to the shareable file format.</summary>
    /// <param name="lists">Situations' lists; a list equal to its situation's default is left out (<see cref="Overrides"/>).</param>
    /// <returns>Indented JSON with a <c>version</c> and a <c>situations</c> object, in situation order.</returns>
    public static string Serialize(QuickCommandLists lists) =>
        JsonSerializer.Serialize(new ListsFile { Version = CurrentVersion, Situations = ToSaved(lists) }, UserPreferences.JsonOptions);

    /// <summary>
    /// Parses a shareable quick-command list file. What this build cannot read is left out and reported in
    /// <see cref="QuickCommandListsImport.Dropped"/>, so a file from a newer build still imports what it shares.
    /// </summary>
    /// <param name="json">The file contents.</param>
    /// <exception cref="JsonException">The text is not valid JSON, or it has no <c>version</c> number or no <c>situations</c> object.</exception>
    public static QuickCommandListsImport Deserialize(string json)
    {
        ListsFile? file = JsonSerializer.Deserialize<ListsFile>(json, UserPreferences.JsonOptions);
        if (file?.Version is null or < 1)
        {
            throw new JsonException("Quick-command list file has no positive 'version' number.");
        }

        if (file.Situations is null)
        {
            throw new JsonException("Quick-command list file has no 'situations' object.");
        }

        List<QuickCommandListDrop> dropped = [];
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> lists = FromSaved(file.Situations, dropped);
        return new QuickCommandListsImport(Overrides(lists), dropped);
    }

    /// <summary>
    /// The overrides among <paramref name="lists"/>: every situation but <see cref="AircraftSituation.Unknown"/> whose list
    /// differs from its default.
    /// </summary>
    public static QuickCommandLists Overrides(QuickCommandLists lists) =>
        lists
            .Where(pair => (pair.Key != AircraftSituation.Unknown) && !QuickCommandDefaults.IsDefault(pair.Key, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>A replace import: the file's overrides become all the overrides, and every other situation takes its default.</summary>
    public static QuickCommandLists Replace(QuickCommandLists imported) => Overrides(imported);

    /// <summary>
    /// The situations a merge of <paramref name="imported"/> into <paramref name="current"/> must settle: those both carry,
    /// in situation order.
    /// </summary>
    public static IReadOnlyList<AircraftSituation> Clashes(QuickCommandLists current, QuickCommandLists imported) =>
        [.. imported.Keys.Where(current.ContainsKey).Order()];

    /// <summary>
    /// A merge import: <paramref name="current"/> plus every situation of <paramref name="imported"/> it lacks, and each
    /// clash (<see cref="Clashes"/>) settled by <paramref name="choices"/>: <see cref="QuickCommandClashChoice.Skip"/>
    /// keeps the current list, <see cref="QuickCommandClashChoice.Overwrite"/> takes the file's.
    /// </summary>
    /// <exception cref="ArgumentException">A clash has no choice in <paramref name="choices"/>.</exception>
    public static QuickCommandLists Merge(
        QuickCommandLists current,
        QuickCommandLists imported,
        IReadOnlyDictionary<AircraftSituation, QuickCommandClashChoice> choices
    )
    {
        var merged = new Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>>(current);
        foreach ((AircraftSituation situation, IReadOnlyList<QuickCommandEntry> list) in imported)
        {
            if (current.ContainsKey(situation) && (ClashChoice(situation, choices) == QuickCommandClashChoice.Skip))
            {
                continue;
            }

            merged[situation] = list;
        }

        return Overrides(merged);
    }

    /// <summary>The stored form of the overrides among <paramref name="lists"/>, keyed by situation name in situation order.</summary>
    internal static JsonObject ToSaved(QuickCommandLists lists)
    {
        var saved = Overrides(lists)
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Select(ToSaved).ToList(), StringComparer.Ordinal);
        return JsonSerializer.SerializeToNode(saved, UserPreferences.JsonOptions)!.AsObject();
    }

    /// <summary>
    /// The lists in <paramref name="saved"/>, each situation as stored (even when it equals the default), leaving out and
    /// adding to <paramref name="dropped"/> every situation and entry this build cannot read.
    /// </summary>
    internal static Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> FromSaved(JsonObject? saved, List<QuickCommandListDrop> dropped)
    {
        var lists = new Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>>();
        if ((saved is null) || (saved.Count == 0))
        {
            return lists;
        }

        HashSet<string> catalogIds = [.. MenuCatalog.All.Select(entry => entry.Id)];
        foreach ((string name, JsonNode? node) in saved)
        {
            if (ReadSituation(name, node, catalogIds, dropped) is { } read)
            {
                lists[read.Situation] = read.Entries;
            }
        }

        return lists;
    }

    /// <summary>One stored situation's list, or null when it is left out (reported in <paramref name="dropped"/>).</summary>
    private static (AircraftSituation Situation, IReadOnlyList<QuickCommandEntry> Entries)? ReadSituation(
        string name,
        JsonNode? node,
        HashSet<string> catalogIds,
        List<QuickCommandListDrop> dropped
    )
    {
        if (!TryParseSituation(name, out AircraftSituation situation))
        {
            dropped.Add(new QuickCommandListDrop(name, null, "not a situation with a quick-command list"));
            return null;
        }

        if (node is not JsonArray array)
        {
            dropped.Add(new QuickCommandListDrop(name, null, "not a list of entries"));
            return null;
        }

        List<QuickCommandEntry> entries = [];
        foreach (JsonNode? element in array)
        {
            (QuickCommandEntry? entry, string? reason) = ReadEntry(element, catalogIds);
            if (entry is not null)
            {
                entries.Add(entry);
            }
            else
            {
                dropped.Add(new QuickCommandListDrop(name, DropLabel(element), reason ?? "unreadable"));
            }
        }

        if ((array.Count > 0) && (entries.Count == 0))
        {
            dropped.Add(new QuickCommandListDrop(name, null, "no readable entry is left, so the default list applies"));
            return null;
        }

        return (situation, entries.AsReadOnly());
    }

    private static string DropLabel(JsonNode? element) =>
        (element is JsonObject entry) ? (GetString(entry, "catalogId") ?? GetString(entry, "label") ?? "(no id or label)") : "(not an object)";

    private static QuickCommandClashChoice ClashChoice(
        AircraftSituation situation,
        IReadOnlyDictionary<AircraftSituation, QuickCommandClashChoice> choices
    ) =>
        choices.TryGetValue(situation, out QuickCommandClashChoice choice)
            ? choice
            : throw new ArgumentException($"The merge has no Skip or Overwrite choice for the clashing situation {situation}.", nameof(choices));

    private static bool TryParseSituation(string name, out AircraftSituation situation) =>
        Enum.TryParse(name, ignoreCase: false, out situation)
        && (situation != AircraftSituation.Unknown)
        && string.Equals(situation.ToString(), name, StringComparison.Ordinal);

    private static SavedQuickCommandEntry ToSaved(QuickCommandEntry entry) =>
        entry switch
        {
            CatalogQuickCommandEntry catalog => new SavedQuickCommandEntry
            {
                Kind = CatalogKind,
                CatalogId = catalog.CatalogId,
                FlightRules = catalog.FlightRules?.ToString(),
            },
            CustomQuickCommandEntry custom => new SavedQuickCommandEntry
            {
                Kind = CustomKind,
                Label = custom.Label,
                CommandText = custom.CommandText,
                GroundCommandText = string.IsNullOrWhiteSpace(custom.GroundCommandText) ? null : custom.GroundCommandText,
                FlightRules = custom.FlightRules.ToString(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown quick-command entry kind."),
        };

    private static (QuickCommandEntry? Entry, string? Reason) ReadEntry(JsonNode? element, HashSet<string> catalogIds)
    {
        if (element is not JsonObject entry)
        {
            return (null, "not an object");
        }

        if (TextFields.FirstOrDefault(field => !TryGetString(entry, field, out _)) is { } field)
        {
            return (null, $"'{field}' is not text");
        }

        return GetString(entry, "kind") switch
        {
            null => (null, "no kind"),
            CatalogKind => ReadCatalog(entry, catalogIds),
            CustomKind => ReadCustom(entry),
            string kind => (null, $"unknown kind '{kind}'"),
        };
    }

    private static (QuickCommandEntry? Entry, string? Reason) ReadCatalog(JsonObject entry, HashSet<string> catalogIds)
    {
        string? catalogId = GetString(entry, "catalogId");
        if (string.IsNullOrWhiteSpace(catalogId) || !catalogIds.Contains(catalogId))
        {
            return (null, "not a catalog id this build has");
        }

        string? flightRules = GetString(entry, "flightRules");
        if (flightRules is null)
        {
            return (new CatalogQuickCommandEntry(catalogId, null), null);
        }

        return TryParseFlightRules(flightRules, out MenuFlightRules rules)
            ? (new CatalogQuickCommandEntry(catalogId, rules), null)
            : (null, $"unknown flight rules '{flightRules}'");
    }

    private static (QuickCommandEntry? Entry, string? Reason) ReadCustom(JsonObject entry)
    {
        string? label = GetString(entry, "label");
        string? commandText = GetString(entry, "commandText");
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(commandText))
        {
            return (null, "a custom entry needs a label and a command text");
        }

        string? flightRules = GetString(entry, "flightRules");
        MenuFlightRules rules = MenuFlightRules.Both;
        if ((flightRules is not null) && !TryParseFlightRules(flightRules, out rules))
        {
            return (null, $"unknown flight rules '{flightRules}'");
        }

        string? ground = GetString(entry, "groundCommandText");
        return (new CustomQuickCommandEntry(label, commandText, string.IsNullOrWhiteSpace(ground) ? null : ground, rules), null);
    }

    /// <summary>
    /// Reads <paramref name="name"/> from <paramref name="entry"/> as text: true with the text, or with null when the field
    /// is absent or JSON null; false when it holds any other JSON value.
    /// </summary>
    private static bool TryGetString(JsonObject entry, string name, out string? value)
    {
        value = null;
        if (entry[name] is not { } node)
        {
            return true;
        }

        return (node is JsonValue text) && text.TryGetValue(out value);
    }

    /// <summary>The text in <paramref name="name"/>, or null when it is absent, JSON null or not text.</summary>
    private static string? GetString(JsonObject entry, string name) => TryGetString(entry, name, out string? value) ? value : null;

    private static bool TryParseFlightRules(string text, out MenuFlightRules rules) =>
        Enum.TryParse(text, ignoreCase: false, out rules) && string.Equals(rules.ToString(), text, StringComparison.Ordinal);

    private sealed class ListsFile
    {
        public int? Version { get; set; }

        public JsonObject? Situations { get; set; }
    }
}

/// <summary>
/// One quick-command list entry as the preferences and the shareable file write it: <see cref="Kind"/> says which of the
/// other fields it carries. Reading walks the JSON nodes instead, so one malformed entry cannot fail the rest.
/// </summary>
internal sealed class SavedQuickCommandEntry
{
    /// <summary><c>"catalog"</c> or <c>"custom"</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>A catalog entry's action id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogId { get; set; }

    /// <summary>A custom entry's menu text.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>A custom entry's command.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandText { get; set; }

    /// <summary>A custom entry's command on the ground; absent for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroundCommandText { get; set; }

    /// <summary>The <see cref="MenuFlightRules"/> name; absent on a catalog entry that takes its catalog default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FlightRules { get; set; }
}
