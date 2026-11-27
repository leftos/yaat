using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring;

/// <summary>Which side a merge row takes when the author does not choose.</summary>
public enum MergeRowKind
{
    /// <summary>Only mine changed the field since the base: mine merges by itself.</summary>
    AutoMine,

    /// <summary>Only theirs changed the field since the base: theirs merges by itself.</summary>
    AutoTheirs,

    /// <summary>Both sides changed the field differently, or there is no base: the author chooses.</summary>
    Clash,
}

/// <summary>One field or list item where mine and theirs differ.</summary>
/// <param name="Path">
/// The JSON Pointer of the field, the key of its choice. A list matched by id numbers its items in mine's order, then the
/// items only theirs holds.
/// </param>
/// <param name="Section">The editor tab the field sits on (<c>General</c>, <c>Aircraft</c>) or its General-tab list section.</param>
/// <param name="Label">The editor's label for the field; a field the editor does not show is labelled by its JSON name.</param>
/// <param name="ItemKey">The top-level list item's id, or <c>#&lt;index&gt;</c> when it has none; null outside a list.</param>
/// <param name="Kind">Which side it takes unless a choice overrides it.</param>
/// <param name="Base">The base's value; null when the base lacks it, holds JSON null, or the merge is two-way.</param>
/// <param name="Mine">Mine's value; null when mine lacks it or holds JSON null.</param>
/// <param name="Theirs">Theirs' value; null when theirs lacks it or holds JSON null.</param>
public sealed record MergeRow(
    string Path,
    string Section,
    string Label,
    string? ItemKey,
    MergeRowKind Kind,
    JsonNode? Base,
    JsonNode? Mine,
    JsonNode? Theirs
);

/// <summary>What a merge choice writes.</summary>
public enum MergeChoiceKind
{
    /// <summary>Mine's value, or no field when mine lacks it.</summary>
    KeepMine,

    /// <summary>Theirs' value, or no field when theirs lacks it.</summary>
    KeepTheirs,

    /// <summary>A value the author edited.</summary>
    Edit,
}

/// <summary>The author's choice for one merge row.</summary>
public sealed class MergeChoice
{
    private MergeChoice(MergeChoiceKind kind, JsonNode? value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Gets the choice that keeps mine.</summary>
    public static MergeChoice KeepMine { get; } = new(MergeChoiceKind.KeepMine, null);

    /// <summary>Gets the choice that keeps theirs.</summary>
    public static MergeChoice KeepTheirs { get; } = new(MergeChoiceKind.KeepTheirs, null);

    /// <summary>Gets what the choice writes.</summary>
    public MergeChoiceKind Kind { get; }

    /// <summary>Gets the edited value of an <see cref="MergeChoiceKind.Edit"/> choice; null otherwise, or for JSON null.</summary>
    public JsonNode? Value { get; }

    /// <summary>A choice that writes <paramref name="value"/> (a copy of it); null writes JSON null.</summary>
    /// <param name="value">The edited value.</param>
    /// <returns>The choice.</returns>
    public static MergeChoice Edit(JsonNode? value) => new(MergeChoiceKind.Edit, value?.DeepClone());

    internal static MergeChoice Side(MergeChoiceKind side) =>
        side switch
        {
            MergeChoiceKind.KeepMine => KeepMine,
            MergeChoiceKind.KeepTheirs => KeepTheirs,
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "A bulk choice keeps mine or theirs; an edit is made per row."),
        };
}

/// <summary>
/// Plans a merge of two scenario JSON texts on the raw JSON, so fields the model does not know survive. Three-way with the
/// base text: a change on one side only merges by itself and only a real clash asks. Two-way without it: every difference asks.
/// </summary>
public static class ScenarioMerge
{
    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioMerge");

