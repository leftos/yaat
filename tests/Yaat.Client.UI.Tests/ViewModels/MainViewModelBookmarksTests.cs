using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Simulation;

namespace Yaat.Client.UI.Tests.ViewModels;

public class MainViewModelBookmarksTests
{
    [AvaloniaFact]
    public void QuickAdd_AddsBookmarkAtCurrentElapsed()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ScenarioElapsedSeconds = 42;

        vm.QuickAddBookmarkCommand.Execute(null);

        var bookmark = Assert.Single(vm.Bookmarks);
        Assert.Equal(42, bookmark.TimeSeconds);
        Assert.True(vm.HasBookmarks);
    }

    [AvaloniaFact]
    public void Add_InsertsSortedByTime()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ScenarioElapsedSeconds = 30;
        vm.QuickAddBookmarkCommand.Execute(null);
        vm.ScenarioElapsedSeconds = 10;
        vm.QuickAddBookmarkCommand.Execute(null);
        vm.ScenarioElapsedSeconds = 20;
        vm.QuickAddBookmarkCommand.Execute(null);

        Assert.Equal([10, 20, 30], vm.Bookmarks.Select(b => b.TimeSeconds));
    }

    [AvaloniaFact]
    public void AddBookmark_RaisesNamePromptForNewBookmark()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ScenarioElapsedSeconds = 5;

        TimelineBookmarkVm? prompted = null;
        vm.BookmarkNamePromptRequested += b => prompted = b;

        vm.AddBookmarkCommand.Execute(null);

        Assert.NotNull(prompted);
        Assert.Equal(5, prompted!.TimeSeconds);
        Assert.Same(prompted, Assert.Single(vm.Bookmarks));
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
    public void ItemRenameCommand_RaisesNamePromptForThatBookmark()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetBookmarks([new TimelineBookmark("a", 5, "First")]);
        var bookmark = Assert.Single(vm.Bookmarks);

        TimelineBookmarkVm? prompted = null;
        vm.BookmarkNamePromptRequested += b => prompted = b;

        bookmark.RenameCommand.Execute(null);

        Assert.Same(bookmark, prompted);
    }

    [AvaloniaFact]
    public void ItemDeleteCommand_RemovesBookmark()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetBookmarks([new TimelineBookmark("a", 5, "First"), new TimelineBookmark("b", 10, "Second")]);

        vm.Bookmarks[0].DeleteCommand.Execute(null);

        Assert.Equal("Second", Assert.Single(vm.Bookmarks).Name);
        Assert.True(vm.HasBookmarks);
    }

    [AvaloniaFact]
    public void SetBookmarks_SortsAndSnapshotRoundTrips()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetBookmarks([new TimelineBookmark("c", 30, "C"), new TimelineBookmark("a", 10, null), new TimelineBookmark("b", 20, "B")]);

        Assert.Equal([10, 20, 30], vm.Bookmarks.Select(b => b.TimeSeconds));

        var snapshot = vm.SnapshotBookmarks();
        Assert.Equal([10, 20, 30], snapshot.Select(b => b.TimeSeconds));
        Assert.Null(snapshot[0].Name);
        Assert.Equal("B", snapshot[1].Name);
    }

    [AvaloniaFact]
    public void SetBookmarks_EnforcesCap()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var many = Enumerable.Range(0, 600).Select(i => new TimelineBookmark($"bm-{i}", i, null)).ToList();

        vm.SetBookmarks(many);

        Assert.Equal(500, vm.Bookmarks.Count);
    }

    [AvaloniaFact]
    public void ClearBookmarks_EmptiesCollection()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetBookmarks([new TimelineBookmark("a", 5, "First")]);

        vm.ClearBookmarks();

        Assert.Empty(vm.Bookmarks);
        Assert.False(vm.HasBookmarks);
    }
}
