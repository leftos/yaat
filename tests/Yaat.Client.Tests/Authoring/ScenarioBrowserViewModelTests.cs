using System.Collections.Concurrent;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Scenario Editor's browser view model over a real store in a temp folder: the ARTCCs the user can edit, the
/// Local and Server rows with their badges, the resync on open and on Refresh (the download seam stubbed, as it is the
/// network boundary), and the commands, with every prompt faked. The post action queues, so each test runs the UI
/// work when and in the order it chooses.
/// </summary>
public sealed class ScenarioBrowserViewModelTests : IDisposable
{
    private const string Zoa = "ZOA";
    private const string Zny = "ZNY";
    private const int Seed = 7;

    // Every wait's backstop: it fires only on a hang, so it is minutes, far past any load delay (load-proof-tests).
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scenario-browser-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalScenarioStore _store;
    private readonly UserPreferences _preferences = new();
    private readonly string _savedArtccId;
    private readonly string? _savedLastArtcc;
    private readonly StubDownloads _downloads = new();
    private readonly FakePrompts _prompts = new();
    private readonly QueuedPost _ui = new();
    private readonly List<(string Json, string Name, string? OriginId)> _roomLoads = [];
    private readonly List<ScenarioBrowserViewModel> _viewModels = [];
    private bool _canLoadInRoom;
    private bool _roomHasScenario;

    public ScenarioBrowserViewModelTests()
    {
        _store = new LocalScenarioStore(Path.Combine(_root, "store"));
        _savedArtccId = _preferences.ArtccId;
        _savedLastArtcc = _preferences.ScenarioEditorLastArtcc;
    }

    public void Dispose()
    {
        foreach (ScenarioBrowserViewModel vm in _viewModels)
        {
            vm.Dispose();
        }

        _ui.Dispose();

        // The preferences file is shared by the test process: put back what these tests changed.
        if (_preferences.ArtccId != _savedArtccId)
        {
            _preferences.SetArtccId(_savedArtccId);
        }

        if (_preferences.ScenarioEditorLastArtcc != _savedLastArtcc)
        {
            _preferences.SetScenarioEditorLastArtcc(_savedLastArtcc);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task NoArtcc_ListsNothing_SaysSo_CommandsDisabled()
    {
        ScenarioBrowserViewModel vm = NewViewModel();

        await _ui.Run(vm.InitializeAsync());

        Assert.Equal("No ARTCC to edit: set your ARTCC in Settings.", vm.StatusText);
        Assert.False(vm.HasArtcc);
        Assert.Empty(vm.LocalRows);
        Assert.Empty(vm.ServerRows);
        Assert.Empty(_downloads.Calls);
        Assert.All(AllCommands(vm), command => Assert.False(command.CanExecute(null)));
    }

    [Fact]
    public async Task AccessReadFails_StatusSaysWhy_NothingListed()
    {
        ScenarioBrowserViewModel vm = NewViewModel(new FailingArtccs("the server refused the request"));

        await _ui.Run(vm.InitializeAsync());

        Assert.Equal("Couldn't read the ARTCCs you can edit: the server refused the request", vm.StatusText);
        Assert.False(vm.HasArtcc);
        Assert.Empty(_downloads.Calls);
        Assert.All(AllCommands(vm), command => Assert.False(command.CanExecute(null)));
    }

    [Fact]
    public async Task OneArtcc_ShowsALabel_NoSelector()
    {
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        Assert.True(vm.HasArtcc);
        Assert.True(vm.IsArtccLabelVisible);
        Assert.False(vm.IsArtccSelectorVisible);
        Assert.Equal(Zoa, vm.ArtccLabelText);
        Assert.Equal(Zoa, vm.SelectedArtcc);
        Assert.Equal([Zoa], _downloads.Calls.Select(c => c.Artcc));
    }

    [Fact]
    public async Task TwoArtccs_SelectorDefaultsToTheSettingsArtcc_PickIsRememberedNotWrittenToSettings()
    {
        _preferences.SetArtccId(Zoa);
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zny, Zoa);

        Assert.True(vm.IsArtccSelectorVisible);
        Assert.False(vm.IsArtccLabelVisible);
        Assert.Equal([Zny, Zoa], vm.Artccs);
        Assert.Equal(Zoa, vm.SelectedArtcc);

        vm.SelectedArtcc = Zny;
        await _ui.Run(vm.ArtccSwitch);
        await _ui.Run(vm.ResyncTask);

        Assert.Equal(Zny, vm.SelectedArtcc);
        Assert.Equal(Zny, _preferences.ScenarioEditorLastArtcc);
        Assert.Equal(Zoa, _preferences.ArtccId);
        Assert.Equal([Zoa, Zny], _downloads.Calls.Select(c => c.Artcc));
    }

    [Fact]
    public async Task TwoArtccs_SettingsArtccNotListed_DefaultsToTheRememberedPick()
    {
        _preferences.SetArtccId("ZLA");
        _preferences.SetScenarioEditorLastArtcc(Zny);

        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa, Zny);

        Assert.Equal(Zny, vm.SelectedArtcc);
        Assert.Equal([Zny], _downloads.Calls.Select(c => c.Artcc));
    }

