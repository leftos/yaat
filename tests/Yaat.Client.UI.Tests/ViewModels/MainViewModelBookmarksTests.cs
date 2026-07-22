using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Bookmarks are server-authoritative shared room state (GitHub issue #288). The client is a mirror:
/// add / rename / delete go out as hub RPCs and the collection is only ever rebuilt from a broadcast
/// (<see cref="MainViewModel.ApplyBookmarks"/>) or the join-time room-state seed. These tests cover the
/// mirror + name-prompt behavior; the RPC/broadcast round-trip is covered server-side.
/// </summary>
public class MainViewModelBookmarksTests
{
    private static TimelineBookmarkDto Bm(string id, double time, string? name, string? creator = null) => new(id, time, name, creator);

    [AvaloniaFact]
    public void ApplyBookmarks_SortsByTime()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyBookmarks([Bm("c", 30, "C"), Bm("a", 10, null), Bm("b", 20, "B")]);

        Assert.Equal([10, 20, 30], vm.Bookmarks.Select(b => b.TimeSeconds));
        Assert.True(vm.HasBookmarks);
    }

    [AvaloniaFact]
    public void ApplyBookmarks_SurfacesCreatorInitials()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyBookmarks([Bm("a", 5, "First", "JD")]);

        var bookmark = Assert.Single(vm.Bookmarks);
        Assert.Equal("JD", bookmark.CreatorInitials);
        Assert.Contains("JD", bookmark.ListLabel);
        Assert.Contains("JD", bookmark.ToolTipText);
    }

    [AvaloniaFact]
    public void ApplyBookmarks_EnforcesCap()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var many = Enumerable.Range(0, 600).Select(i => Bm($"bm-{i}", i, null)).ToList();

        vm.ApplyBookmarks(many);

        Assert.Equal(500, vm.Bookmarks.Count);
    }

    [AvaloniaFact]
    public void ApplyBookmarks_ThenSnapshotRoundTrips()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyBookmarks([Bm("c", 30, "C", "AB"), Bm("a", 10, null), Bm("b", 20, "B")]);

        var snapshot = vm.SnapshotBookmarks();
        Assert.Equal([10, 20, 30], snapshot.Select(b => b.TimeSeconds));
        Assert.Null(snapshot[0].Name);
        Assert.Equal("B", snapshot[1].Name);
        Assert.Equal("AB", snapshot[2].CreatorInitials);
    }

    [AvaloniaFact]
    public void ApplyBookmarks_Null_ClearsCollection()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("a", 5, "First")]);

        vm.ApplyBookmarks(null);

        Assert.Empty(vm.Bookmarks);
        Assert.False(vm.HasBookmarks);
    }

    [AvaloniaFact]
    public void ClearBookmarks_EmptiesCollection()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("a", 5, "First")]);

        vm.ClearBookmarks();

        Assert.Empty(vm.Bookmarks);
        Assert.False(vm.HasBookmarks);
    }

    [AvaloniaFact]
    public void AddBookmark_RaisesNamePromptForNewBookmark()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ScenarioElapsedSeconds = 5;

        BookmarkNamePrompt? prompted = null;
        vm.BookmarkNamePromptRequested += p => prompted = p;

        vm.AddBookmarkCommand.Execute(null);

        Assert.NotNull(prompted);
        Assert.Null(prompted!.InitialName);
    }

    [AvaloniaFact]
    public void QuickAdd_DoesNotRaiseNamePrompt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        bool raised = false;
        vm.BookmarkNamePromptRequested += _ => raised = true;

        vm.QuickAddBookmarkCommand.Execute(null);

        Assert.False(raised);
    }

    [AvaloniaFact]
    public async Task BmList_PrintsEachBookmarkLocallyWithItsId()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("bm-1", 10, null), Bm("bm-2", 20, "Conflict", "JD")]);
        vm.TerminalEntries.Clear();

        await vm.HandleBookmarkGlobalCommand("LIST");

        Assert.Equal(2, vm.TerminalEntries.Count);
        Assert.Contains("bm-1", vm.TerminalEntries[0].Message);
        Assert.Contains("(unnamed)", vm.TerminalEntries[0].Message);
        Assert.Contains("bm-2", vm.TerminalEntries[1].Message);
        Assert.Contains("Conflict", vm.TerminalEntries[1].Message);
        Assert.Contains("JD", vm.TerminalEntries[1].Message);
    }

    [AvaloniaFact]
    public async Task BmList_WithNoBookmarks_SaysSo()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.TerminalEntries.Clear();

        await vm.HandleBookmarkGlobalCommand("LIST");

        Assert.Equal("No bookmarks", Assert.Single(vm.TerminalEntries).Message);
    }

    [AvaloniaFact]
    public async Task BmGoto_UnknownId_ReportsItWithoutSeeking()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("bm-1", 10, null)]);

        await vm.HandleBookmarkGlobalCommand("GO 9");

        Assert.Equal("No bookmark bm-9", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task BmMalformed_ReportsTheParseFailure()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        await vm.HandleBookmarkGlobalCommand("DEL xyz");

        Assert.Contains("BM DEL requires a bookmark id or ALL", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task BmNext_WithNothingAhead_IsANoOp()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("bm-1", 10, null)]);
        vm.ScenarioElapsedSeconds = 30;
        vm.StatusText = "unchanged";

        await vm.HandleBookmarkGlobalCommand("NEXT");

        Assert.Equal("unchanged", vm.StatusText);
    }

    [AvaloniaFact]
    public void FindNextAndPrev_SkipTheBookmarkUnderTheCursor()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("bm-1", 10, null), Bm("bm-2", 20, null), Bm("bm-3", 30, null)]);
        vm.ScenarioElapsedSeconds = 20;

        // The 0.5s deadband keeps the bookmark we are parked on out of both directions.
        Assert.Equal("bm-3", vm.FindNextBookmark()?.Id);
        Assert.Equal("bm-1", vm.FindPrevBookmark()?.Id);
    }

    [AvaloniaFact]
    public void FindNextAndPrev_AtTheEnds_ReturnNull()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("bm-1", 10, null)]);

        vm.ScenarioElapsedSeconds = 0;
        Assert.Null(vm.FindPrevBookmark());

        vm.ScenarioElapsedSeconds = 100;
        Assert.Null(vm.FindNextBookmark());
    }

    [AvaloniaFact]
    public void ListLabel_LeadsWithTheId()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("bm-4", 5, "Conflict")]);

        Assert.StartsWith("bm-4", Assert.Single(vm.Bookmarks).ListLabel);
    }

    [AvaloniaFact]
    public void ItemRenameCommand_RaisesNamePromptWithCurrentName()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyBookmarks([Bm("a", 5, "First")]);
        var bookmark = Assert.Single(vm.Bookmarks);

        BookmarkNamePrompt? prompted = null;
        vm.BookmarkNamePromptRequested += p => prompted = p;

        bookmark.RenameCommand.Execute(null);

        Assert.NotNull(prompted);
        Assert.Equal("First", prompted!.InitialName);
    }
}
