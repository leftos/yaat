using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.Views;
using Yaat.Sim.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Local Files tab's single-file picker (Load File…) and its Recent list (YAAT-373): a picked file
/// loads at once, recents are local entries shared with File → Recent Scenarios, a missing file is
/// marked and not loadable, and Remove drops an entry.
/// </summary>
public class LoadScenarioWindowTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Window ShowOwner()
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    private static void Click(Window window, string buttonName)
    {
        window.FindControl<Button>(buttonName)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void SelectLocalTab(Window window)
    {
        // The Local Files tab is index 1; index 0 is ARTCC Scenarios.
        window.FindControl<TabControl>("SourceTabs")!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClearRecents(UserPreferences prefs)
    {
        foreach (RecentScenario entry in prefs.RecentScenarios.ToList())
        {
            prefs.RemoveRecentScenario(entry.Key);
        }
    }

    [AvaloniaFact]
    public async Task LoadFile_PickedFile_ClosesWithThatPath()
    {
        string scenario = CopyExampleScenario();
        var picker = new FakeFilePickerService();
        picker.QueueOpenFile(scenario);
        Window owner = ShowOwner();
        var window = new LoadScenarioWindow(new UserPreferences(), null, _ => picker);
        try
        {
            Task<ScenarioLoadResult?> pending = DialogPresenter.ShowModalAsync<ScenarioLoadResult?>(window, owner);
            Dispatcher.UIThread.RunJobs();

            Click(window, "LoadFileButton");

            ScenarioLoadResult? result = await pending;
            Assert.NotNull(result);
            Assert.Equal(scenario, result!.FilePath);
            Assert.Null(result.ApiScenarioId);

            RecordedCall call = Assert.Single(picker.Calls);
            Assert.Equal(PickerKind.OpenFile, call.Kind);
            Assert.Equal("Load Scenario File", call.Title);
            OpenFileOptions options = Assert.IsType<OpenFileOptions>(call.Options);
            Assert.Contains(options.Filters, f => (f.Name == "Scenario files") && f.Patterns.Contains("*.json"));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task LoadFile_Cancelled_KeepsTheWindowOpen()
    {
        var picker = new FakeFilePickerService();
        picker.QueueOpenFile(null);
        Window owner = ShowOwner();
        var window = new LoadScenarioWindow(new UserPreferences(), null, _ => picker);
        try
        {
            Task<ScenarioLoadResult?> pending = DialogPresenter.ShowModalAsync<ScenarioLoadResult?>(window, owner);
            Dispatcher.UIThread.RunJobs();

            Click(window, "LoadFileButton");

            Assert.True(window.IsVisible);
            Assert.False(pending.IsCompleted);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task LoadFile_DoesNotChangeTheScenarioFolder()
    {
        string indexedFolder = Path.GetDirectoryName(CopyExampleScenario())!;
        string picked = CopyExampleScenario();

        var prefs = new UserPreferences();
        prefs.SetLastScenarioFolder(indexedFolder);

        var picker = new FakeFilePickerService();
        picker.QueueOpenFile(picked);

        Window owner = ShowOwner();
        var window = new LoadScenarioWindow(prefs, null, _ => picker);
        ListBox indexedList = window.FindControl<ListBox>("LocalScenarioList")!;
        object? before = indexedList.ItemsSource;
        int countBefore = ((System.Collections.ICollection)before!).Count;
        try
        {
            Task<ScenarioLoadResult?> pending = DialogPresenter.ShowModalAsync<ScenarioLoadResult?>(window, owner);
            Dispatcher.UIThread.RunJobs();

            Click(window, "LoadFileButton");
            ScenarioLoadResult? result = await pending;

            Assert.Equal(picked, result!.FilePath);

            // A picked file never re-scans the folder: the indexed folder, its list and the picker call kind are untouched.
            Assert.Equal(indexedFolder, prefs.LastScenarioFolder);
            Assert.Same(before, indexedList.ItemsSource);
            Assert.Equal(countBefore, ((System.Collections.ICollection)indexedList.ItemsSource!).Count);
            Assert.DoesNotContain(picker.Calls, c => c.Kind == PickerKind.OpenFolder);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecentList_ShowsLocalRecentsOnly_AndMarksMissing()
    {
        var prefs = new UserPreferences();
        ClearRecents(prefs);
        string existing = CopyExampleScenario();
        string missing = Path.Combine(Path.GetTempPath(), $"yaat-missing-{Guid.NewGuid():N}.json");
        prefs.AddRecentScenario(existing, "Existing One");
        prefs.AddRecentScenario(missing, "Missing One");
        prefs.AddRecentScenario("", "Api One", apiId: "api-scenario-1");

        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.RecentListReady;
            SelectLocalTab(window);

            ListBox recentList = window.FindControl<ListBox>("RecentScenarioList")!;
            Button loadButton = window.FindControl<Button>("LoadButton")!;
            var items = ((IEnumerable<RecentScenarioItem>)recentList.ItemsSource!).ToList();

            // Local entries only (the API entry is File → Recent Scenarios' concern), newest first.
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => (i.DisplayName == "Existing One") && !i.IsMissing);
            Assert.Contains(items, i => (i.DisplayName == "Missing One (missing)") && i.IsMissing);

            recentList.SelectedItem = items.First(i => !i.IsMissing);
            Dispatcher.UIThread.RunJobs();
            Assert.True(loadButton.IsEnabled);

            recentList.SelectedItem = items.First(i => i.IsMissing);
            Dispatcher.UIThread.RunJobs();
            Assert.False(loadButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecentEntry_SelectThenLoad_ClosesWithItsPath_AndClearsTheOtherList()
    {
        string existing = CopyExampleScenario();
        var prefs = new UserPreferences();
        ClearRecents(prefs);
        prefs.AddRecentScenario(existing, "Recent One");
        prefs.SetLastScenarioFolder(Path.GetDirectoryName(existing)!);

        Window owner = ShowOwner();
        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            Task<ScenarioLoadResult?> pending = DialogPresenter.ShowModalAsync<ScenarioLoadResult?>(window, owner);
            Dispatcher.UIThread.RunJobs();
            await window.RecentListReady;
            SelectLocalTab(window);

            ListBox recentList = window.FindControl<ListBox>("RecentScenarioList")!;
            ListBox indexedList = window.FindControl<ListBox>("LocalScenarioList")!;
            RecentScenarioItem recent = ((IEnumerable<RecentScenarioItem>)recentList.ItemsSource!).Single();
            LocalScenarioItem indexed = ((IEnumerable<LocalScenarioItem>)indexedList.ItemsSource!).Single();

            // The two lists are mutually exclusive: a selection in one clears the other.
            indexedList.SelectedItem = indexed;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(recentList.SelectedItem);

            recentList.SelectedItem = recent;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(indexedList.SelectedItem);

            Click(window, "LoadButton");

            ScenarioLoadResult? result = await pending;
            Assert.NotNull(result);
            Assert.Equal(existing, result!.FilePath);
            Assert.Null(result.ApiScenarioId);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecentEntry_DoubleClick_ClosesWithItsPath()
    {
        string existing = CopyExampleScenario();
        var prefs = new UserPreferences();
        ClearRecents(prefs);
        prefs.AddRecentScenario(existing, "Recent One");

        Window owner = ShowOwner();
        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            Task<ScenarioLoadResult?> pending = DialogPresenter.ShowModalAsync<ScenarioLoadResult?>(window, owner);
            Dispatcher.UIThread.RunJobs();
            await window.RecentListReady;
            SelectLocalTab(window);

            ListBox recentList = window.FindControl<ListBox>("RecentScenarioList")!;
            recentList.SelectedItem = ((IEnumerable<RecentScenarioItem>)recentList.ItemsSource!).Single();
            Dispatcher.UIThread.RunJobs();

            recentList.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));

            ScenarioLoadResult? result = await pending;
            Assert.NotNull(result);
            Assert.Equal(existing, result!.FilePath);
            Assert.Null(result.ApiScenarioId);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task RemoveRecent_DropsTheEntry_AndPersists()
    {
        var prefs = new UserPreferences();
        ClearRecents(prefs);
        string existing = CopyExampleScenario();
        prefs.AddRecentScenario(existing, "Remove Me");

        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.RecentListReady;

            ListBox recentList = window.FindControl<ListBox>("RecentScenarioList")!;
            Button remove = window.FindControl<Button>("RemoveRecentButton")!;
            RecentScenarioItem target = ((IEnumerable<RecentScenarioItem>)recentList.ItemsSource!).Single();

            recentList.SelectedItem = target;
            Dispatcher.UIThread.RunJobs();
            Assert.True(remove.IsEnabled);

            Click(window, "RemoveRecentButton");
            await window.RecentListReady;

            Assert.Empty(((IEnumerable<RecentScenarioItem>)recentList.ItemsSource!));
            Assert.False(remove.IsEnabled);
            Assert.DoesNotContain(new UserPreferences().RecentScenarios, r => r.Key == existing);
        }
        finally
        {
            window.Close();
        }
    }

    private const string IdA = "01H06NVK7VN8BS7MCDXHKJZ7MQ";
    private const string IdB = "01H08FSWF5NCDXTQMQ3BWB0BND";

    /// <summary>
    /// Preferences naming a fresh ARTCC, and its downloaded mirror seeded with the given real scenarios through the store
    /// at its default root (under this process's YAAT_APPDATA_DIR).
    /// </summary>
    private static (UserPreferences Prefs, string Artcc, LocalScenarioStore Store) SeedMirror(params string[] ids)
    {
        string artcc = "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var prefs = new UserPreferences();
        prefs.SetArtccId(artcc);
        var store = new LocalScenarioStore();
        foreach (string id in ids)
        {
            string json = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "atctrainer-scenario-examples", id + ".json"));
            Assert.Equal(ServerApplyOutcome.Added, store.ApplyServerBody(artcc, id, json).Outcome);
        }

        return (prefs, artcc, store);
    }

    private static List<ArtccScenarioItem> ArtccItems(Window window) =>
        [.. (IEnumerable<ArtccScenarioItem>)window.FindControl<ListBox>("ArtccScenarioList")!.ItemsSource!];

    private static string TextOf(Window window, string name) => window.FindControl<TextBlock>(name)!.Text ?? "";

    [AvaloniaFact]
    public async Task ArtccTab_NotConnected_ListsDownloadedScenarios()
    {
        (UserPreferences prefs, _, _) = SeedMirror(IdA, IdB);
        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            List<ArtccScenarioItem> items = ArtccItems(window);
            Assert.Equal([IdA, IdB], items.Select(i => i.Id).Order(StringComparer.Ordinal));
            Assert.Equal("Offline: 2 downloaded scenarios", TextOf(window, "ArtccStatusText"));
            Assert.False(window.FindControl<TextBlock>("ArtccGateText")!.IsVisible);
            Assert.True(window.FindControl<Button>("DownloadButton")!.IsEnabled);
            Assert.All(items, i => Assert.True(File.Exists(i.MirrorPath)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ArtccTab_NotConnected_NoDownloads_SaysSo()
    {
        (UserPreferences prefs, string artcc, _) = SeedMirror();
        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            Assert.Empty(ArtccItems(window));
            Assert.Equal($"Offline: no downloaded scenarios for {artcc}", TextOf(window, "ArtccStatusText"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ArtccTab_ShowsStoreProblems_ByFileName()
    {
        (UserPreferences prefs, string artcc, LocalScenarioStore store) = SeedMirror(IdA, IdB);
        File.WriteAllText(store.GetMirrorPath(artcc, IdA), "{ broken");
        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            TextBlock notice = window.FindControl<TextBlock>("ArtccGateText")!;
            Assert.True(notice.IsVisible);
            Assert.Contains($"server/{IdA}.json", notice.Text, StringComparison.Ordinal);
            Assert.Equal([IdB], ArtccItems(window).Select(i => i.Id));
            Assert.Equal("Offline: 1 downloaded scenario", TextOf(window, "ArtccStatusText"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ArtccTab_PickMirroredScenario_ClosesWithItsMirrorPath()
    {
        (UserPreferences prefs, string artcc, LocalScenarioStore store) = SeedMirror(IdA);
        Window owner = ShowOwner();
        var window = new LoadScenarioWindow(prefs, null, _ => new FakeFilePickerService());
        try
        {
            Task<ScenarioLoadResult?> pending = DialogPresenter.ShowModalAsync<ScenarioLoadResult?>(window, owner);
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            window.FindControl<ListBox>("ArtccScenarioList")!.SelectedItem = Assert.Single(ArtccItems(window));
            Dispatcher.UIThread.RunJobs();
            Click(window, "LoadButton");

            ScenarioLoadResult? result = await pending;
            Assert.NotNull(result);
            Assert.Equal(store.GetMirrorPath(artcc, IdA), result!.FilePath);
            Assert.Null(result.ApiScenarioId);
        }
        finally
        {
            owner.Close();
        }
    }

    private static readonly Func<Task<ScenarioCatalogResponseDto>> NoHub = () =>
        Task.FromException<ScenarioCatalogResponseDto>(new InvalidOperationException("The hub is down."));

    private static readonly Func<string, IProgress<DownloadProgress>, CancellationToken, Task<DownloadReport>> NoDownload = (_, _, _) =>
        Task.FromException<DownloadReport>(new InvalidOperationException("No download in this test."));

    private static DownloadReport OneDownloaded(string artcc) => new(artcc, null, [IdA], [], [], [], [], [], []);

    private static LoadScenarioWindow WindowOver(UserPreferences prefs, ArtccTabSources sources) =>
        new(prefs, _ => new FakeFilePickerService(), sources);

    [AvaloniaFact]
    public async Task ArtccTab_ConnectionNotUp_ListsDownloadedScenarios()
    {
        (UserPreferences prefs, _, _) = SeedMirror(IdA);
        await using var connection = new ServerConnection();
        var window = new LoadScenarioWindow(prefs, connection, _ => new FakeFilePickerService());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            Assert.Equal([IdA], ArtccItems(window).Select(i => i.Id));
            Assert.Equal("Offline: 1 downloaded scenario", TextOf(window, "ArtccStatusText"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ArtccTab_HubThrows_ListsDownloadedScenarios()
    {
        (UserPreferences prefs, _, _) = SeedMirror(IdA, IdB);
        LoadScenarioWindow window = WindowOver(prefs, new ArtccTabSources(() => true, NoHub, NoDownload));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            Assert.Equal([IdA, IdB], ArtccItems(window).Select(i => i.Id).Order(StringComparer.Ordinal));
            Assert.Equal("Offline: 2 downloaded scenarios", TextOf(window, "ArtccStatusText"));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Summarize_ListsFailuresThenStoreProblems_ByFileName()
    {
        var report = new DownloadReport(
            "ZOA",
            null,
            [IdA],
            [],
            [],
            [],
            [],
            [new DownloadFailure(IdB, "Scenario B", "data-api answered HTTP 500 (Internal Server Error)")],
            [new StoreProblem("index.json", "corrupt; kept as index.json.corrupt-1 and rebuilt")]
        );

        (string status, List<string> notices) = LoadScenarioWindow.Summarize(report);

        Assert.Equal("1 downloaded, 0 updated, 1 failed", status);
        Assert.Equal(
            ["Scenario B: data-api answered HTTP 500 (Internal Server Error)", "index.json: corrupt; kept as index.json.corrupt-1 and rebuilt"],
            notices
        );
    }

    [AvaloniaFact]
    public async Task Download_RunThrows_ReEnablesTheButton_AndSaysWhy()
    {
        (UserPreferences prefs, _, _) = SeedMirror(IdA);
        var sources = new ArtccTabSources(
            () => false,
            NoHub,
            (_, _, _) => Task.FromException<DownloadReport>(new NullReferenceException("a summary was null"))
        );
        LoadScenarioWindow window = WindowOver(prefs, sources);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            Click(window, "DownloadButton");
            await window.DownloadDone;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Download failed: a summary was null", TextOf(window, "ArtccStatusText"));
            Assert.True(window.FindControl<Button>("DownloadButton")!.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Download_ClickedTwiceWhileRunning_RunsOnce()
    {
        (UserPreferences prefs, string artcc, _) = SeedMirror(IdA);
        var release = new TaskCompletionSource<DownloadReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        var sources = new ArtccTabSources(
            () => false,
            NoHub,
            (_, _, _) =>
            {
                Interlocked.Increment(ref runs);
                return release.Task;
            }
        );
        LoadScenarioWindow window = WindowOver(prefs, sources);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            Click(window, "DownloadButton");
            Task first = window.DownloadDone;
            Click(window, "DownloadButton");
            Assert.Same(first, window.DownloadDone);
            release.SetResult(OneDownloaded(artcc));
            await window.DownloadDone;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, runs);
            Assert.Equal("1 downloaded, 0 updated, 0 failed", TextOf(window, "ArtccStatusText"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Download_WindowClosed_CancelsTheRun()
    {
        (UserPreferences prefs, _, _) = SeedMirror(IdA);
        var sources = new ArtccTabSources(
            () => false,
            NoHub,
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("The delay only ends by a cancel.");
            }
        );
        LoadScenarioWindow window = WindowOver(prefs, sources);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.ArtccListReady;

        Click(window, "DownloadButton");
        window.Close();
        await window.DownloadDone;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Download cancelled.", TextOf(window, "ArtccStatusText"));
    }

    [AvaloniaFact]
    public async Task HubReply_DuringADownload_LeavesTheDownloadsStatus()
    {
        (UserPreferences prefs, string artcc, _) = SeedMirror();
        var hub = new TaskCompletionSource<ScenarioCatalogResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DownloadReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadScenarioWindow window = WindowOver(prefs, new ArtccTabSources(() => true, () => hub.Task, (_, _, _) => release.Task));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Click(window, "DownloadButton");

            hub.SetResult(new ScenarioCatalogResponseDto([new ScenarioSummaryDto(IdA, "ZOA-SFO Scenario", artcc, "SFO", null)], 0));
            await window.ArtccListReady;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Downloading…", TextOf(window, "ArtccStatusText"));
            Assert.Single(ArtccItems(window));

            release.SetResult(OneDownloaded(artcc));
            await window.DownloadDone;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1 downloaded, 0 updated, 0 failed", TextOf(window, "ArtccStatusText"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task HubFailsAfterADownloadFinished_KeepsTheDownloadsStatusAndNotices()
    {
        (UserPreferences prefs, string artcc, _) = SeedMirror(IdB);
        var hub = new TaskCompletionSource<ScenarioCatalogResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DownloadReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadScenarioWindow window = WindowOver(prefs, new ArtccTabSources(() => true, () => hub.Task, (_, _, _) => release.Task));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Click(window, "DownloadButton");

            release.SetResult(
                OneDownloaded(artcc) with
                {
                    Problems = [new StoreProblem("index.json", "corrupt; kept as index.json.corrupt-1 and rebuilt")],
                }
            );
            await window.DownloadDone;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1 downloaded, 0 updated, 0 failed", TextOf(window, "ArtccStatusText"));
            string notices = TextOf(window, "ArtccGateText");
            Assert.Equal("index.json: corrupt; kept as index.json.corrupt-1 and rebuilt", notices);

            hub.SetException(new HttpRequestException("The hub went away."));
            await window.ArtccListReady;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("1 downloaded, 0 updated, 0 failed", TextOf(window, "ArtccStatusText"));
            Assert.Equal(notices, TextOf(window, "ArtccGateText"));
            Assert.Equal([IdB], ArtccItems(window).Select(i => i.Id));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Download_Offline_ReloadAndDownloadNamingTheSameFile_ShowsTheLineOnce()
    {
        (UserPreferences prefs, string artcc, LocalScenarioStore store) = SeedMirror(IdA, IdB);
        File.WriteAllText(store.GetMirrorPath(artcc, IdA), "{ broken");
        StoreProblem planted = Assert.Single(store.Load(artcc).Problems);
        var report = new DownloadReport(artcc, null, [IdB], [], [], [], [], [], [planted]);
        var sources = new ArtccTabSources(() => false, NoHub, (_, _, _) => Task.FromResult(report));
        LoadScenarioWindow window = WindowOver(prefs, sources);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await window.ArtccListReady;

            Click(window, "DownloadButton");
            await window.DownloadDone;
            Dispatcher.UIThread.RunJobs();

            string[] lines = TextOf(window, "ArtccGateText").Split('\n');
            Assert.Single(lines, line => line == $"{planted.FileName}: {planted.Reason}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Copies a real ATCTrainer scenario JSON into a fresh temp folder and returns the copy's path.</summary>
    private string CopyExampleScenario()
    {
        string source = Path.Combine(FindRepoRoot(), "docs", "atctrainer-scenario-examples", "01H06NVK7VN8BS7MCDXHKJZ7MQ.json");
        string dir = Path.Combine(Path.GetTempPath(), $"yaat-load-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        string dest = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, dest);
        return dest;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No yaat.slnx above {AppContext.BaseDirectory}: cannot locate the example scenarios.");
    }
}
