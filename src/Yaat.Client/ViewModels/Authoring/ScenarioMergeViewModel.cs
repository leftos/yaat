using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The merge window's tabs.</summary>
public enum ScenarioMergeTab
{
    /// <summary>The General tab: settings and the General-tab lists.</summary>
    General,

    /// <summary>The Aircraft tab.</summary>
    Aircraft,
}

/// <summary>
/// The merge window over a planned merge: one row per difference, grouped into the General tab's sections of lines and the
/// Aircraft tab's aircraft lines; the author's choices keyed by row path, the counts and texts that gate Apply,
/// Previous/Next over every clash in the editor's order, and the base strips.
/// </summary>
public sealed partial class ScenarioMergeViewModel : ObservableObject
{
    internal const string GeneralSection = "General";

    private const string AircraftSectionName = "Aircraft";

    private const string AircraftKey = "aircraft";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioMergeViewModel>();

    private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    // The General tab's lists in view order, by their scenario member.
    private static readonly (string Key, string Section)[] Lists =
    [
        ("atc", "ATC positions"),
        ("initializationTriggers", "Initialization triggers"),
        ("aircraftGenerators", "Generators"),
        ("vfrArrivalGenerators", "VFR arrival generators"),
        ("overflightGenerators", "Overflight generators"),
        ("flightStripConfigurations", "Strip bays"),
    ];

    private static readonly Dictionary<string, int> SectionRanks = new(StringComparer.Ordinal)
    {
        ["General"] = 0,
        ["ATC positions"] = 1,
        ["Initialization triggers"] = 2,
        ["Generators"] = 3,
        ["VFR arrival generators"] = 4,
        ["Overflight generators"] = 5,
        ["Strip bays"] = 6,
        [AircraftSectionName] = 7,
    };

    private readonly Dictionary<string, MergeChoice> _choices = new(StringComparer.Ordinal);

    private readonly List<ScenarioMergeRowViewModel> _clashOrder;

    private readonly Dictionary<ScenarioMergeRowViewModel, ScenarioMergeScopeViewModel> _lineOfRow = [];

    private int _position = -1;

    /// <summary>The number of clashes with no choice yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private int _openClashCount;

    /// <summary>The General tab's count of clashes with no choice yet.</summary>
    [ObservableProperty]
    private int _generalOpenClashCount;

    /// <summary>The Aircraft tab's count of clashes with no choice yet.</summary>
    [ObservableProperty]
    private int _aircraftOpenClashCount;

    /// <summary>The toolbar's summary of what merged by itself and what is left to choose.</summary>
    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>The footer's text: what Apply waits on, or that nothing is saved until it runs.</summary>
    [ObservableProperty]
    private string _footerText = "";

    /// <summary>Where Previous/Next stands ("clash n of N", two-way "difference n of N"); empty before the first move.</summary>
    [ObservableProperty]
    private string _positionText = "";

    /// <summary>The clash Previous/Next last selected; null before the first move.</summary>
    [ObservableProperty]
    private ScenarioMergeRowViewModel? _currentClash;

    /// <summary>The line holding the clash Previous/Next last selected: a section line or an aircraft line.</summary>
    [ObservableProperty]
    private ScenarioMergeScopeViewModel? _currentLine;

    /// <summary>The tab shown; Previous/Next moves it to the selected clash's tab.</summary>
    [ObservableProperty]
    private ScenarioMergeTab _selectedTab;

    /// <summary>The aircraft line selected on the Aircraft tab; Previous/Next selects the line of an aircraft's clash.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAircraftId))]
    private ScenarioMergeAircraftLineViewModel? _selectedAircraftLine;

    /// <summary>Whether lines identical on both sides are hidden.</summary>
    [ObservableProperty]
    private bool _onlyDifferences;

    /// <summary>Whether base strips show under auto-merged rows too; clashes always show theirs in a three-way merge.</summary>
    [ObservableProperty]
    private bool _showBase;

    private ScenarioMergeViewModel(ScenarioMergeRequest request, ScenarioMergePlan plan)
    {
        Request = request;
        Plan = plan;
        Rows = [.. plan.Rows.Select((row, index) => new ScenarioMergeRowViewModel(row, plan.IsTwoWay, index, Choose))];
        WatchEditors();
        _clashOrder = [.. Rows.Where(row => row.IsClash).OrderBy(row => RankOf(row.Section)).ThenBy(row => row.Index)];
        var sides = MergeSides.Read(request.Texts);
        Sections = [FieldSection(sides), .. Lists.Select(list => ListSection(sides, list.Key, list.Section))];
        AircraftLines = AircraftLinesOf(sides);
        AircraftSection = Section(AircraftSectionName, OrderLines(sides, AircraftSectionName, AircraftKey));
        Refresh();
    }