    /// <summary>Compares mine and theirs, against the base when there is one.</summary>
    /// <param name="baseJson">The text both sides started from; null for a two-way merge.</param>
    /// <param name="mineJson">Mine: the working copy.</param>
    /// <param name="theirsJson">Theirs: the other version.</param>
    /// <returns>The plan, one row per field or list item where mine and theirs differ.</returns>
    /// <exception cref="JsonException">A text is malformed or not a JSON object.</exception>
    public static ScenarioMergePlan Plan(string? baseJson, string mineJson, string theirsJson)
    {
        ArgumentNullException.ThrowIfNull(mineJson);
        ArgumentNullException.ThrowIfNull(theirsJson);
        JsonObject? baseRoot = baseJson is null ? null : MergeTree.ParseRoot(baseJson);
        var plan = new ScenarioMergePlan(new MergeTree(baseRoot, MergeTree.ParseRoot(mineJson), MergeTree.ParseRoot(theirsJson)));
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Merge planned: {Rows} rows, {Clashes} clashes, two-way {TwoWay}", plan.Rows.Count, plan.ClashCount, plan.IsTwoWay);
        }

        return plan;
    }
}

/// <summary>A planned merge: its rows, and the merged text once every clash has a choice.</summary>
public sealed class ScenarioMergePlan
{
    private readonly MergeTree _tree;

    internal ScenarioMergePlan(MergeTree tree)
    {
        _tree = tree;
        Rows = [.. tree.Rows.Select(planned => planned.Row)];
    }

    /// <summary>Gets a value indicating whether the merge has no base, so every difference is a clash.</summary>
    public bool IsTwoWay => _tree.IsTwoWay;

    /// <summary>Gets the rows in document order; a field identical on both sides has none.</summary>
    public IReadOnlyList<MergeRow> Rows { get; }

    /// <summary>Gets the number of one-sided changes, from both sides, that merge by themselves.</summary>
    public int AutoMergedCount => Rows.Count(row => row.Kind != MergeRowKind.Clash);

    /// <summary>Gets the number of clashes.</summary>
    public int ClashCount => Rows.Count(row => row.Kind == MergeRowKind.Clash);

    /// <summary>
    /// Writes the merged scenario: each row takes its choice, an auto row its own side when it has none; a field with no row
    /// is the same on both sides and is kept, known to the model or not. Indented JSON.
    /// </summary>
    /// <param name="choicesByPath">The author's choices keyed by row path.</param>
    /// <returns>The merged scenario JSON.</returns>
    /// <exception cref="InvalidOperationException">A clash has no choice; the message names its path.</exception>
    /// <exception cref="ArgumentException">A choice's path is no row's.</exception>
    public string Apply(IReadOnlyDictionary<string, MergeChoice> choicesByPath)
    {
        ArgumentNullException.ThrowIfNull(choicesByPath);
        return _tree.Apply(choicesByPath);
    }

    /// <summary>Choices keeping one side for every clash.</summary>
    /// <param name="side"><see cref="MergeChoiceKind.KeepMine"/> or <see cref="MergeChoiceKind.KeepTheirs"/>.</param>
    /// <returns>The choices keyed by row path.</returns>
    public IReadOnlyDictionary<string, MergeChoice> ChooseAll(MergeChoiceKind side) => Choose(_ => true, side);

    /// <summary>Choices keeping one side for every clash in <paramref name="section"/>.</summary>
    /// <param name="section">A row's <see cref="MergeRow.Section"/>.</param>
    /// <param name="side"><see cref="MergeChoiceKind.KeepMine"/> or <see cref="MergeChoiceKind.KeepTheirs"/>.</param>
    /// <returns>The choices keyed by row path.</returns>
    public IReadOnlyDictionary<string, MergeChoice> ChooseSection(string section, MergeChoiceKind side) =>
        Choose(row => string.Equals(row.Section, section, StringComparison.Ordinal), side);