    [Fact]
    public async Task SwitchArtccMidResync_CancelsTheOldRun_StaleCompletionIgnored()
    {
        Mirror(Zoa, "ZOA1", "Oakland One");
        Mirror(Zny, "ZNY1", "New York One");
        var zoaRun = new TaskCompletionSource<DownloadReport>();
        _downloads.Next = artcc => artcc == Zoa ? zoaRun.Task : Task.FromResult(Online(artcc));
        _preferences.SetArtccId(Zoa);
        ScenarioBrowserViewModel vm = NewViewModel(Zoa, Zny);
        await _ui.Run(vm.InitializeAsync());
        Task staleRun = vm.ResyncTask;
        Assert.Equal(["ZOA1"], vm.ServerRows.Select(r => r.Id));

        // The download starts on a pool thread: switch only once the ZOA call is in flight, or the switch's cancel
        // stops it before it is made.
        Task zoaStarted = _downloads.Started(Zoa);
        await WaitUntil(() => zoaStarted.IsCompleted);
        vm.SelectedArtcc = Zny;
        await _ui.Run(vm.ArtccSwitch);
        await _ui.Run(vm.ResyncTask);

        Assert.True(_downloads.Calls.Single(c => c.Artcc == Zoa).Ct.IsCancellationRequested);
        zoaRun.SetResult(Online(Zoa) with { Downloaded = ["ZOA2"], Failed = [new DownloadFailure("ZOA3", "Stale", "timeout")] });
        await _ui.Run(staleRun);

        Assert.Equal(["ZNY1"], vm.ServerRows.Select(r => r.Id));
        Assert.Equal("0 downloaded, 0 updated, 0 failed", vm.StatusText);
        Assert.DoesNotContain(vm.Notices, n => n.Contains("Stale", StringComparison.Ordinal));
        Assert.False(vm.IsResyncRunning);
    }

    [Fact]
    public async Task Offline_WithMirrors_ListsThemAtOnce_StatusSaysOffline()
    {
        Mirror(Zoa, "A1", "Alpha");
        Mirror(Zoa, "B1", "Bravo");
        var run = new TaskCompletionSource<DownloadReport>();
        _downloads.Next = _ => run.Task;
        ScenarioBrowserViewModel vm = NewViewModel(Zoa);

        await _ui.Run(vm.InitializeAsync());

        Assert.True(vm.IsResyncRunning);
        Assert.Equal(["Alpha", "Bravo"], vm.ServerRows.Select(r => r.Name));

        run.SetException(new HttpRequestException("no route to host"));
        await _ui.Run(vm.ResyncTask);

        Assert.False(vm.IsResyncRunning);
        Assert.Equal("Offline: 2 downloaded scenarios", vm.StatusText);
        Assert.Contains("Download failed: no route to host", vm.Notices);
        Assert.Equal(2, vm.ServerRows.Count);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Offline_WithNothingDownloaded_ShowsEmptyStates_NewAndImportStillWork()
    {
        _downloads.Next = artcc => Task.FromResult(DownloadReport.ListFailed(artcc, "HTTP 503"));
        string file = WriteFile("imported.json", Scenario("IMP1", "Imported", Zoa));
        _prompts.ImportFiles = [file];

        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        Assert.Equal($"Offline: no downloaded scenarios for {Zoa}", vm.StatusText);
        Assert.Contains("Download failed: HTTP 503", vm.Notices);
        Assert.Empty(vm.ServerRows);
        Assert.Empty(vm.LocalRows);

        await _ui.Run(vm.NewCommand.ExecuteAsync(null));
        await _ui.Run(vm.ImportCommand.ExecuteAsync(null));

        Assert.Equal(2, vm.LocalRows.Count);
        Assert.Contains(vm.LocalRows, r => (r.Id == "IMP1") && (r.Name == "Imported"));
        Assert.Contains(vm.LocalRows, r => r.Name == ScenarioBrowserViewModel.NewScenarioName);
        Assert.All(vm.LocalRows, r => Assert.Contains(ScenarioBadge.LocalOnly, r.Badges));
    }

    [Fact]
    public async Task CorruptEntries_AreListedWithTheirReason_NotOpenable_WorkingCopyDeletable()
    {
        Mirror(Zoa, "M1", "Mike");
        File.WriteAllText(_store.GetMirrorPath(Zoa, "M1"), "{ broken");
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        File.WriteAllText(_store.GetWorkingCopyPath(Zoa, "L1"), "[1, 2]");
        _prompts.ConfirmDelete = true;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        ScenarioBrowserRowViewModel server = Assert.Single(vm.ServerRows);
        ScenarioBrowserRowViewModel local = Assert.Single(vm.LocalRows);
        Assert.Contains(ScenarioBadge.Corrupt, server.Badges);
        Assert.Contains(ScenarioBadge.Corrupt, local.Badges);
        Assert.False(string.IsNullOrEmpty(server.Problem));
        Assert.False(string.IsNullOrEmpty(local.Problem));
        Assert.Contains(vm.Notices, n => n.StartsWith("server/M1.json: ", StringComparison.Ordinal));
        Assert.Contains(vm.Notices, n => n.StartsWith("local/L1.json: ", StringComparison.Ordinal));

        Select(vm, server);
        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.StartWorkingCopyCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));