    /// <summary>Gets the request the window opened over.</summary>
    public ScenarioMergeRequest Request { get; }

    /// <summary>Gets the engine's plan.</summary>
    public ScenarioMergePlan Plan { get; }

    /// <summary>Gets the rows in the engine's order.</summary>
    public IReadOnlyList<ScenarioMergeRowViewModel> Rows { get; }

    /// <summary>Gets the selected aircraft line's item key; null with no aircraft selected.</summary>
    public string? SelectedAircraftId => SelectedAircraftLine?.ItemKey;

    /// <summary>Gets the General tab's sections in view order, each listed whether or not it differs.</summary>
    public IReadOnlyList<ScenarioMergeSectionViewModel> Sections { get; }

    /// <summary>Gets the Aircraft tab's scope: every aircraft row, with the aircraft order row as its line.</summary>
    public ScenarioMergeSectionViewModel AircraftSection { get; }

    /// <summary>Gets the Aircraft tab's lines: mine's aircraft in mine's order, then the aircraft only theirs holds.</summary>
    public IReadOnlyList<ScenarioMergeAircraftLineViewModel> AircraftLines { get; }

    /// <summary>Gets the author's choices keyed by row path.</summary>
    public IReadOnlyDictionary<string, MergeChoice> Choices => _choices;

    /// <summary>Gets a value indicating whether the merge has no base, so every difference is chosen.</summary>
    public bool IsTwoWay => Plan.IsTwoWay;

    /// <summary>Gets the window's title.</summary>
    public string Title => Request.Captions.Title;

    /// <summary>
    /// Gets a value indicating whether Apply is offered: every clash has a choice, no MERGED VALUE or MERGED AIRCRAFT editor is
    /// open and no Apply runs.
    /// </summary>
    public bool CanApply => (OpenClashCount == 0) && !_applying && !Rows.Any(row => row.IsEditing) && (MergedAircraft is null);

    /// <summary>Gets a value indicating whether Show base is offered; there is no base in a two-way merge.</summary>
    public bool CanShowBase => !IsTwoWay;

    /// <summary>Gets the Show base toggle's label.</summary>
    public string ShowBaseLabel => IsTwoWay ? "Show base (no base version)" : "Show base";

    /// <summary>Gets the text naming the base beside the toggle; empty in a two-way merge.</summary>
    public string ShowBaseSourceText => IsTwoWay ? "" : "base: " + Request.Captions.Base;

    /// <summary>Gets the note strip shown over a two-way merge; null in a three-way merge.</summary>
    public string? TwoWayNote => IsTwoWay ? Request.Captions.TwoWayNote : null;

    /// <summary>Creates the window's model, planning the merge of the request's texts.</summary>
    /// <param name="request">The texts, captions and writer.</param>
    /// <returns>The model.</returns>
    /// <exception cref="System.Text.Json.JsonException">A text is malformed or not a JSON object.</exception>
    public static ScenarioMergeViewModel Create(ScenarioMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ScenarioMergeTexts texts = request.Texts;
        return new ScenarioMergeViewModel(request, ScenarioMerge.Plan(texts.Base, texts.Mine, texts.Theirs));
    }

    partial void OnShowBaseChanged(bool value) => Refresh();