    /// <summary>Choices keeping one side for every clash of one list item (an aircraft, an ATC position, ...).</summary>
    /// <param name="section">The item's <see cref="MergeRow.Section"/>: a positional key (<c>#0</c>) repeats across lists.</param>
    /// <param name="itemKey">A row's <see cref="MergeRow.ItemKey"/>.</param>
    /// <param name="side"><see cref="MergeChoiceKind.KeepMine"/> or <see cref="MergeChoiceKind.KeepTheirs"/>.</param>
    /// <returns>The choices keyed by row path.</returns>
    public IReadOnlyDictionary<string, MergeChoice> ChooseItem(string section, string itemKey, MergeChoiceKind side) =>
        Choose(
            row => string.Equals(row.Section, section, StringComparison.Ordinal) && string.Equals(row.ItemKey, itemKey, StringComparison.Ordinal),
            side
        );

    private Dictionary<string, MergeChoice> Choose(Func<MergeRow, bool> where, MergeChoiceKind side)
    {
        var choice = MergeChoice.Side(side);
        return Rows.Where(row => (row.Kind == MergeRowKind.Clash) && where(row)).ToDictionary(row => row.Path, _ => choice, StringComparer.Ordinal);
    }
}

/// <summary>A value at a place on one side; <see cref="Present"/> is false where the side has no member or item there.</summary>
internal readonly record struct JsonSlot(bool Present, JsonNode? Value)
{
    public static JsonSlot Absent => default;

    public static JsonSlot Of(JsonNode? value) => new(true, value);

    public bool SameAs(JsonSlot other) => (Present == other.Present) && (!Present || JsonNode.DeepEquals(Value, other.Value));

    public JsonSlot Cloned() => Present ? Of(Value?.DeepClone()) : Absent;
}

/// <summary>Where a value sits: its pointer, its member name, and the section, item and label path it is shown under.</summary>
internal sealed record FieldPlace(string Path, string? Key, string Section, string? ItemKey, string LabelPath)
{
    public static FieldPlace Root { get; } = new("", null, ScenarioFieldLabels.General, null, "");

    public string Label => ScenarioFieldLabels.Label(Section, LabelPath);

    public FieldPlace Member(string key)
    {
        string path = Path + "/" + Escape(key);
        if (Path.Length > 0)
        {
            return new FieldPlace(path, key, Section, ItemKey, Join(LabelPath, key));
        }

        string section = ScenarioFieldLabels.SectionOf(key);
        return new FieldPlace(path, key, section, null, section == ScenarioFieldLabels.General ? key : "");
    }

    public FieldPlace Item(int index, string? id)
    {
        string number = index.ToString(CultureInfo.InvariantCulture);
        // A top-level list sits right under the root, its pointer one segment long (a member name escapes its slashes).
        string? itemKey = Path.LastIndexOf('/') == 0 ? (id ?? "#" + number) : ItemKey;
        return new FieldPlace(Path + "/" + number, null, Section, itemKey, LabelPath + "[]");
    }

    private static string Join(string parent, string key) => parent is "" or "[]" ? key : parent + "/" + key;

    private static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>The three sides' values at one place.</summary>
internal readonly record struct MergeSlots(FieldPlace Place, JsonSlot Base, JsonSlot Mine, JsonSlot Theirs);

/// <summary>A row with mine's and theirs' slots, which say whether each side holds the value.</summary>
internal sealed record PlannedRow(MergeRow Row, JsonSlot Mine, JsonSlot Theirs);

/// <summary>
/// Walks base, mine and theirs together: where mine and theirs are both objects, or both lists of objects, it descends
/// (members by name, list items by id when every item of mine and theirs has a distinct one, else by position); anywhere
/// else a difference between mine and theirs is one row.
/// </summary>
internal sealed class MergeTree
{
    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioMerge");

    private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly List<PlannedRow> _rows = [];

    private readonly Dictionary<string, PlannedRow> _rowsByPath = new(StringComparer.Ordinal);