        Select(vm, local);
        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.DuplicateCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));

        await _ui.Run(vm.DeleteCommand.ExecuteAsync(null));

        Assert.Empty(vm.LocalRows);
        Assert.False(File.Exists(_store.GetWorkingCopyPath(Zoa, "L1")));
        Assert.True(File.Exists(_store.GetMirrorPath(Zoa, "M1")));
    }

    [Fact]
    public async Task ServerChangedCopy_HasTheBadge_AndIsUntouched()
    {
        Mirror(Zoa, "S1", "Sierra");
        _store.CreateWorkingCopy(Zoa, "S1");
        _store.ApplyServerBody(Zoa, "S1", ScenarioWith("S1", "Sierra", Zoa, "\"description\": \"changed on the server\""));
        string before = _store.ReadWorkingCopyText(Zoa, "S1");
        _downloads.Next = artcc =>
        {
            _store.ApplyServerBody(artcc, "S1", ScenarioWith("S1", "Sierra", artcc, "\"description\": \"changed again\""));
            return Task.FromResult(Online(artcc) with { ServerChanged = ["S1"] });
        };

        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        ScenarioBrowserRowViewModel local = Assert.Single(vm.LocalRows);
        Assert.Equal(ScenarioRowKind.WorkingCopy, local.Kind);
        Assert.Equal("S1", local.OriginId);
        Assert.NotNull(local.BaseVersion);
        Assert.Contains(ScenarioBadge.ServerChanged, local.Badges);
        Assert.DoesNotContain(ScenarioBadge.Modified, local.Badges);
        Assert.DoesNotContain(ScenarioBadge.LocalOnly, local.Badges);
        Assert.True(Assert.Single(vm.ServerRows).HasWorkingCopy);
        Assert.Equal("0 downloaded, 1 updated, 0 failed", vm.StatusText);
        Assert.Equal(before, _store.ReadWorkingCopyText(Zoa, "S1"));
    }

    [Fact]
    public async Task Badges_ModifiedAndGoneFromServer()
    {
        Mirror(Zoa, "S1", "Sierra");
        Mirror(Zoa, "G1", "Golf");
        _store.CreateWorkingCopy(Zoa, "S1");
        _store.CreateWorkingCopy(Zoa, "G1");
        _store.SaveWorkingCopy(Zoa, "S1", Scenario("S1", "Sierra edited", Zoa));
        _store.MarkGoneFromServer(Zoa, ["S1"]);

        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        ScenarioBrowserRowViewModel edited = Assert.Single(vm.LocalRows, r => r.Id == "S1");
        ScenarioBrowserRowViewModel goneCopy = Assert.Single(vm.LocalRows, r => r.Id == "G1");
        Assert.Equal([ScenarioBadge.Modified, ScenarioBadge.Unexported], edited.Badges);
        Assert.Equal([ScenarioBadge.GoneFromServer], goneCopy.Badges);
        Assert.Equal([ScenarioBadge.GoneFromServer], Assert.Single(vm.ServerRows, r => r.Id == "G1").Badges);
        Assert.Empty(Assert.Single(vm.ServerRows, r => r.Id == "S1").Badges);
    }

    [Fact]
    public async Task Online_Refresh_RebuildsRows_KeepsTheSelection_StatusSummarizesTheReport()
    {
        Mirror(Zoa, "A1", "Alpha");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.ServerRows));
        _downloads.Next = artcc =>
        {
            Mirror(artcc, "B1", "Bravo");
            return Task.FromResult(Online(artcc) with { Downloaded = ["B1"], Failed = [new DownloadFailure("C1", "Charlie", "HTTP 500")] });
        };

        await _ui.Run(vm.RefreshCommand.ExecuteAsync(null));

        Assert.Equal(["Alpha", "Bravo"], vm.ServerRows.Select(r => r.Name));
        Assert.Equal(ScenarioBrowserTab.Server, vm.SelectedTab);
        Assert.Equal("A1", vm.SelectedServerRow?.Id);
        Assert.Same(vm.ServerRows[0], vm.SelectedServerRow);
        Assert.Equal("1 downloaded, 0 updated, 1 failed", vm.StatusText);
        Assert.Contains("Charlie: HTTP 500", vm.Notices);
        Assert.Equal(vm.Notices.Count, vm.NoticeCount);
        Assert.Equal(2, _downloads.Calls.Length);
    }

    [Fact]
    public async Task NoticeFlagAndHeader_FollowTheNoticeCount()
    {
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        Assert.False(vm.HasNotices);
        Assert.Equal("0 problems from the last refresh", vm.NoticesHeaderText);

        _downloads.Next = artcc => Task.FromResult(Online(artcc) with { Failed = [Failure("C1", "Charlie")] });
        await _ui.Run(vm.RefreshCommand.ExecuteAsync(null));

        Assert.True(vm.HasNotices);
        Assert.Equal("1 problem from the last refresh", vm.NoticesHeaderText);

        _downloads.Next = artcc =>
            Task.FromResult(Online(artcc) with { Failed = [Failure("C1", "Charlie"), Failure("D1", "Delta"), Failure("E1", "Echo")] });
        await _ui.Run(vm.RefreshCommand.ExecuteAsync(null));

        Assert.True(vm.HasNotices);
        Assert.Equal("3 problems from the last refresh", vm.NoticesHeaderText);
    }

    [Fact]
    public async Task OfflineFlag_IsSetWithTheOfflineStatus_AndClearedByTheNextStatus()
    {
        var run = new TaskCompletionSource<DownloadReport>();
        _downloads.Next = _ => run.Task;
        ScenarioBrowserViewModel vm = NewViewModel(Zoa);
        await _ui.Run(vm.InitializeAsync());

        Assert.True(vm.IsResyncRunning);
        Assert.True(vm.IsProgressIndeterminate);
        Assert.False(vm.IsStatusOffline);

        run.SetResult(DownloadReport.ListFailed(Zoa, "HTTP 503"));
        await _ui.Run(vm.ResyncTask);

        Assert.True(vm.IsStatusOffline);

        _downloads.Next = artcc => Task.FromResult(Online(artcc));
        await _ui.Run(vm.RefreshCommand.ExecuteAsync(null));

        Assert.False(vm.IsStatusOffline);
        Assert.Equal("0 downloaded, 0 updated, 0 failed", vm.StatusText);
    }

    [Fact]
    public async Task NothingVisibleChanges_UntilThePostRuns()
    {
        Mirror(Zoa, "A1", "Alpha");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        _downloads.Next = artcc =>
        {
            Mirror(artcc, "B1", "Bravo");
            return Task.FromResult(Online(artcc) with { Downloaded = ["B1"] });
        };

        Task refresh = vm.RefreshCommand.ExecuteAsync(null);
        await _ui.WaitForPending(1);

        Assert.Equal(["A1"], vm.ServerRows.Select(r => r.Id));
        Assert.Equal("Downloading…", vm.StatusText);
        Assert.True(vm.IsResyncRunning);

        await _ui.Run(refresh);

        Assert.Equal(["A1", "B1"], vm.ServerRows.Select(r => r.Id));
        Assert.Equal("1 downloaded, 0 updated, 0 failed", vm.StatusText);
    }

    [Fact]
    public async Task StaleResyncCompletion_DoesNotOverwriteNewerRows()
    {
        Mirror(Zoa, "A1", "Alpha");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        _downloads.Next = artcc =>
        {
            Mirror(artcc, "B1", "Bravo");
            return Task.FromResult(Online(artcc) with { Downloaded = ["B1"] });
        };

        // The resync's load reads the rows first; a New command's load reads them after it, and its rows apply first.
        Task refresh = vm.RefreshCommand.ExecuteAsync(null);
        await _ui.WaitForPending(1);
        Task create = vm.NewCommand.ExecuteAsync(null);
        await _ui.WaitForPending(2);
        _ui.RunAt(1);
        _ui.RunAt(0);
        await _ui.Run(Task.WhenAll(refresh, create));

        ScenarioBrowserRowViewModel created = Assert.Single(vm.LocalRows);
        Assert.Equal(ScenarioBrowserViewModel.NewScenarioName, created.Name);
        Assert.Same(created, vm.SelectedLocalRow);
        Assert.Equal(["A1", "B1"], vm.ServerRows.Select(r => r.Id));
        Assert.Equal("1 downloaded, 0 updated, 0 failed", vm.StatusText);
    }

    [Fact]
    public async Task CommandReloadForTheOldArtcc_AfterASwitch_IsDropped()
    {
        Mirror(Zoa, "A1", "Alpha");
        Mirror(Zny, "N1", "November");
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        _preferences.SetArtccId(Zoa);
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa, Zny);
        Select(vm, Assert.Single(vm.LocalRows));
        var confirm = new TaskCompletionSource<bool>();
        _prompts.ConfirmGate = confirm.Task;

        Task delete = vm.DeleteCommand.ExecuteAsync(null);
        _ui.Drain();
        vm.SelectedArtcc = Zny;
        await _ui.Run(vm.ArtccSwitch);
        await _ui.Run(vm.ResyncTask);
        confirm.SetResult(true);
        await _ui.Run(delete);

        Assert.False(File.Exists(_store.GetWorkingCopyPath(Zoa, "L1")));
        Assert.Equal(Zny, vm.SelectedArtcc);
        Assert.Equal(["N1"], vm.ServerRows.Select(r => r.Id));
        Assert.Empty(vm.LocalRows);
    }

    [Fact]
    public async Task Progress_IsShownWhileDownloading()
    {
        var run = new TaskCompletionSource<DownloadReport>();
        _downloads.Next = _ => run.Task;
        _downloads.Report = progress => progress.Report(new DownloadProgress(1, 3, "A1"));
        ScenarioBrowserViewModel vm = NewViewModel(Zoa);
        await _ui.Run(vm.InitializeAsync());

        await _ui.WaitForPending(1);
        _ui.Drain();

        Assert.Equal("Downloading 1/3…", vm.StatusText);
        Assert.Equal(1, vm.ProgressDone);
        Assert.Equal(3, vm.ProgressTotal);

        run.SetResult(Online(Zoa));
        await _ui.Run(vm.ResyncTask);

        Assert.Equal("0 downloaded, 0 updated, 0 failed", vm.StatusText);
    }

    [Fact]
    public async Task Dispose_CancelsTheRunningResync_LaterCompletionChangesNothing()
    {
        Mirror(Zoa, "A1", "Alpha");
        var run = new TaskCompletionSource<DownloadReport>();
        _downloads.Next = _ => run.Task;
        ScenarioBrowserViewModel vm = NewViewModel(Zoa);
        await _ui.Run(vm.InitializeAsync());
        Task resync = vm.ResyncTask;
        await WaitUntil(() => _downloads.Calls.Length == 1);

        vm.Dispose();
        Mirror(Zoa, "B1", "Bravo");
        run.SetResult(Online(Zoa) with { Downloaded = ["B1"] });
        await _ui.Run(resync);

        Assert.True(_downloads.Calls[0].Ct.IsCancellationRequested);
        Assert.Equal(["A1"], vm.ServerRows.Select(r => r.Id));
        Assert.Equal("Downloading…", vm.StatusText);
        Assert.True(vm.IsResyncRunning);
    }

    [Fact]
    public async Task Dispose_DuringStartUp_EndsQuietly()
    {
        ScenarioBrowserViewModel vm = NewViewModel(new BlockedArtccs());

        Task start = vm.InitializeAsync();
        vm.Dispose();
        await _ui.Run(start);

        Assert.False(vm.HasArtcc);
        Assert.Empty(vm.Artccs);
        Assert.Equal("", vm.StatusText);
        Assert.Empty(_downloads.Calls);
    }

    [Fact]
    public async Task Duplicate_MakesALocalOnlyCopyWithAFreshId()
    {
        Mirror(Zoa, "S1", "Sierra");
        _store.CreateWorkingCopy(Zoa, "S1");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));
        string expectedId = ScenarioIdGenerator.Create(Now, new Random(Seed));

        await _ui.Run(vm.DuplicateCommand.ExecuteAsync(null));

        Assert.Equal(2, vm.LocalRows.Count);
        ScenarioBrowserRowViewModel copy = Assert.Single(vm.LocalRows, r => r.Id != "S1");
        Assert.Equal(expectedId, copy.Id);
        Assert.Equal("Sierra", copy.Name);
        Assert.Null(copy.OriginId);
        Assert.Contains(ScenarioBadge.LocalOnly, copy.Badges);
        Assert.DoesNotContain(ScenarioBadge.ServerChanged, copy.Badges);
        Assert.Same(copy, vm.SelectedLocalRow);
        Assert.Contains($"\"id\": \"{expectedId}\"", _store.ReadWorkingCopyText(Zoa, expectedId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreFailureInACommand_IsANoticeNamingTheScenario()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));
        File.Delete(_store.GetWorkingCopyPath(Zoa, "L1"));

        await _ui.Run(vm.DuplicateCommand.ExecuteAsync(null));

        Assert.Single(vm.Notices, n => n.StartsWith("Lima: ", StringComparison.Ordinal));
        Assert.Single(vm.LocalRows);
    }

    [Fact]
    public async Task Delete_AsksFirst_DeclineKeepsTheCopy_ConfirmRemovesIt()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));

        _prompts.ConfirmDelete = false;
        await _ui.Run(vm.DeleteCommand.ExecuteAsync(null));

        Assert.Equal(["Lima"], _prompts.DeleteAsked.Select(r => r.Name));
        Assert.Single(vm.LocalRows);
        Assert.True(File.Exists(_store.GetWorkingCopyPath(Zoa, "L1")));

        _prompts.ConfirmDelete = true;
        await _ui.Run(vm.DeleteCommand.ExecuteAsync(null));

        Assert.Equal(2, _prompts.DeleteAsked.Count);
        Assert.Empty(vm.LocalRows);
        Assert.False(File.Exists(_store.GetWorkingCopyPath(Zoa, "L1")));
    }

    [Fact]
    public async Task Import_ThreeFiles_IdClashOnOne_ApplyToAllOnTheRest_AndAnArtccMismatchNotice()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        string original = _store.ReadWorkingCopyText(Zoa, "L1");
        _prompts.ImportFiles =
        [
            WriteFile("one.json", Scenario("L1", "One", Zoa)),
            WriteFile("two.json", Scenario("L1", "Two", Zoa)),
            WriteFile("three.json", Scenario("F3", "Three", Zny)),
        ];
        _prompts.ConflictAnswers.Enqueue(new ImportConflictAnswer(ImportIdChoice.NewId, ApplyToRemaining: true, Compare: false));
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        await _ui.Run(vm.ImportCommand.ExecuteAsync(null));

        ImportConflictQuestion asked = Assert.Single(_prompts.ConflictsAsked);
        Assert.Equal("L1", asked.ScenarioId);
        Assert.Equal("One", asked.ScenarioName);
        Assert.Equal(ScenarioIdConflict.WorkingCopy, asked.Conflict);
        Assert.Equal(2, asked.RemainingFiles);
        Assert.Equal(["Lima", "One", "Three", "Two"], vm.LocalRows.Select(r => r.Name));
        Assert.Contains(vm.LocalRows, r => r.Id == "F3");
        Assert.Equal(4, vm.LocalRows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(original, _store.ReadWorkingCopyText(Zoa, "L1"));
        string notice = Assert.Single(vm.Notices);
        Assert.StartsWith("Three: ", notice, StringComparison.Ordinal);
        Assert.Contains("artccId", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_ApplyToAll_CoversOnlyLaterClashesOfTheSameKind()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        Mirror(Zoa, "M1", "Mike");
        _prompts.ImportFiles =
        [
            WriteFile("one.json", Scenario("L1", "One", Zoa)),
            WriteFile("two.json", Scenario("M1", "Two", Zoa)),
            WriteFile("three.json", Scenario("L1", "Three", Zoa)),
        ];
        _prompts.ConflictAnswers.Enqueue(new ImportConflictAnswer(ImportIdChoice.NewId, ApplyToRemaining: true, Compare: false));
        _prompts.ConflictAnswers.Enqueue(new ImportConflictAnswer(ImportIdChoice.Skip, ApplyToRemaining: false, Compare: false));
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        await _ui.Run(vm.ImportCommand.ExecuteAsync(null));

        Assert.Equal([ScenarioIdConflict.WorkingCopy, ScenarioIdConflict.Mirror], _prompts.ConflictsAsked.Select(q => q.Conflict));
        Assert.Equal(["Two"], _prompts.ConflictsAsked.Skip(1).Select(q => q.ScenarioName));
        Assert.Equal(["Lima", "One", "Three"], vm.LocalRows.Select(r => r.Name));
    }

    [Fact]
    public async Task Import_KeepIdOverAServerId_BecomesThatScenariosWorkingCopy()
    {
        Mirror(Zoa, "A1", "Alpha");
        _prompts.ImportFiles = [WriteFile("alpha.json", Scenario("A1", "Alpha edited", Zoa))];
        _prompts.ConflictAnswers.Enqueue(new ImportConflictAnswer(ImportIdChoice.KeepId, ApplyToRemaining: false, Compare: false));
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        await _ui.Run(vm.ImportCommand.ExecuteAsync(null));

        Assert.Equal(ScenarioIdConflict.Mirror, Assert.Single(_prompts.ConflictsAsked).Conflict);
        ScenarioBrowserRowViewModel copy = Assert.Single(vm.LocalRows);
        Assert.Equal("A1", copy.Id);
        Assert.Equal("A1", copy.OriginId);
        Assert.Equal("Alpha edited", copy.Name);
        Assert.Contains(ScenarioBadge.Modified, copy.Badges);
        Assert.Same(copy, vm.SelectedLocalRow);
        Assert.True(Assert.Single(vm.ServerRows).HasWorkingCopy);
    }

    [Fact]
    public async Task Import_UnreadableAndRejectedFiles_AreNotices()
    {
        _prompts.ImportFiles = [Path.Combine(_root, "missing.json"), WriteFile("bad.json", "[1, 2]")];
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);

        await _ui.Run(vm.ImportCommand.ExecuteAsync(null));

        Assert.Contains(vm.Notices, n => n.StartsWith("missing.json: ", StringComparison.Ordinal));
        Assert.Contains(vm.Notices, n => n.StartsWith("bad.json: not imported: ", StringComparison.Ordinal));
        Assert.Empty(vm.LocalRows);
        Assert.Empty(_prompts.ConflictsAsked);
    }

    [Fact]
    public async Task SaveCopy_WritesTheStoredTextByteForByte()
    {
        _store.ApplyServerBody(Zoa, "A1", Scenario("A1", "Alpha/Bravo", Zoa));
        byte[] stored =
        [
            0xEF,
            0xBB,
            0xBF,
            .. Encoding.UTF8.GetBytes("{\"id\":\"A1\",  \"name\":\"Alpha/Bravo\",\r \"speed\": 250.0, \"note\": \"café\"}\n"),
        ];
        File.WriteAllBytes(_store.GetMirrorPath(Zoa, "A1"), stored);
        string target = Path.Combine(_root, "saved.json");
        _prompts.SavePath = target;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.ServerRows));

        await _ui.Run(vm.SaveCopyCommand.ExecuteAsync(null));

        Assert.Equal(["Alpha_Bravo.json"], _prompts.SaveSuggested);
        Assert.Equal(stored, File.ReadAllBytes(target));
        Assert.DoesNotContain(vm.Notices, n => n.StartsWith("Alpha/Bravo: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenServerRow_CreatesTheWorkingCopy_ThenRaisesOpenRequested()
    {
        Mirror(Zoa, "A1", "Alpha");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.ServerRows));
        var opened = new List<(ScenarioOpenRequestedEventArgs Args, bool CopyExisted)>();
        vm.OpenRequested += (_, e) => opened.Add((e, File.Exists(_store.GetWorkingCopyPath(e.ArtccId, e.Id))));

        Assert.True(vm.OpenCommand.CanExecute(null));
        await _ui.Run(vm.OpenCommand.ExecuteAsync(null));

        (ScenarioOpenRequestedEventArgs args, bool copyExisted) = Assert.Single(opened);
        Assert.Equal(Zoa, args.ArtccId);
        Assert.Equal("A1", args.Id);
        Assert.Equal(ScenarioRowKind.WorkingCopy, args.Kind);
        Assert.True(copyExisted);
        ScenarioBrowserRowViewModel local = Assert.Single(vm.LocalRows);
        Assert.Equal("A1", local.OriginId);
        Assert.Equal(ScenarioBrowserTab.Local, vm.SelectedTab);
        Assert.Same(local, vm.SelectedLocalRow);
        Assert.True(Assert.Single(vm.ServerRows).HasWorkingCopy);
    }

    [Fact]
    public async Task OpenServerRow_WithItsCopy_SelectsTheCopyOnTheLocalTab_AndOpensIt()
    {
        Mirror(Zoa, "A1", "Alpha");
        _store.CreateWorkingCopy(Zoa, "A1");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.ServerRows));
        var opened = new List<ScenarioOpenRequestedEventArgs>();
        vm.OpenRequested += (_, e) => opened.Add(e);

        await _ui.Run(vm.OpenCommand.ExecuteAsync(null));

        Assert.Equal("A1", Assert.Single(opened).Id);
        Assert.Equal(ScenarioBrowserTab.Local, vm.SelectedTab);
        Assert.Same(Assert.Single(vm.LocalRows), vm.SelectedLocalRow);
    }

    [Fact]
    public async Task OpenServerRow_WhoseCopyIsCorrupt_OpensNothing()
    {
        Mirror(Zoa, "A1", "Alpha");
        _store.CreateWorkingCopy(Zoa, "A1");
        File.WriteAllText(_store.GetWorkingCopyPath(Zoa, "A1"), "[1]");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.ServerRows));
        var opened = new List<ScenarioOpenRequestedEventArgs>();
        vm.OpenRequested += (_, e) => opened.Add(e);

        Assert.False(vm.OpenCommand.CanExecute(null));
        await _ui.Run(vm.OpenCommand.ExecuteAsync(null));

        Assert.Empty(opened);
        Assert.Equal(ScenarioBrowserTab.Server, vm.SelectedTab);
    }

    [Fact]
    public async Task OpenWorkingCopy_RaisesOpenRequested()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));
        var opened = new List<ScenarioOpenRequestedEventArgs>();
        vm.OpenRequested += (_, e) => opened.Add(e);

        await _ui.Run(vm.OpenCommand.ExecuteAsync(null));

        ScenarioOpenRequestedEventArgs args = Assert.Single(opened);
        Assert.Equal("L1", args.Id);
        Assert.Equal(ScenarioRowKind.WorkingCopy, args.Kind);
    }

    [Fact]
    public async Task TestInRoom_NeedsAWorkingCopyAndARoom_LoadsTheStoredText()
    {
        Mirror(Zoa, "A1", "Alpha");
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));
        int changes = 0;
        vm.TestInRoomCommand.CanExecuteChanged += (_, _) => changes++;

        Assert.False(vm.TestInRoomCommand.CanExecute(null));
        _canLoadInRoom = true;
        vm.RefreshTestInRoom();

        Assert.True(changes > 0);
        Assert.True(vm.TestInRoomCommand.CanExecute(null));

        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        (string json, string name, string? originId) = Assert.Single(_roomLoads);
        Assert.Equal(_store.ReadWorkingCopyText(Zoa, "L1"), json);
        Assert.Equal("Lima", name);
        Assert.Null(originId);

        Select(vm, Assert.Single(vm.ServerRows));
        Assert.False(vm.TestInRoomCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestInRoom_ServerDerivedCopy_PassesItsOriginId()
    {
        Mirror(Zoa, "A1", "Alpha");
        _store.CreateWorkingCopy(Zoa, "A1");
        _canLoadInRoom = true;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));

        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        (string json, string name, string? originId) = Assert.Single(_roomLoads);
        Assert.Equal(_store.ReadWorkingCopyText(Zoa, "A1"), json);
        Assert.Equal("Alpha", name);
        Assert.Equal("A1", originId);
    }

    [Fact]
    public async Task TestInRoom_CorruptCopy_IsDisabled()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        File.WriteAllText(_store.GetWorkingCopyPath(Zoa, "L1"), "[1, 2]");
        _canLoadInRoom = true;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));

        Assert.False(vm.TestInRoomCommand.CanExecute(null));
        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        Assert.Empty(_roomLoads);
    }

    [Fact]
    public async Task TestInRoom_WithAScenarioRunning_AsksFirst_NoLeavesTheRoomAlone_YesLoads()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        _canLoadInRoom = true;
        _roomHasScenario = true;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));

        _prompts.ConfirmReplaceRunning = false;
        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        Assert.Equal(["Lima"], _prompts.ReplaceRunningAsked);
        Assert.Empty(_roomLoads);

        _prompts.ConfirmReplaceRunning = true;
        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        Assert.Equal(["Lima", "Lima"], _prompts.ReplaceRunningAsked);
        Assert.Equal("Lima", Assert.Single(_roomLoads).Name);
    }

    [Fact]
    public async Task TestInRoom_WithNoScenarioRunning_AsksNothing()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        _canLoadInRoom = true;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));

        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        Assert.Empty(_prompts.ReplaceRunningAsked);
        Assert.Single(_roomLoads);
    }

    // The guard is the one Export asks: the editor answers it, saving an open copy's unsaved edits before the load.
    [Fact]
    public async Task TestInRoom_AsksTheSaveFirstGuardBeforeLoading_AndAStopLoadsNothing()
    {
        _store.CreateLocalScenario(Zoa, Scenario("L1", "Lima", Zoa), "L1");
        _canLoadInRoom = true;
        ScenarioBrowserViewModel vm = await OpenedViewModel(Zoa);
        Select(vm, Assert.Single(vm.LocalRows));
        var guarded = new List<(string Artcc, string Id, int LoadsBefore)>();
        bool allow = false;
        vm.ExportGuard = (artcc, id) =>
        {
            guarded.Add((artcc, id, _roomLoads.Count));
            return Task.FromResult(allow);
        };

        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        Assert.Equal([(Zoa, "L1", 0)], guarded);
        Assert.Empty(_roomLoads);

        allow = true;
        await _ui.Run(vm.TestInRoomCommand.ExecuteAsync(null));

        Assert.Equal([(Zoa, "L1", 0), (Zoa, "L1", 0)], guarded);
        Assert.Single(_roomLoads);
    }

    private static string Scenario(string id, string name, string artcc) => $"{{\"id\": \"{id}\", \"name\": \"{name}\", \"artccId\": \"{artcc}\"}}";

    private static string ScenarioWith(string id, string name, string artcc, string extra) =>
        $"{{\"id\": \"{id}\", \"name\": \"{name}\", \"artccId\": \"{artcc}\", {extra}}}";

    private static DownloadReport Online(string artcc) => new(artcc, null, [], [], [], [], [], [], []);

    private static IEnumerable<IRelayCommand> AllCommands(ScenarioBrowserViewModel vm) =>
        [
            vm.NewCommand,
            vm.DuplicateCommand,
            vm.DeleteCommand,
            vm.ImportCommand,
            vm.SaveCopyCommand,
            vm.RefreshCommand,
            vm.StartWorkingCopyCommand,
            vm.OpenCommand,
            vm.TestInRoomCommand,
            vm.ExportJsonCommand,
            vm.ExportCleanJsonCommand,
            vm.ExportAllUnexportedCommand,
            vm.ResolveServerChangesCommand,
        ];

    private static void Select(ScenarioBrowserViewModel vm, ScenarioBrowserRowViewModel row)
    {
        if (row.Kind == ScenarioRowKind.Server)
        {
            vm.SelectedTab = ScenarioBrowserTab.Server;
            vm.SelectedServerRow = row;
        }
        else
        {
            vm.SelectedTab = ScenarioBrowserTab.Local;
            vm.SelectedLocalRow = row;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        // A hang guard: a passing run never waits it out, so it is sized by the hang it rules out.
        DateTime deadline = DateTime.UtcNow + HangGuard;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Hung: the condition never held.");
            await Task.Delay(10);
        }
    }

    private void Mirror(string artcc, string id, string name) => _store.ApplyServerBody(artcc, id, Scenario(id, name, artcc));

    private string WriteFile(string name, string text)
    {
        string dir = Path.Combine(_root, "files");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<ScenarioBrowserViewModel> OpenedViewModel(params string[] artccs)
    {
        ScenarioBrowserViewModel vm = NewViewModel(artccs);
        await _ui.Run(vm.InitializeAsync());
        await _ui.Run(vm.ResyncTask);
        return vm;
    }

    private static DownloadFailure Failure(string id, string name) => new(id, name, "HTTP 500");

    private ScenarioBrowserViewModel NewViewModel(params string[] artccs) => NewViewModel(new FixedArtccs(artccs));

    private ScenarioBrowserViewModel NewViewModel(IArtccAccessSource access)
    {
        var services = new ScenarioBrowserServices(
            _preferences,
            _store,
            access,
            _downloads.Download,
            _prompts,
            new FixedClock(Now),
            new Random(Seed),
            _ui.Post,
            () => _canLoadInRoom,
            (json, name, originId) =>
            {
                _roomLoads.Add((json, name, originId));
                return Task.CompletedTask;
            },
            () => _roomHasScenario,
            ScenarioEditorFixture.OfflineConfigs(Path.Combine(_root, "artcc")),
            ScenarioEditorFixture.NoRouteFinding(),
            ScenarioEditorFixture.EchoVideoMaps()
        );
        var vm = new ScenarioBrowserViewModel(services);
        _viewModels.Add(vm);
        return vm;
    }

    private sealed class FixedArtccs(IReadOnlyList<string> artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult(artccs);
    }

    private sealed class FailingArtccs(string reason) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<string>>(new InvalidOperationException(reason));
    }

    // Never answers; a cancel ends the read as the window closing would.
    private sealed class BlockedArtccs : IArtccAccessSource
    {
        public async Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    private sealed class StubDownloads
    {
        private readonly ConcurrentQueue<(string Artcc, CancellationToken Ct)> _calls = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);

        public (string Artcc, CancellationToken Ct)[] Calls => [.. _calls];

        // Completes once a download for the ARTCC has been entered.
        public Task Started(string artcc) => StartedSource(artcc).Task;

        public Func<string, Task<DownloadReport>> Next { get; set; } = artcc => Task.FromResult(Online(artcc));

        public Action<IProgress<DownloadProgress>>? Report { get; set; }

        public Task<DownloadReport> Download(string artcc, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            _calls.Enqueue((artcc, ct));
            StartedSource(artcc).TrySetResult();
            Report?.Invoke(progress);
            return Next(artcc);
        }

        private TaskCompletionSource StartedSource(string artcc) =>
            _started.GetOrAdd(artcc, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class FakePrompts : IScenarioBrowserPrompts
    {
        public bool ConfirmDelete { get; set; }

        public Task<bool>? ConfirmGate { get; set; }

        public List<ScenarioBrowserRowViewModel> DeleteAsked { get; } = [];

        public IReadOnlyList<string> ImportFiles { get; set; } = [];

        public string? SavePath { get; set; }

        public List<string> SaveSuggested { get; } = [];

        public Queue<ImportConflictAnswer> ConflictAnswers { get; } = new();

        public List<ImportConflictQuestion> ConflictsAsked { get; } = [];

        public Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row)
        {
            DeleteAsked.Add(row);
            return ConfirmGate ?? Task.FromResult(ConfirmDelete);
        }

        public Task<IReadOnlyList<string>> PickImportFilesAsync() => Task.FromResult(ImportFiles);

        public Task<string?> PickSavePathAsync(string suggestedFileName)
        {
            SaveSuggested.Add(suggestedFileName);
            return Task.FromResult(SavePath);
        }

        public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question)
        {
            ConflictsAsked.Add(question);
            return Task.FromResult(
                ConflictAnswers.Count > 0 ? ConflictAnswers.Dequeue() : new ImportConflictAnswer(ImportIdChoice.Skip, false, false)
            );
        }

        // The browser alone never asks it: only the editor's UnsavedCopy hook does.
        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context) =>
            Task.FromResult(UnsavedChangesChoice.Cancel);

        public Task<string?> PickExportPathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<string?> PickExportFolderAsync() => Task.FromResult<string?>(null);

        // Asked only through the editor's replace guard.
        public Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName) => Task.FromResult(false);

        public bool ConfirmReplaceRunning { get; set; }

        public List<string> ReplaceRunningAsked { get; } = [];

        public Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName)
        {
            ReplaceRunningAsked.Add(scenarioName);
            return Task.FromResult(ConfirmReplaceRunning);
        }

        public Task<UnexportedChangesChoice> AskUnexportedChangesAsync(IReadOnlyList<string> names, UnexportedChangesContext context) =>
            Task.FromResult(UnexportedChangesChoice.Leave);

        public Task<UnsavedAndUnexportedChoice> AskUnsavedAndUnexportedAsync(
            string openName,
            IReadOnlyList<string> names,
            UnexportedChangesContext context
        ) => Task.FromResult(UnsavedAndUnexportedChoice.DiscardAndLeave);

        public Queue<ScenarioMergeResult?> MergeAnswers { get; } = new();

        public List<ScenarioMergeRequest> MergeRequests { get; } = [];

        public Task<ScenarioMergeResult?> ShowMergeAsync(ScenarioMergeRequest request)
        {
            MergeRequests.Add(request);
            return Task.FromResult(MergeAnswers.Count > 0 ? MergeAnswers.Dequeue() : null);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