    partial void OnOnlyDifferencesChanged(bool value) => Refresh();

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextClash() => MoveTo(_position + 1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousClash() => MoveTo(_position - 1);

    private bool CanGoNext() => _position < (_clashOrder.Count - 1);

    private bool CanGoPrevious() => _position > 0;

    private static int RankOf(string section) => SectionRanks[section];

    private static string Count(int count, string noun, string plural) => $"{count} {(count == 1 ? noun : plural)}";

    private void MoveTo(int position)
    {
        if ((position < 0) || (position >= _clashOrder.Count))
        {
            return;
        }

        _position = position;
        ScenarioMergeRowViewModel row = _clashOrder[position];
        CurrentClash = row;
        CurrentLine = _lineOfRow.GetValueOrDefault(row);
        SelectedTab = (row.Section == AircraftSectionName) ? ScenarioMergeTab.Aircraft : ScenarioMergeTab.General;
        if (CurrentLine is ScenarioMergeAircraftLineViewModel aircraft)
        {
            SelectedAircraftLine = aircraft;
        }

        PositionText = $"{(IsTwoWay ? "difference" : "clash")} {position + 1} of {_clashOrder.Count}";
        NextClashCommand.NotifyCanExecuteChanged();
        PreviousClashCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Keeps one side for a row, an auto row included; on an aircraft's row it drops that aircraft's merged-aircraft replacement.</summary>
    /// <param name="row">One of <see cref="Rows"/>.</param>
    /// <param name="choice"><see cref="MergeChoice.KeepMine"/> or <see cref="MergeChoice.KeepTheirs"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The choice is an edit, which is made through the row's editor.</exception>
    /// <exception cref="InvalidOperationException">The MERGED AIRCRAFT editor is open.</exception>
    public void Choose(ScenarioMergeRowViewModel row, MergeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(choice);
        if (MergedAircraft is not null)
        {
            throw new InvalidOperationException("Close the merged aircraft editor first.");
        }

        if (choice.Kind == MergeChoiceKind.Edit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(choice),
                choice.Kind,
                "Choose keeps mine or theirs; an edited value is kept through its editor."
            );
        }

        _choices[row.Path] = choice;
        DropAircraftEdits([row]);
        Refresh();
    }

    private void ChooseRows(IReadOnlyList<ScenarioMergeRowViewModel> rows, MergeChoice choice)
    {
        if (MergedAircraft is not null)
        {
            return;
        }

        foreach (ScenarioMergeRowViewModel row in rows)
        {
            _choices[row.Path] = choice;
        }

        DropAircraftEdits(rows);
        Refresh();
    }

    private void Refresh()
    {
        ApplyError = (MergedAircraft is null) ? null : _editorRefusal;
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            row.Refresh(_choices.GetValueOrDefault(row.Path), ShowBase, IsReplaced(row));
        }

        List<ScenarioMergeRowViewModel> open = [.. Rows.Where(row => row.IsOpen)];
        OpenClashCount = open.Count;
        AircraftOpenClashCount = open.Count(row => row.Section == AircraftSectionName);
        GeneralOpenClashCount = open.Count - AircraftOpenClashCount;
        SummaryText = Summary(open.Count);
        FooterText = Footer(open.Count);
        foreach (ScenarioMergeSectionViewModel section in Sections)
        {
            section.Refresh(OnlyDifferences);
        }

        AircraftSection.Refresh(OnlyDifferences);
        foreach (ScenarioMergeAircraftLineViewModel line in AircraftLines)
        {
            line.Refresh(OnlyDifferences, _aircraftEdits.ContainsKey(line.ItemKey));
        }
    }

    private string Summary(int open)
    {
        if (IsTwoWay)
        {
            return (open > 0) ? Count(open, "difference", "differences") + " to choose" : $"All differences resolved: {Plan.ClashCount} chosen.";
        }

        return (open > 0)
            ? $"{Plan.AutoMergedCount} auto-merged · {Count(open, "clash", "clashes")} to resolve"
            : $"All differences resolved: {Plan.AutoMergedCount} merged by themselves, {Plan.ClashCount} chosen.";
    }

    private string Footer(int open)
    {
        if (open == 0)
        {
            return "Nothing is saved until you apply.";
        }

        return IsTwoWay
            ? $"Choose a side for {Count(open, "difference", "differences")} to apply."
            : $"Resolve {Count(open, "clash", "clashes")} to apply. Nothing is saved until you apply.";
    }

    private static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static bool IsListKey(string key) => (key == AircraftKey) || Lists.Any(list => list.Key == key);

    private static string? IdOf(JsonObject? item) => ScenarioMergeItemKeys.IdOf(item);

    private static string? TextOf(JsonObject? item, string key) =>
        ((item?[key] is JsonValue value) && value.TryGetValue(out string? text)) ? text : null;

    private static List<JsonObject> Items(JsonObject root, string key) => (root[key] is JsonArray items) ? [.. items.OfType<JsonObject>()] : [];

    private static bool HasDistinctIds(List<JsonObject> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.All(item => (IdOf(item) is string id) && seen.Add(id));
    }