    // The places with rows below them: Apply rebuilds these and copies mine everywhere else.
    private readonly HashSet<string> _branches = new(StringComparer.Ordinal);

    private readonly MergeSlots _root;

    public MergeTree(JsonObject? baseRoot, JsonObject mine, JsonObject theirs)
    {
        IsTwoWay = baseRoot is null;
        _root = new MergeSlots(FieldPlace.Root, baseRoot is null ? JsonSlot.Absent : JsonSlot.Of(baseRoot), JsonSlot.Of(mine), JsonSlot.Of(theirs));
        Walk(_root);
    }

    public bool IsTwoWay { get; }

    public IReadOnlyList<PlannedRow> Rows => _rows;

    public static JsonObject ParseRoot(string json) =>
        JsonNode.Parse(json, documentOptions: ParseOptions) as JsonObject ?? throw new JsonException("Scenario JSON is not an object.");

    public string Apply(IReadOnlyDictionary<string, MergeChoice> choices)
    {
        string? unknown = choices.Keys.FirstOrDefault(path => !_rowsByPath.ContainsKey(path));
        if (unknown is not null)
        {
            throw new ArgumentException($"No merge row has the path '{unknown}'.", nameof(choices));
        }

        return Resolve(_root, choices).Value!.ToJsonString(WriteOptions);
    }

    private static bool IsItemList(JsonSlot slot) => (slot.Value is JsonArray items) && items.All(item => item is JsonObject);

    private static bool HasChildren(MergeSlots slots) =>
        ((slots.Mine.Value is JsonObject) && (slots.Theirs.Value is JsonObject)) || (IsItemList(slots.Mine) && IsItemList(slots.Theirs));

    private static IEnumerable<MergeSlots> Children(MergeSlots slots) =>
        slots.Mine.Value is JsonObject mine
            ? Members(slots, mine, (JsonObject)slots.Theirs.Value!)
            : Items(slots, (JsonArray)slots.Mine.Value!, (JsonArray)slots.Theirs.Value!);

    private static IEnumerable<MergeSlots> Members(MergeSlots slots, JsonObject mine, JsonObject theirs)
    {
        var baseObject = slots.Base.Value as JsonObject;
        List<string> keys = [.. mine.Select(member => member.Key)];
        keys.AddRange(theirs.Select(member => member.Key).Where(key => !mine.ContainsKey(key)));
        return keys.Select(key => new MergeSlots(slots.Place.Member(key), Member(baseObject, key), Member(mine, key), Member(theirs, key)));
    }

    private static JsonSlot Member(JsonObject? owner, string key) =>
        ((owner is not null) && owner.TryGetPropertyValue(key, out JsonNode? value)) ? JsonSlot.Of(value) : JsonSlot.Absent;

    private static IEnumerable<MergeSlots> Items(MergeSlots slots, JsonArray mine, JsonArray theirs)
    {
        var baseItems = slots.Base.Value as JsonArray;
        if (!HasDistinctIds(mine) || !HasDistinctIds(theirs))
        {
            if (((mine.Count > 0) || (theirs.Count > 0)) && Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("List at {Path} matched by position: not every item carries a distinct id", slots.Place.Path);
            }

            return Enumerable
                .Range(0, Math.Max(mine.Count, theirs.Count))
                .Select(i => new MergeSlots(slots.Place.Item(i, null), At(baseItems, i), At(mine, i), At(theirs, i)));
        }

        List<string> ids = [.. mine.Select(item => IdOf(item)!)];
        var mineIds = new HashSet<string>(ids, StringComparer.Ordinal);
        ids.AddRange(theirs.Select(item => IdOf(item)!).Where(id => !mineIds.Contains(id)));
        return ids.Select((id, i) => new MergeSlots(slots.Place.Item(i, id), WithId(baseItems, id), WithId(mine, id), WithId(theirs, id)));
    }

