using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

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
/// The merge window over a planned merge: one row per difference, the author's choices keyed by row path, the counts and
/// texts that gate Apply, Previous/Next over every clash in the editor's order, and the base strips.
/// </summary>
public sealed partial class ScenarioMergeViewModel : ObservableObject
{
    private const string AircraftSection = "Aircraft";

    private static readonly Dictionary<string, int> SectionRanks = new(StringComparer.Ordinal)
    {
        ["General"] = 0,
        ["ATC positions"] = 1,
        ["Initialization triggers"] = 2,
        ["Generators"] = 3,
        ["VFR arrival generators"] = 4,
        ["Overflight generators"] = 5,
        ["Strip bays"] = 6,
        [AircraftSection] = 7,
    };

    private readonly Dictionary<string, MergeChoice> _choices = new(StringComparer.Ordinal);

    private readonly List<ScenarioMergeRowViewModel> _clashOrder;

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

    /// <summary>The tab shown; Previous/Next moves it to the selected clash's tab.</summary>
    [ObservableProperty]
    private ScenarioMergeTab _selectedTab;

    /// <summary>The aircraft selected on the Aircraft tab, by its row's item key.</summary>
    [ObservableProperty]
    private string? _selectedAircraftId;

    /// <summary>Whether base strips show under auto-merged rows too; clashes always show theirs in a three-way merge.</summary>
    [ObservableProperty]
    private bool _showBase;

    private ScenarioMergeViewModel(ScenarioMergeRequest request, ScenarioMergePlan plan)
    {
        Request = request;
        Plan = plan;
        Rows = [.. plan.Rows.Select((row, index) => new ScenarioMergeRowViewModel(row, plan.IsTwoWay, index, Choose))];
        _clashOrder = [.. Rows.Where(row => row.IsClash).OrderBy(row => RankOf(row.Section)).ThenBy(row => row.Index)];
        Refresh();
    }

    /// <summary>Gets the request the window opened over.</summary>
    public ScenarioMergeRequest Request { get; }

    /// <summary>Gets the engine's plan.</summary>
    public ScenarioMergePlan Plan { get; }

    /// <summary>Gets the rows in the engine's order.</summary>
    public IReadOnlyList<ScenarioMergeRowViewModel> Rows { get; }

    /// <summary>Gets the author's choices keyed by row path.</summary>
    public IReadOnlyDictionary<string, MergeChoice> Choices => _choices;

    /// <summary>Gets a value indicating whether the merge has no base, so every difference is chosen.</summary>
    public bool IsTwoWay => Plan.IsTwoWay;

    /// <summary>Gets the window's title.</summary>
    public string Title => Request.Captions.Title;

    /// <summary>Gets a value indicating whether Apply is offered: every clash has a choice.</summary>
    public bool CanApply => OpenClashCount == 0;

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
        bool onAircraft = row.Section == AircraftSection;
        SelectedTab = onAircraft ? ScenarioMergeTab.Aircraft : ScenarioMergeTab.General;
        if (onAircraft)
        {
            SelectedAircraftId = row.ItemKey;
        }

        PositionText = $"{(IsTwoWay ? "difference" : "clash")} {position + 1} of {_clashOrder.Count}";
        NextClashCommand.NotifyCanExecuteChanged();
        PreviousClashCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Keeps one side for a row, an auto row included.</summary>
    /// <param name="row">One of <see cref="Rows"/>.</param>
    /// <param name="choice"><see cref="MergeChoice.KeepMine"/> or <see cref="MergeChoice.KeepTheirs"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The choice is an edit, which is made through the row's editor.</exception>
    public void Choose(ScenarioMergeRowViewModel row, MergeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.Kind == MergeChoiceKind.Edit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(choice),
                choice.Kind,
                "Choose keeps mine or theirs; an edited value is kept through its editor."
            );
        }

        _choices[row.Path] = choice;
        Refresh();
    }

    private void Refresh()
    {
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            row.Refresh(_choices.GetValueOrDefault(row.Path), ShowBase);
        }

        List<ScenarioMergeRowViewModel> open = [.. Rows.Where(row => row.IsOpen)];
        OpenClashCount = open.Count;
        AircraftOpenClashCount = open.Count(row => row.Section == AircraftSection);
        GeneralOpenClashCount = open.Count - AircraftOpenClashCount;
        SummaryText = Summary(open.Count);
        FooterText = Footer(open.Count);
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
}
