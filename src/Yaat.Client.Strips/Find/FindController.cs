using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yaat.Client.Find;

/// <summary>
/// Drives the shared in-view Find (Ctrl+F) for one view. Holds the query and visibility the
/// <c>FindBarView</c> binds to, computes matches over a host-supplied snapshot, tracks the
/// current match <b>by reference</b> (so it survives server-driven reorders), toggles the
/// <see cref="IFindableItem"/> highlight flags, and asks the host to scroll the current match
/// into view. UI-agnostic — no Avalonia dependency — so it is unit-testable directly.
/// </summary>
public sealed partial class FindController : ObservableObject
{
    private readonly Func<IReadOnlyList<IFindableItem>> _snapshot;
    private readonly Action<IFindableItem> _scrollTo;

    // Items currently carrying highlight flags. Tracked so a recompute can clear flags on rows
    // that have since left the snapshot (e.g. a vStrips bay switch) — the fresh snapshot alone
    // can't reach them.
    private readonly List<IFindableItem> _flagged = [];

    private List<IFindableItem> _matches = [];
    private IFindableItem? _currentItem;

    /// <summary>Whether the find bar is shown. Setting it recomputes matches (or clears them when hidden).</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>The search text bound to the find bar's input.</summary>
    [ObservableProperty]
    private string _query = "";

    public FindController(Func<IReadOnlyList<IFindableItem>> snapshot, Action<IFindableItem> scrollTo)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _scrollTo = scrollTo ?? throw new ArgumentNullException(nameof(scrollTo));
    }

    /// <summary>Bar caption: empty with no query, "No matches", or "{ordinal}/{count}".</summary>
    public string MatchSummary
    {
        get
        {
            if (!IsVisible || string.IsNullOrWhiteSpace(Query))
            {
                return "";
            }
            if (_matches.Count == 0)
            {
                return "No matches";
            }
            var ordinal = _currentItem is null ? 0 : _matches.IndexOf(_currentItem) + 1;
            return $"{ordinal}/{_matches.Count}";
        }
    }

    /// <summary>Shows the find bar and (re)runs the current query against the live snapshot.</summary>
    public void Open() => IsVisible = true;

    /// <summary>Advances to the next match, wrapping around; opens on the first match if none is current.</summary>
    public void Next() => Move(+1);

    /// <summary>Steps to the previous match, wrapping around.</summary>
    public void Previous() => Move(-1);

    /// <summary>Hides the find bar and clears every highlight.</summary>
    public void Close() => IsVisible = false;

    /// <summary>
    /// Re-runs the query against the live snapshot after the underlying data changed, keeping the
    /// current match if it still matches, else falling to the first match.
    /// </summary>
    public void Refresh()
    {
        RecomputeMatches();
        if (!IsVisible)
        {
            _currentItem = null;
            OnPropertyChanged(nameof(MatchSummary));
            return;
        }
        var idx = _currentItem is null ? -1 : _matches.IndexOf(_currentItem);
        if (idx < 0)
        {
            idx = _matches.Count > 0 ? 0 : -1;
        }
        SelectByIndex(idx);
    }

    [RelayCommand]
    private void NextMatch() => Next();

    [RelayCommand]
    private void PreviousMatch() => Previous();

    [RelayCommand]
    private void CloseFind() => Close();

    partial void OnIsVisibleChanged(bool value)
    {
        if (value)
        {
            RecomputeMatches();
            var idx = _currentItem is not null ? _matches.IndexOf(_currentItem) : (_matches.Count > 0 ? 0 : -1);
            SelectByIndex(idx);
        }
        else
        {
            // Hiding clears every highlight (RecomputeMatches short-circuits to an empty set).
            RecomputeMatches();
            _currentItem = null;
            OnPropertyChanged(nameof(MatchSummary));
        }
    }

    partial void OnQueryChanged(string value)
    {
        RecomputeMatches();
        SelectByIndex(_matches.Count > 0 ? 0 : -1);
    }

    private void Move(int delta)
    {
        if (!IsVisible)
        {
            return;
        }
        RecomputeMatches();
        if (_matches.Count == 0)
        {
            SelectByIndex(-1);
            return;
        }
        var currentIdx = _currentItem is null ? -1 : _matches.IndexOf(_currentItem);
        int nextIdx;
        if (currentIdx < 0)
        {
            nextIdx = delta > 0 ? 0 : _matches.Count - 1;
        }
        else
        {
            var n = _matches.Count;
            nextIdx = ((currentIdx + delta) % n + n) % n;
        }
        SelectByIndex(nextIdx);
    }

    private void RecomputeMatches()
    {
        foreach (var item in _flagged)
        {
            item.IsFindMatch = false;
            item.IsCurrentFindMatch = false;
        }
        _flagged.Clear();

        if (!IsVisible)
        {
            _matches = [];
            return;
        }

        _matches = FindMatcher.ComputeMatches(_snapshot(), Query);
        foreach (var match in _matches)
        {
            match.IsFindMatch = true;
            _flagged.Add(match);
        }
    }

    private void SelectByIndex(int index)
    {
        if (_currentItem is not null)
        {
            _currentItem.IsCurrentFindMatch = false;
        }
        if (index >= 0 && index < _matches.Count)
        {
            _currentItem = _matches[index];
            _currentItem.IsCurrentFindMatch = true;
            _scrollTo(_currentItem);
        }
        else
        {
            _currentItem = null;
        }
        OnPropertyChanged(nameof(MatchSummary));
    }
}