    private static JsonSlot At(JsonArray? items, int index) =>
        ((items is not null) && (index < items.Count)) ? JsonSlot.Of(items[index]) : JsonSlot.Absent;

    private static JsonSlot WithId(JsonArray? items, string id) =>
        items?.FirstOrDefault(item => IdOf(item) == id) is JsonNode match ? JsonSlot.Of(match) : JsonSlot.Absent;

    private static string? IdOf(JsonNode? item) =>
        (
            (item is JsonObject owner)
            && owner.TryGetPropertyValue("id", out JsonNode? id)
            && (id is JsonValue value)
            && value.TryGetValue(out string? text)
            && !string.IsNullOrEmpty(text)
        )
            ? text
            : null;

    private static bool HasDistinctIds(JsonArray items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.All(item => (IdOf(item) is string id) && seen.Add(id));
    }

    private void Walk(MergeSlots slots)
    {
        if (slots.Mine.SameAs(slots.Theirs))
        {
            return;
        }

        if (!HasChildren(slots))
        {
            AddRow(slots);
            return;
        }

        _branches.Add(slots.Place.Path);
        foreach (MergeSlots child in Children(slots))
        {
            Walk(child);
        }
    }

    private void AddRow(MergeSlots slots)
    {
        MergeRowKind kind =
            IsTwoWay ? MergeRowKind.Clash
            : slots.Base.SameAs(slots.Mine) ? MergeRowKind.AutoTheirs
            : slots.Base.SameAs(slots.Theirs) ? MergeRowKind.AutoMine
            : MergeRowKind.Clash;
        FieldPlace place = slots.Place;
        var row = new MergeRow(
            place.Path,
            place.Section,
            place.Label,
            place.ItemKey,
            kind,
            slots.Base.Value?.DeepClone(),
            slots.Mine.Value?.DeepClone(),
            slots.Theirs.Value?.DeepClone()
        );
        var planned = new PlannedRow(row, slots.Mine, slots.Theirs);
        _rows.Add(planned);
        _rowsByPath.Add(place.Path, planned);
    }

    private JsonSlot Resolve(MergeSlots slots, IReadOnlyDictionary<string, MergeChoice> choices)
    {
        if (_rowsByPath.TryGetValue(slots.Place.Path, out PlannedRow? row))
        {
            return Choose(row, choices);
        }

        if (!_branches.Contains(slots.Place.Path))
        {
            return slots.Mine.Cloned();
        }

        IEnumerable<(FieldPlace Place, JsonSlot Value)> children = Children(slots).Select(child => (child.Place, Resolve(child, choices)));
        if (slots.Mine.Value is JsonObject)
        {
            var merged = new JsonObject();
            foreach ((FieldPlace place, JsonSlot value) in children.Where(child => child.Value.Present))
            {
                merged.Add(place.Key!, value.Value);
            }

            return JsonSlot.Of(merged);
        }

        return JsonSlot.Of(new JsonArray([.. children.Where(child => child.Value.Present).Select(child => child.Value.Value)]));
    }

    private static JsonSlot Choose(PlannedRow planned, IReadOnlyDictionary<string, MergeChoice> choices)
    {
        MergeRow row = planned.Row;
        MergeChoice choice = choices.TryGetValue(row.Path, out MergeChoice? chosen)
            ? chosen
            : row.Kind switch
            {
                MergeRowKind.AutoMine => MergeChoice.KeepMine,
                MergeRowKind.AutoTheirs => MergeChoice.KeepTheirs,
                _ => throw new InvalidOperationException($"The clash at {row.Path} ({row.Label}) has no choice: keep mine, keep theirs or edit it."),
            };
        return choice.Kind switch
        {
            MergeChoiceKind.KeepMine => planned.Mine.Cloned(),
            MergeChoiceKind.KeepTheirs => planned.Theirs.Cloned(),
            _ => JsonSlot.Of(choice.Value?.DeepClone()),
        };
    }
}
