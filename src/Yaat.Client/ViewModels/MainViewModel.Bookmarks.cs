using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Simulation;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    private const int MaxBookmarks = 500;
    private int _bookmarkCounter;

    /// <summary>
    /// User-authored timeline bookmarks, kept sorted by <see cref="TimelineBookmarkVm.TimeSeconds"/>.
    /// Held in memory during a session and embedded into the recording archive on save (see
    /// <see cref="SnapshotBookmarks"/> / <see cref="SetBookmarks"/>). Never touched by the 5 s
    /// Finding/Command marker poll, so it survives those rebuilds.
    /// </summary>
    public ObservableCollection<TimelineBookmarkVm> Bookmarks { get; } = [];

    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>
    /// Raised when the view should prompt the user for a bookmark name (on deliberate Add, or
    /// Rename). The argument is the bookmark to name; the view writes back to its
    /// <see cref="TimelineBookmarkVm.Name"/>. Quick-add does not raise this.
    /// </summary>
    public event Action<TimelineBookmarkVm>? BookmarkNamePromptRequested;

    [RelayCommand]
    private void AddBookmark()
    {
        var bookmark = CreateBookmark(ScenarioElapsedSeconds, name: null);
        if (bookmark is not null)
        {
            BookmarkNamePromptRequested?.Invoke(bookmark);
        }
    }

    [RelayCommand]
    private void QuickAddBookmark()
    {
        CreateBookmark(ScenarioElapsedSeconds, name: null);
    }

    [RelayCommand]
    private async Task NextBookmark()
    {
        const double epsilon = 0.5;
        var next = Bookmarks.FirstOrDefault(b => b.TimeSeconds > ScenarioElapsedSeconds + epsilon);
        if (next is not null)
        {
            await RewindToSeconds(next.TimeSeconds);
        }
    }

    [RelayCommand]
    private async Task PrevBookmark()
    {
        const double epsilon = 0.5;
        var prev = Bookmarks.LastOrDefault(b => b.TimeSeconds < ScenarioElapsedSeconds - epsilon);
        if (prev is not null)
        {
            await RewindToSeconds(prev.TimeSeconds);
        }
    }

    /// <summary>Snapshot the current bookmarks for embedding into a recording archive on save.</summary>
    public IReadOnlyList<TimelineBookmark> SnapshotBookmarks()
    {
        return [.. Bookmarks.Select(b => new TimelineBookmark(b.Id, b.TimeSeconds, b.Name))];
    }

    /// <summary>Replace the bookmark list from a loaded recording. Sorts and applies the cap.</summary>
    public void SetBookmarks(IReadOnlyList<TimelineBookmark> bookmarks)
    {
        ClearBookmarks();
        foreach (var b in bookmarks)
        {
            if (Bookmarks.Count >= MaxBookmarks)
            {
                break;
            }
            InsertSorted(NewBookmarkVm(b.Id, b.TimeSeconds, b.Name));
        }
        _bookmarkCounter += bookmarks.Count;
    }

    /// <summary>Clear all bookmarks at a session boundary (scenario load/unload, recording load).</summary>
    public void ClearBookmarks()
    {
        if (Bookmarks.Count == 0)
        {
            return;
        }
        Bookmarks.Clear();
        OnPropertyChanged(nameof(HasBookmarks));
    }

    private TimelineBookmarkVm? CreateBookmark(double timeSeconds, string? name)
    {
        if (Bookmarks.Count >= MaxBookmarks)
        {
            StatusText = $"Bookmark limit reached ({MaxBookmarks})";
            return null;
        }

        var id = string.Create(CultureInfo.InvariantCulture, $"bm-{timeSeconds:0.000}-{_bookmarkCounter++}");
        var bookmark = NewBookmarkVm(id, timeSeconds, name);
        InsertSorted(bookmark);
        StatusText = $"Bookmark added at {FormatTime(timeSeconds)}";
        return bookmark;
    }

    private TimelineBookmarkVm NewBookmarkVm(string id, double timeSeconds, string? name)
    {
        return new TimelineBookmarkVm
        {
            Id = id,
            TimeSeconds = timeSeconds,
            Name = name,
            RenameRequested = bm => BookmarkNamePromptRequested?.Invoke(bm),
            DeleteRequested = RemoveBookmark,
            JumpRequested = bm => RewindToSeconds(bm.TimeSeconds),
        };
    }

    private void RemoveBookmark(TimelineBookmarkVm bookmark)
    {
        if (Bookmarks.Remove(bookmark))
        {
            OnPropertyChanged(nameof(HasBookmarks));
            StatusText = "Bookmark deleted";
        }
    }

    private void InsertSorted(TimelineBookmarkVm bookmark)
    {
        int index = 0;
        while (index < Bookmarks.Count && Bookmarks[index].TimeSeconds <= bookmark.TimeSeconds)
        {
            index++;
        }
        Bookmarks.Insert(index, bookmark);
        OnPropertyChanged(nameof(HasBookmarks));
    }
}
