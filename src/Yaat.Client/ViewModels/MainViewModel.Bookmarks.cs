using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Client.ViewModels;

/// <summary>
/// A request for the view to prompt the user for a bookmark name. <see cref="OnSave"/> commits the
/// typed name; <see cref="OnCancel"/> runs when the prompt is dismissed without saving (for a new
/// bookmark that means "create it unnamed", for a rename it is a no-op).
/// </summary>
public sealed record BookmarkNamePrompt(string? InitialName, Action<string?> OnSave, Action OnCancel);

public partial class MainViewModel
{
    private const int MaxBookmarks = 500;

    /// <summary>
    /// Client mirror of the room's shared timeline bookmarks (<see cref="ServerConnection.BookmarksChanged"/>
    /// and the <c>RoomStateDto.Bookmarks</c> join seed), kept sorted by
    /// <see cref="TimelineBookmarkVm.TimeSeconds"/>. Server-authoritative: add/rename/delete go out as hub
    /// RPCs and the collection is only ever rebuilt from a broadcast, so no local echo guard is needed.
    /// </summary>
    public ObservableCollection<TimelineBookmarkVm> Bookmarks { get; } = [];

    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>
    /// Raised when the view should prompt the user for a bookmark name (on deliberate Add, or Rename).
    /// Quick-add does not raise this.
    /// </summary>
    public event Action<BookmarkNamePrompt>? BookmarkNamePromptRequested;

    [RelayCommand]
    private void AddBookmark()
    {
        var timeSeconds = ScenarioElapsedSeconds;
        BookmarkNamePromptRequested?.Invoke(
            new BookmarkNamePrompt(
                InitialName: null,
                OnSave: name => _ = SendAddBookmarkAsync(timeSeconds, name),
                OnCancel: () => _ = SendAddBookmarkAsync(timeSeconds, null)
            )
        );
    }

    [RelayCommand]
    private Task QuickAddBookmark() => SendAddBookmarkAsync(ScenarioElapsedSeconds, null);

    /// <summary>Deadband around the current position so a step never re-selects the bookmark we sit on.</summary>
    private const double BookmarkStepEpsilonSeconds = 0.5;

    [RelayCommand]
    private Task NextBookmark() => SeekNextBookmarkAsync();

    [RelayCommand]
    private Task PrevBookmark() => SeekPrevBookmarkAsync();

    /// <summary>The first bookmark after the current position, or null when there is none.</summary>
    public TimelineBookmarkVm? FindNextBookmark() =>
        Bookmarks.FirstOrDefault(b => b.TimeSeconds > ScenarioElapsedSeconds + BookmarkStepEpsilonSeconds);

    /// <summary>The last bookmark before the current position, or null when there is none.</summary>
    public TimelineBookmarkVm? FindPrevBookmark() =>
        Bookmarks.LastOrDefault(b => b.TimeSeconds < ScenarioElapsedSeconds - BookmarkStepEpsilonSeconds);

    /// <summary>Seeks to the first bookmark after the current position; no-op when there is none.</summary>
    public async Task SeekNextBookmarkAsync()
    {
        if (FindNextBookmark() is { } next)
        {
            await RewindToSeconds(next.TimeSeconds);
        }
    }

    /// <summary>Seeks to the last bookmark before the current position; no-op when there is none.</summary>
    public async Task SeekPrevBookmarkAsync()
    {
        if (FindPrevBookmark() is { } prev)
        {
            await RewindToSeconds(prev.TimeSeconds);
        }
    }