    // A list's items as the engine matches them: by id when every item of both sides has a distinct one, else by position.
    private static List<MergeItemSlot> ItemSlots(MergeSides sides, string key)
    {
        List<JsonObject> mine = Items(sides.Mine, key);
        List<JsonObject> theirs = Items(sides.Theirs, key);
        if (!HasDistinctIds(mine) || !HasDistinctIds(theirs))
        {
            return
            [
                .. Enumerable
                    .Range(0, Math.Max(mine.Count, theirs.Count))
                    .Select(i => new MergeItemSlot(
                        "#" + i.ToString(CultureInfo.InvariantCulture),
                        (i < mine.Count) ? i : -1,
                        (i < theirs.Count) ? i : -1,
                        mine.ElementAtOrDefault(i),
                        theirs.ElementAtOrDefault(i)
                    )),
            ];
        }

        List<string> ids = [.. mine.Select(item => IdOf(item)!)];
        ids.AddRange([.. theirs.Select(item => IdOf(item)!).Where(id => !ids.Contains(id))]);
        return [.. ids.Select(id => MergeItemSlot.ById(id, mine, theirs))];
    }

    // An initialization trigger reads as its time and command, as the editor lists it; any other item as its JSON.
    private static string ItemText(string section, JsonObject? item)
    {
        if (item is null)
        {
            return ScenarioMergeValueText.NotSet;
        }

        if (section != "Initialization triggers")
        {
            return item.ToJsonString();
        }

        int seconds = ((item["timeOffset"] is JsonValue time) && time.TryGetValue(out int offset)) ? offset : 0;
        return $"{ScenarioFieldText.FormatTime(seconds)} {TextOf(item, "command")}".TrimEnd();
    }

    private static string OrderText(JsonObject root, string key, string id)
    {
        JsonObject? item = Items(root, key).FirstOrDefault(candidate => IdOf(candidate) == id);
        return TextOf(item, "aircraftId") ?? TextOf(item, "command") ?? id;
    }

    // The editor's labels for the settings fields, from the engine: each field planned against a scenario without it.
    private static Dictionary<string, string> FieldLabels(MergeSides sides, List<string> keys)
    {
        var fields = new JsonObject();
        foreach (string key in keys)
        {
            fields[key] = (sides.Mine.TryGetPropertyValue(key, out JsonNode? value) ? value : sides.Theirs[key])?.DeepClone();
        }

        return ScenarioMerge.Plan(null, fields.ToJsonString(), "{}").Rows.ToDictionary(row => row.Path, row => row.Label, StringComparer.Ordinal);
    }

    private ScenarioMergeLineViewModel NewLine(ScenarioMergeLineText text, List<ScenarioMergeRowViewModel> rows, ScenarioMergeOrderListing? order)
    {
        var line = new ScenarioMergeLineViewModel(text, rows, order, ChooseRows);
        foreach (ScenarioMergeRowViewModel row in rows)
        {
            _lineOfRow[row] = line;
        }

        return line;
    }

    // A section over its lines; a row of the section no line holds gets a line of its own, so every row has one.
    private ScenarioMergeSectionViewModel Section(string name, List<ScenarioMergeLineViewModel> lines)
    {
        List<ScenarioMergeRowViewModel> rows = [.. Rows.Where(row => row.Section == name)];
        foreach (ScenarioMergeRowViewModel row in rows.Where(row => !_lineOfRow.ContainsKey(row)).ToList())
        {
            lines.Add(NewLine(new(row.Label, row.ItemKey, row.MineText, row.TheirsText), [row], null));
        }

        return new ScenarioMergeSectionViewModel(name, lines, rows, ChooseRows);
    }

    private ScenarioMergeSectionViewModel FieldSection(MergeSides sides)
    {
        List<string> keys = sides.Keys(key => !IsListKey(key));
        Dictionary<string, string> labels = FieldLabels(sides, keys);
        return Section(GeneralSection, [.. keys.Select(key => FieldLine(sides, key, labels["/" + Escape(key)]))]);
    }

    private ScenarioMergeLineViewModel FieldLine(MergeSides sides, string key, string label)
    {
        string path = "/" + Escape(key);
        List<ScenarioMergeRowViewModel> rows =
        [
            .. Rows.Where(row =>
                (row.Section == GeneralSection) && ((row.Path == path) || row.Path.StartsWith(path + "/", StringComparison.Ordinal))
            ),
        ];
        string Text(JsonObject side) => ScenarioMergeValueText.Format(GeneralSection, key, side.ContainsKey(key), side[key]);
        return NewLine(new(label, null, Text(sides.Mine), Text(sides.Theirs)), rows, null);
    }

    // A list with a row of its own (not a list of objects on both sides) has no item lines: that row is its line.
    private ScenarioMergeSectionViewModel ListSection(MergeSides sides, string key, string section)
    {
        List<ScenarioMergeLineViewModel> lines = [];
        if (!Rows.Any(row => row.Path == "/" + key))
        {
            lines.AddRange(ItemSlots(sides, key).Select(slot => ItemLine(section, slot)));
        }

        lines.AddRange(OrderLines(sides, section, key));
        return Section(section, lines);
    }

