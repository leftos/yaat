using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.Views;

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