    /// <summary>
    /// Handles the client-side half of the <c>BM</c> verb — the read-only <c>LIST</c> query and the
    /// <c>GO</c>/<c>NEXT</c>/<c>PREV</c> seeks, which need <see cref="RewindToSeconds"/> and so have no
    /// server-side form. Returns false for the mutating actions, which the caller forwards to the server.
    /// </summary>
    private async Task<bool> TryHandleBookmarkLocallyAsync(BookmarkCommand bookmark)
    {
        switch (bookmark.Action)
        {
            case BookmarkAction.List:
                ListBookmarks();
                return true;
            case BookmarkAction.Next:
                await SeekNextBookmarkAsync();
                return true;
            case BookmarkAction.Prev:
                await SeekPrevBookmarkAsync();
                return true;
            case BookmarkAction.Goto:
                var target = Bookmarks.FirstOrDefault(b => b.Id == bookmark.Id);
                if (target is null)
                {
                    StatusText = $"No bookmark {bookmark.Id}";
                    return true;
                }

                await RewindToSeconds(target.TimeSeconds);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Prints the bookmark list into this client's terminal only. It is a query over the local mirror,
    /// so broadcasting it would put one RPO's lookup on everyone else's terminal.
    /// </summary>
    private void ListBookmarks()
    {
        if (Bookmarks.Count == 0)
        {
            AddSystemEntry("No bookmarks");
            return;
        }

        foreach (var b in Bookmarks)
        {
            var name = string.IsNullOrWhiteSpace(b.Name) ? "(unnamed)" : b.Name;
            var creator = string.IsNullOrWhiteSpace(b.CreatorInitials) ? "" : $" — {b.CreatorInitials}";
            AddSystemEntry($"{b.Id}  {b.TimeText}  {name}{creator}");
        }
    }

    /// <summary>Snapshot the current bookmarks for embedding into a recording archive on save.</summary>
    public IReadOnlyList<TimelineBookmark> SnapshotBookmarks()
    {
        return [.. Bookmarks.Select(b => new TimelineBookmark(b.Id, b.TimeSeconds, b.Name, b.CreatorInitials))];
    }

    private void OnBookmarksChanged(BookmarksChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyBookmarks(dto.Bookmarks));

    /// <summary>Replace the bookmark list from a server broadcast or the join-time room-state seed.</summary>
    public void ApplyBookmarks(List<TimelineBookmarkDto>? bookmarks)
    {
        Bookmarks.Clear();
        if (bookmarks is not null)
        {
            foreach (var b in bookmarks.OrderBy(b => b.TimeSeconds).Take(MaxBookmarks))
            {
                Bookmarks.Add(NewBookmarkVm(b.Id, b.TimeSeconds, b.Name, b.CreatorInitials));
            }
        }
        OnPropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>Clear all bookmarks locally at a session boundary (scenario load/unload, room join).</summary>
    public void ClearBookmarks()
    {
        if (Bookmarks.Count == 0)
        {
            return;
        }
        Bookmarks.Clear();
        OnPropertyChanged(nameof(HasBookmarks));
    }

    private async Task SendAddBookmarkAsync(double timeSeconds, string? name)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var result = await _connection.AddBookmarkAsync(timeSeconds, name, _preferences.UserInitials);
            StatusText = result.Success ? $"Bookmark added at {FormatTime(timeSeconds)}" : (result.Message ?? "Add bookmark failed");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Add bookmark failed");
            StatusText = $"Add bookmark error: {ex.Message}";
        }
    }

    private async Task SendRenameBookmarkAsync(string id, string? name)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            await _connection.RenameBookmarkAsync(id, name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rename bookmark failed");
            StatusText = $"Rename bookmark error: {ex.Message}";
        }
    }

    private async Task SendDeleteBookmarkAsync(string id)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var result = await _connection.DeleteBookmarkAsync(id);
            if (result.Success)
            {
                StatusText = "Bookmark deleted";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete bookmark failed");
            StatusText = $"Delete bookmark error: {ex.Message}";
        }
    }

    private TimelineBookmarkVm NewBookmarkVm(string id, double timeSeconds, string? name, string? creatorInitials)
    {
        return new TimelineBookmarkVm
        {
            Id = id,
            TimeSeconds = timeSeconds,
            Name = name,
            CreatorInitials = creatorInitials,
            RenameRequested = RequestRenameBookmark,
            DeleteRequested = bm => _ = SendDeleteBookmarkAsync(bm.Id),
            JumpRequested = bm => RewindToSeconds(bm.TimeSeconds),
        };
    }

    private void RequestRenameBookmark(TimelineBookmarkVm bookmark)
    {
        BookmarkNamePromptRequested?.Invoke(
            new BookmarkNamePrompt(
                InitialName: bookmark.Name,
                OnSave: name => _ = SendRenameBookmarkAsync(bookmark.Id, name),
                OnCancel: static () => { }
            )
        );
    }
}