    private ScenarioMergeLineViewModel ItemLine(string section, MergeItemSlot slot)
    {
        List<ScenarioMergeRowViewModel> rows = [.. Rows.Where(row => (row.Section == section) && (row.ItemKey == slot.Key))];
        return NewLine(new(slot.Key, slot.Key, ItemText(section, slot.Mine), ItemText(section, slot.Theirs)), rows, null);
    }

    private List<ScenarioMergeLineViewModel> OrderLines(MergeSides sides, string section, string key) =>
        [
            .. Rows.Where(row => row.IsOrderRow && (row.Section == section) && (row.ItemKey is null))
                .Select(row =>
                    NewLine(
                        new(row.Label, null, row.MineText, row.TheirsText),
                        [row],
                        ScenarioMergeOrderListing.Of(row.Row, id => OrderText(sides.Mine, key, id), id => OrderText(sides.Theirs, key, id))
                    )
                ),
        ];

    private List<ScenarioMergeAircraftLineViewModel> AircraftLinesOf(MergeSides sides)
    {
        if (Rows.Any(row => row.Path == "/" + AircraftKey))
        {
            return [];
        }

        return [.. ItemSlots(sides, AircraftKey).Select(slot => AircraftLine(sides, slot))];
    }

    private ScenarioMergeAircraftLineViewModel AircraftLine(MergeSides sides, MergeItemSlot slot)
    {
        List<ScenarioMergeRowViewModel> rows = [.. Rows.Where(row => (row.Section == AircraftSectionName) && (row.ItemKey == slot.Key))];
        var cells = new ScenarioMergeAircraftCells(
            slot.Key,
            Cell(sides.MineScenario, slot.MineIndex),
            Cell(sides.TheirsScenario, slot.TheirsIndex),
            slot.Mine is not null,
            slot.Theirs is not null
        );
        var line = new ScenarioMergeAircraftLineViewModel(cells, IsTwoWay, rows, ChooseRows);
        foreach (ScenarioMergeRowViewModel row in rows)
        {
            _lineOfRow[row] = line;
        }

        return line;
    }

    private static ScenarioAircraftRowViewModel? Cell(Scenario? scenario, int index) =>
        ((scenario is not null) && (index >= 0) && (index < scenario.Aircraft.Count)) ? new(scenario.Aircraft[index], index) : null;

    /// <summary>One list item on each side: its key, its index into each side's list (-1 when absent), and each side's item.</summary>
    private sealed record MergeItemSlot(string Key, int MineIndex, int TheirsIndex, JsonObject? Mine, JsonObject? Theirs)
    {
        public static MergeItemSlot ById(string id, List<JsonObject> mine, List<JsonObject> theirs)
        {
            int mineIndex = mine.FindIndex(item => IdOf(item) == id);
            int theirsIndex = theirs.FindIndex(item => IdOf(item) == id);
            return new(id, mineIndex, theirsIndex, mine.ElementAtOrDefault(mineIndex), theirs.ElementAtOrDefault(theirsIndex));
        }
    }

    /// <summary>Mine's and theirs' JSON, and their scenarios when the text reads as one, for the cells the lines show.</summary>
    private sealed record MergeSides(JsonObject Mine, JsonObject Theirs, Scenario? MineScenario, Scenario? TheirsScenario)
    {
        public static MergeSides Read(ScenarioMergeTexts texts) => new(Root(texts.Mine), Root(texts.Theirs), Model(texts.Mine), Model(texts.Theirs));

        // Mine's members in mine's order, then the members only theirs holds.
        public List<string> Keys(Func<string, bool> where)
        {
            List<string> keys = [.. Mine.Select(member => member.Key)];
            keys.AddRange([.. Theirs.Select(member => member.Key).Where(key => !Mine.ContainsKey(key))]);
            return [.. keys.Where(where)];
        }

        // The plan has parsed the text already, so it is a JSON object.
        private static JsonObject Root(string text) => JsonNode.Parse(text, documentOptions: ParseOptions)!.AsObject();

        private static Scenario? Model(string text)
        {
            try
            {
                return ScenarioDocument.Read(text).Scenario;
            }
            catch (JsonException ex)
            {
                Log.LogWarning(ex, "Merge window: a side does not read as a scenario; its aircraft show no grid cells");
                return null;
            }
        }
    }
}
