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
