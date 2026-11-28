using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>One aircraft's cells on the Aircraft tab's two grids.</summary>
/// <param name="ItemKey">The aircraft's key: its id, or <c>#n</c> when the list is matched by position.</param>
/// <param name="Mine">Mine's grid cells; null when mine lacks the aircraft or its text does not read as a scenario.</param>
/// <param name="Theirs">Theirs' grid cells; null when theirs lacks the aircraft or its text does not read as a scenario.</param>
/// <param name="HasMine">Whether mine holds the aircraft.</param>
/// <param name="HasTheirs">Whether theirs holds the aircraft.</param>
public sealed record ScenarioMergeAircraftCells(
    string ItemKey,
    ScenarioAircraftRowViewModel? Mine,
    ScenarioAircraftRowViewModel? Theirs,
    bool HasMine,
    bool HasTheirs
);

/// <summary>
/// One aircraft line of the merge window's Aircraft tab, aligned by id across the panes (mine's aircraft in mine's order,
/// then theirs-only): each side's grid cells or a placeholder, a status, and the aircraft's differing field rows.
/// </summary>
public sealed class ScenarioMergeAircraftLineViewModel : ScenarioMergeScopeViewModel
{
    private bool _isReplaced;

    internal ScenarioMergeAircraftLineViewModel(
        ScenarioMergeAircraftCells cells,
        bool isTwoWay,
        IReadOnlyList<ScenarioMergeRowViewModel> rows,
        Action<IReadOnlyList<ScenarioMergeRowViewModel>, MergeChoice> choose
    )
        : base(rows, choose)
    {
        Cells = cells;
        StatusText = Status(rows, isTwoWay);
    }

    /// <summary>
    /// Gets a value indicating whether the MERGED AIRCRAFT editor's replacement stands for the aircraft, differing rows or
    /// not; Only differences never hides a replaced line.
    /// </summary>
    public bool IsReplaced
    {
        get => _isReplaced;
        private set => SetProperty(ref _isReplaced, value);
    }

    /// <summary>Gets both sides' cells.</summary>
    public ScenarioMergeAircraftCells Cells { get; }

    /// <summary>Gets the aircraft's key, the selection's <see cref="ScenarioMergeViewModel.SelectedAircraftId"/>.</summary>
    public string ItemKey => Cells.ItemKey;

    /// <summary>Gets mine's grid cells; null draws the placeholder.</summary>
    public ScenarioAircraftRowViewModel? Mine => Cells.Mine;

    /// <summary>Gets theirs' grid cells; null draws the placeholder.</summary>
    public ScenarioAircraftRowViewModel? Theirs => Cells.Theirs;

    /// <summary>Gets a value indicating whether mine holds the aircraft.</summary>
    public bool HasMine => Cells.HasMine;

    /// <summary>Gets a value indicating whether theirs holds the aircraft.</summary>
    public bool HasTheirs => Cells.HasTheirs;

    /// <summary>
    /// Gets the line's status: an aircraft one side lacks reads as its item row's note ("only in mine", "removed in theirs",
    /// "added in mine"), one with differing fields "changed", and one identical on both sides "same".
    /// </summary>
    public string StatusText { get; }

    internal void Refresh(bool onlyDifferences, bool replaced)
    {
        IsReplaced = replaced;
        Refresh(onlyDifferences);
    }

    internal override void Refresh(bool onlyDifferences)
    {
        base.Refresh(onlyDifferences);
        IsVisible = IsVisible || IsReplaced;
    }

    private static string Status(IReadOnlyList<ScenarioMergeRowViewModel> rows, bool isTwoWay)
    {
        ScenarioMergeRowViewModel? item = rows.FirstOrDefault(row => row.IsItemRow);
        if (item is null)
        {
            return (rows.Count > 0) ? "changed" : "same";
        }

        string? note = isTwoWay ? (item.Row.InMine ? item.MineNote : item.TheirsNote) : (item.MineNote ?? item.TheirsNote);
        return note ?? "changed";
    }
}
