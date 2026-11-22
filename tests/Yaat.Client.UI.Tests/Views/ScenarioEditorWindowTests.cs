using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Tools › Scenario Editor…: the item needs no scenario and no connection, a click opens one window and a second click
/// brings it forward; the window lists a store's working copies on its Local tab and its server mirror on its Server
/// tab, offers the same commands on its toolbar and its context menu, and shows the ARTCC as a label for one ARTCC and
/// a selector for several. The store is a real one under the test process's app-data folder; the download never
/// finishes, so no test reaches the network.
/// </summary>
public class ScenarioEditorWindowTests
{
    private const string Zoa = "ZOA";
    private const string Zny = "ZNY";
    private const string MirrorId = "01HB2KQ4XG8T3W5Y9RZ0MNPC1D";
    private const string CopyId = "01HE9T5V1R7MZQ3KJ8W2NXD4GB";

    [AvaloniaFact]
    public void MenuItem_IsEnabledWithoutAScenarioOrConnection_AndOpensOneWindow()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        ScenarioEditorWindow? opened = null;

        // No Settings ARTCC, so the production wiring finds no ARTCC and starts no download.
        string savedArtcc = vm.Preferences.ArtccId;
        vm.Preferences.SetArtccId("");
        try
        {
            MenuItem item = main.FindControl<MenuItem>("ScenarioEditorMenuItem")!;
            Assert.True(item.IsEnabled);

            Click(item);
            opened = Assert.Single(OpenWindows.All.OfType<ScenarioEditorWindow>());
            Assert.False(opened.Browser.HasArtcc);

            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<ScenarioEditorWindow>()));
        }
        finally
        {
            opened?.Close();
            vm.Preferences.SetArtccId(savedArtcc);
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void RefusedClose_KeepsTheWindow_AndTheNextClickActivatesIt()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        ScenarioEditorWindow? opened = null;
        string savedArtcc = vm.Preferences.ArtccId;
        vm.Preferences.SetArtccId("");
        try
        {
            MenuItem item = main.FindControl<MenuItem>("ScenarioEditorMenuItem")!;
            Click(item);
            opened = Assert.Single(OpenWindows.All.OfType<ScenarioEditorWindow>());
            opened.CanCloseNow = () => false;

            opened.Close();
            MainWindowHost.Pump();
            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<ScenarioEditorWindow>()));
        }
        finally
        {
            if (opened is not null)
            {
                opened.CanCloseNow = () => true;
                opened.Close();
            }

            vm.Preferences.SetArtccId(savedArtcc);
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Closing_CancelsARunningResync()
    {
        var token = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<DownloadReport>();
        ScenarioEditorWindow window = NewWindow(
            SeededStore(),
            async (_, _, ct) =>
            {
                token.TrySetResult(ct);
                return await never.Task.WaitAsync(ct);
            },
            Zoa
        );
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await ShowAsync(window);
            CancellationToken ct = await token.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            using CancellationTokenRegistration registration = ct.Register(() => cancelled.TrySetResult());
            Assert.False(ct.IsCancellationRequested);

            window.Close();
            MainWindowHost.Pump();

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(ct.IsCancellationRequested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AFailedOpen_IsShownInTheStatus()
    {
        var preferences = new UserPreferences();
        var window = new ScenarioEditorWindow(
            preferences,
            prompts =>
                ScenarioBrowserServices.ForApp(preferences, prompts, new StubAccess([Zoa])) with
                {
                    Store = SeededStore(),
                    Download = NeverFinishes,
                    Post = _ => throw new InvalidOperationException("no UI thread"),
                },
            _ => new FakeFilePickerService()
        );
        try
        {
            await ShowAsync(window);

            Assert.Equal("Couldn't open the scenario list: no UI thread", window.Browser.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Tabs_ListTheStoresWorkingCopiesAndServerMirror()
    {
        LocalScenarioStore store = SeededStore();
        ScenarioEditorWindow window = NewWindow(store, Zoa);
        try
        {
            await ShowAsync(window);
            ScenarioBrowserView view = window.FindControl<ScenarioBrowserView>("BrowserView")!;

            ListBox local = view.FindControl<ListBox>("LocalList")!;
            ListBox server = view.FindControl<ListBox>("ServerList")!;
            Assert.Equal(["SMF_APP - Sacramento Arrivals"], local.Items.Cast<ScenarioBrowserRowViewModel>().Select(r => r.Name));
            Assert.Equal(["SFO_TWR - SFO Tower Rush"], server.Items.Cast<ScenarioBrowserRowViewModel>().Select(r => r.Name));
            TabControl tabs = view.FindControl<TabControl>("BrowserTabs")!;
            Assert.Equal(2, tabs.ItemCount);
            Assert.Equal("Local 1", HeaderText(view.FindControl<TabItem>("LocalTab")!));
            Assert.Equal("Server 1", HeaderText(view.FindControl<TabItem>("ServerTab")!));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ToolbarAndContextMenu_OfferTheSameCommands()
    {
        ScenarioEditorWindow window = NewWindow(SeededStore(), Zoa);
        try
        {
            await ShowAsync(window);
            ScenarioBrowserViewModel vm = window.Browser;
            ScenarioBrowserView view = window.FindControl<ScenarioBrowserView>("BrowserView")!;

            // The right-click menu, opened over the lists as a right-click opens it.
            TabControl tabs = view.FindControl<TabControl>("BrowserTabs")!;
            tabs.ContextMenu!.Open(tabs);
            MainWindowHost.Pump();
            (string Name, ICommand Command)[] commands =
            [
                ("New", vm.NewCommand),
                ("Duplicate", vm.DuplicateCommand),
                ("Delete", vm.DeleteCommand),
                ("Import", vm.ImportCommand),
                ("SaveCopy", vm.SaveCopyCommand),
                ("Refresh", vm.RefreshCommand),
            ];

            foreach ((string name, ICommand command) in commands)
            {
                Assert.Same(command, window.FindControl<Button>(name + "Button")!.Command);
                Assert.Same(command, view.FindControl<MenuItem>(name + "MenuItem")!.Command);
            }

            tabs.ContextMenu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task OneArtcc_ShowsALabel()
    {
        ScenarioEditorWindow window = NewWindow(SeededStore(), Zoa);
        try
        {
            await ShowAsync(window);

            TextBlock label = window.FindControl<TextBlock>("ArtccLabel")!;
            Assert.True(label.IsVisible);
            Assert.Equal(Zoa, label.Text);
            Assert.False(window.FindControl<ComboBox>("ArtccSelector")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TwoArtccs_ShowASelector()
    {
        ScenarioEditorWindow window = NewWindow(SeededStore(), Zoa, Zny);
        try
        {
            await ShowAsync(window);

            ComboBox selector = window.FindControl<ComboBox>("ArtccSelector")!;
            Assert.True(selector.IsVisible);
            Assert.Equal([Zoa, Zny], selector.Items.Cast<string>());
            Assert.False(window.FindControl<TextBlock>("ArtccLabel")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // A store under this process's app-data folder with one ZOA server scenario and one local-only working copy.
    private static LocalScenarioStore SeededStore()
    {
        var store = new LocalScenarioStore(YaatPaths.Combine("scenario-editor-tests", Guid.NewGuid().ToString("N")));
        store.ApplyServerBody(Zoa, MirrorId, $$"""{"id":"{{MirrorId}}","name":"SFO_TWR - SFO Tower Rush","artccId":"ZOA"}""");
        store.CreateLocalScenario(Zoa, $$"""{"id":"{{CopyId}}","name":"SMF_APP - Sacramento Arrivals","artccId":"ZOA"}""", CopyId);
        return store;
    }

    private static ScenarioEditorWindow NewWindow(LocalScenarioStore store, params string[] artccs) => NewWindow(store, NeverFinishes, artccs);

    private static ScenarioEditorWindow NewWindow(
        LocalScenarioStore store,
        Func<string, IProgress<DownloadProgress>, CancellationToken, Task<DownloadReport>> download,
        params string[] artccs
    )
    {
        var preferences = new UserPreferences();
        return new ScenarioEditorWindow(
            preferences,
            prompts => ScenarioBrowserServices.ForApp(preferences, prompts, new StubAccess(artccs)) with { Store = store, Download = download },
            _ => new FakeFilePickerService()
        );
    }

    // A tab header's texts, in order, joined by spaces: its label and its count.
    private static string HeaderText(TabItem tab) =>
        string.Join(" ", ((Control)tab.Header!).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static async Task<DownloadReport> NeverFinishes(string artccId, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct);
    }

    private static async Task ShowAsync(ScenarioEditorWindow window)
    {
        window.Show();
        MainWindowHost.Pump();
        await window.BrowserReady.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }

    private sealed class StubAccess(string[] artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artccs);
    }
}
