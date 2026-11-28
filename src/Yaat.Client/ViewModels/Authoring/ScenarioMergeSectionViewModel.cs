using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// Rows the merge window keeps one side for at once (a section, a line, an aircraft): its buttons cover every differing row
/// in scope, auto rows included, and show pressed while every one of them takes that side.
/// </summary>
public abstract partial class ScenarioMergeScopeViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<ScenarioMergeRowViewModel>, MergeChoice> _choose;

    // Set while the MERGED AIRCRAFT editor is open: the scope's buttons do nothing.
    private bool _locked;

    /// <summary>Whether every row in scope takes mine; false when the scope has no row.</summary>
    [ObservableProperty]
    private bool _isMinePressed;

    /// <summary>Whether every row in scope takes theirs; false when the scope has no row.</summary>
    [ObservableProperty]
    private bool _isTheirsPressed;

    /// <summary>Whether the scope is shown: always, or with Only differences on, only while it holds a differing row.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    private protected ScenarioMergeScopeViewModel(
        IReadOnlyList<ScenarioMergeRowViewModel> rows,
        Action<IReadOnlyList<ScenarioMergeRowViewModel>, MergeChoice> choose
    )
    {
        Rows = rows;
        _choose = choose;
    }

    /// <summary>Gets the differing rows in scope, in the engine's order.</summary>
    public IReadOnlyList<ScenarioMergeRowViewModel> Rows { get; }

    /// <summary>Gets a value indicating whether mine and theirs differ anywhere in scope.</summary>
    public bool IsDifferent => Rows.Count > 0;

    internal virtual void Refresh(bool onlyDifferences)
    {
        IsMinePressed = IsDifferent && Rows.All(row => row.IsMinePressed);
        IsTheirsPressed = IsDifferent && Rows.All(row => row.IsTheirsPressed);
        IsVisible = !onlyDifferences || IsDifferent;
    }

    // The scope's buttons are offered only while the MERGED AIRCRAFT editor is closed.
    internal virtual void Lock(bool locked)
    {
        if (_locked == locked)
        {
            return;
        }

        _locked = locked;
        KeepMineCommand.NotifyCanExecuteChanged();
        KeepTheirsCommand.NotifyCanExecuteChanged();
    }

    private bool CanChoose() => IsDifferent && !_locked;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void KeepMine()
    {
        if (CanChoose())
        {
            _choose(Rows, MergeChoice.KeepMine);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void KeepTheirs()
    {
        if (CanChoose())
        {
            _choose(Rows, MergeChoice.KeepTheirs);
        }
    }
}

/// <summary>One General-tab section of the merge window: its lines in view order and the buttons over all its rows.</summary>
public sealed class ScenarioMergeSectionViewModel : ScenarioMergeScopeViewModel
{
    internal ScenarioMergeSectionViewModel(
        string name,
        IReadOnlyList<ScenarioMergeLineViewModel> lines,
        IReadOnlyList<ScenarioMergeRowViewModel> rows,
        Action<IReadOnlyList<ScenarioMergeRowViewModel>, MergeChoice> choose
    )
        : base(rows, choose)
    {
        Name = name;
        Lines = lines;
    }

    /// <summary>Gets the section's name, the rows' <see cref="MergeRow.Section"/>.</summary>
    public string Name { get; }

    /// <summary>Gets the section's heading: the General section reads "Settings", as in the editor.</summary>
    public string Title => (Name == ScenarioMergeViewModel.GeneralSection) ? "Settings" : Name;

    /// <summary>Gets the section's lines: a field or list item each, identical ones included, then its order row.</summary>
    public IReadOnlyList<ScenarioMergeLineViewModel> Lines { get; }

    internal override void Refresh(bool onlyDifferences)
    {
        base.Refresh(onlyDifferences);
        foreach (ScenarioMergeLineViewModel line in Lines)
        {
            line.Refresh(onlyDifferences);
        }
    }

    internal override void Lock(bool locked)
    {
        base.Lock(locked);
        foreach (ScenarioMergeLineViewModel line in Lines)
        {
            line.Lock(locked);
        }
    }
}

/// <summary>The texts of one merge line.</summary>
/// <param name="Label">The field's label, a list item's key, or an order row's "{list} order".</param>
/// <param name="ItemKey">The list item's key (its id, or <c>#n</c> by position); null for a field or an order row.</param>
/// <param name="MineText">Mine's cell.</param>
/// <param name="TheirsText">Theirs' cell.</param>
public sealed record ScenarioMergeLineText(string Label, string? ItemKey, string MineText, string TheirsText);

/// <summary>
/// One line of a General-tab section, drawn across both panes: a settings field, a list item with its differing rows under
/// it, or a list's order row with each side's items listed. An identical line has no rows.
/// </summary>
public sealed class ScenarioMergeLineViewModel : ScenarioMergeScopeViewModel
{
    private readonly ScenarioMergeLineText _text;

    internal ScenarioMergeLineViewModel(
        ScenarioMergeLineText text,
        IReadOnlyList<ScenarioMergeRowViewModel> rows,
        ScenarioMergeOrderListing? order,
        Action<IReadOnlyList<ScenarioMergeRowViewModel>, MergeChoice> choose
    )
        : base(rows, choose)
    {
        _text = text;
        Order = order;
    }

    /// <summary>Gets the line's label.</summary>
    public string Label => _text.Label;

    /// <summary>Gets the list item's key; null for a field or an order row.</summary>
    public string? ItemKey => _text.ItemKey;

    /// <summary>Gets mine's cell.</summary>
    public string MineText => _text.MineText;

    /// <summary>Gets theirs' cell.</summary>
    public string TheirsText => _text.TheirsText;

    /// <summary>Gets each side's items in that side's order for an order row; null on every other line.</summary>
    public ScenarioMergeOrderListing? Order { get; }
}

/// <summary>One item in an order row's pane.</summary>
/// <param name="Position">The item's 1-based place in this side's order.</param>
/// <param name="Text">The item as the pane names it: an aircraft's callsign, a command, else its id.</param>
/// <param name="MovedFrom">
/// The 1-based place it moved from (in the base, or in the other side in a two-way merge); null when it did not move.
/// </param>
public sealed record ScenarioMergeOrderItem(int Position, string Text, int? MovedFrom);

/// <summary>An order row's panes: each side's shared items in that side's order, the moved ones marked.</summary>
/// <param name="Mine">Mine's items.</param>
/// <param name="Theirs">Theirs' items.</param>
public sealed record ScenarioMergeOrderListing(IReadOnlyList<ScenarioMergeOrderItem> Mine, IReadOnlyList<ScenarioMergeOrderItem> Theirs)
{
    /// <summary>
    /// The listing of an order row: an item moved when it falls outside the longest run of items that side keeps in the
    /// base's order (the other side's in a two-way merge).
    /// </summary>
    /// <param name="row">The order row.</param>
    /// <param name="mineText">Names an item of mine by its id.</param>
    /// <param name="theirsText">Names an item of theirs by its id.</param>
    /// <returns>The listing.</returns>
    internal static ScenarioMergeOrderListing Of(MergeRow row, Func<string, string> mineText, Func<string, string> theirsText)
    {
        List<string> mine = Ids(row.Mine);
        List<string> theirs = Ids(row.Theirs);
        if (row.InBase)
        {
            List<string> baseIds = Ids(row.Base);
            return new(Items(mine, baseIds, Kept(mine, baseIds), mineText), Items(theirs, baseIds, Kept(theirs, baseIds), theirsText));
        }

        // Two-way, each pane's reference is the other pane: one kept set marks the same items moved on both.
        HashSet<string> kept = Kept(mine, theirs);
        return new(Items(mine, theirs, kept, mineText), Items(theirs, mine, kept, theirsText));
    }

    private static List<string> Ids(JsonNode? ids) => (ids is JsonArray items) ? [.. items.Select(id => id!.GetValue<string>())] : [];

    private static List<ScenarioMergeOrderItem> Items(List<string> side, List<string> reference, HashSet<string> kept, Func<string, string> text) =>
        [.. side.Select((id, i) => new ScenarioMergeOrderItem(i + 1, text(id), kept.Contains(id) ? null : PlaceIn(reference, id)))];

    private static int? PlaceIn(List<string> reference, string id)
    {
        int index = reference.IndexOf(id);
        return (index < 0) ? null : index + 1;
    }

    // The ids of a longest common subsequence of the two orders.
    private static HashSet<string> Kept(List<string> side, List<string> reference)
    {
        int[][] lengths = CommonLengths(side, reference);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        int j = 0;
        while ((i < side.Count) && (j < reference.Count))
        {
            if (string.Equals(side[i], reference[j], StringComparison.Ordinal))
            {
                kept.Add(side[i]);
                i++;
                j++;
            }
            else if (lengths[i + 1][j] >= lengths[i][j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return kept;
    }

    // lengths[i][j]: the longest common subsequence of side[i..] and reference[j..].
    private static int[][] CommonLengths(List<string> side, List<string> reference)
    {
        int[][] lengths = [.. Enumerable.Range(0, side.Count + 1).Select(_ => new int[reference.Count + 1])];
        for (int i = side.Count - 1; i >= 0; i--)
        {
            for (int j = reference.Count - 1; j >= 0; j--)
            {
                lengths[i][j] = string.Equals(side[i], reference[j], StringComparison.Ordinal)
                    ? lengths[i + 1][j + 1] + 1
                    : Math.Max(lengths[i + 1][j], lengths[i][j + 1]);
            }
        }

        return lengths;
    }
}
