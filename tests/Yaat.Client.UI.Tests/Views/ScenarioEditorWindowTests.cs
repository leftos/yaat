using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
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
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Tools › Scenario Editor…: the item needs no scenario and no connection, a click opens one window and a second click
/// brings it forward; the window lists a store's working copies on its Local tab and its server mirror on its Server
/// tab, offers the same commands on its toolbar and its context menu, and shows the ARTCC as a label for one ARTCC and
/// a selector for several. A double-click on a row, or Enter on the selected row, opens it in the General tab, whose
/// fields and pickers show the copy and record one undo step per edit; Save (its button and Ctrl+S) writes it, Undo and
/// Redo follow the session, and Ctrl+S and closing take a field still being typed in. Closing asks about unsaved edits,
/// except on an application or OS shutdown, which never waits on the question. The store is a real one under the test
/// process's app-data folder; the download never finishes, so no test reaches the network.
/// </summary>
public class ScenarioEditorWindowTests
{
    private const string Zoa = "ZOA";
    private const string Zny = "ZNY";
    private const string MirrorId = "01HB2KQ4XG8T3W5Y9RZ0MNPC1D";
    private const string CopyId = "01HE9T5V1R7MZQ3KJ8W2NXD4GB";
    private const string OakId = "01HHWYG5C2DCFR09A7S5A0Q0KP";
    private const string OakName = "S2-OAK-5 (2) | Practical Exam Preparation/Advanced Concepts";

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
            opened.CanCloseNow = () => Task.FromResult(false);

            opened.Close();
            MainWindowHost.Pump();
            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<ScenarioEditorWindow>()));
        }
        finally
        {
            if (opened is not null)
            {
                opened.CanCloseNow = () => Task.FromResult(true);
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

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Closing_WithAPendingGuard_WaitsForItsAnswer(bool allowed)
    {
        ScenarioEditorWindow window = NewWindow(SeededStore(), NeverFinishes, Zoa);
        var answer = new TaskCompletionSource<bool>();
        int asked = 0;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await ShowAsync(window);
            window.CanCloseNow = () =>
            {
                asked++;
                return answer.Task;
            };

            window.Close();
            MainWindowHost.Pump();
            Assert.False(closed);

            answer.SetResult(allowed);
            MainWindowHost.Pump();

            Assert.Equal(allowed, closed);
            Assert.Equal(1, asked);
        }
        finally
        {
            window.CanCloseNow = () => Task.FromResult(true);
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

    [AvaloniaFact]
    public async Task OpeningACopy_ShowsTheGeneralView_WithItsName()
    {
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), new CountingPrompts());
        try
        {
            await ShowAsync(window);
            ScenarioGeneralView general = window.FindControl<ScenarioGeneralView>("GeneralView")!;
            Assert.False(window.FindControl<TabControl>("EditorTabs")!.IsVisible);
            Assert.True(window.FindControl<Panel>("PlaceholderPanel")!.IsVisible);

            await OpenOakAsync(window);

            Assert.False(window.Editor.IsDirty);
            Assert.False(window.Editor.Session!.CanUndo);
            Assert.True(window.FindControl<TabControl>("EditorTabs")!.IsVisible && general.IsEffectivelyVisible);
            Assert.False(window.FindControl<Panel>("PlaceholderPanel")!.IsVisible);
            Assert.Equal(OakName, general.FindControl<TextBox>("NameBox")!.Text);
            ComboBox studentPosition = general.FindControl<ComboBox>("StudentPositionPicker")!;
            Assert.Equal(window.Editor.Session!.Document.Scenario.StudentPositionId, (studentPosition.SelectedItem as ScenarioPickOption)?.Value);

            // The pickers whose option lists the ARTCC catalog filled after the open show the document's values too.
            global::Yaat.Sim.Scenarios.Scenario scenario = window.Editor.Session.Document.Scenario;
            ScenarioGeneralViewModel vm = window.Editor.General!;
            Assert.Equal(scenario.Atc[0].PositionId, ShownValue(RowPicker(general, "AtcList", 0, vm.AtcPositions[0].PositionOptions)));
            Assert.Equal(
                scenario.AircraftGenerators[0].AutoTrackConfiguration!.PositionId,
                ShownValue(RowPicker(general, "ArrivalGeneratorList", 0, vm.ArrivalGenerators[0].AutoTrackPositionOptions))
            );
            Control arrivalRow = general.FindControl<ItemsControl>("ArrivalGeneratorList")!.ContainerFromIndex(0)!;
            Button remove = arrivalRow.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("remove"));
            Assert.Same(vm.RemoveArrivalGeneratorCommand, remove.Command);
            global::Yaat.Sim.Scenarios.FlightStripConfiguration strip = scenario.FlightStripConfigurations[0];
            Assert.Equal(
                StripPlacementRowViewModel.BayValueOf(strip.FacilityId, strip.BayId),
                ShownValue(RowPicker(general, "StripList", 0, vm.StripPlacements[0].BayOptions))
            );
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task PickingAnOptionInARowsPicker_RecordsOneUndoStep()
    {
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), new CountingPrompts());
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);
            ScenarioGeneralView general = window.FindControl<ScenarioGeneralView>("GeneralView")!;
            ScenarioAtcRowViewModel row = window.Editor.General!.AtcPositions[0];
            ComboBox picker = RowPicker(general, "AtcList", 0, row.PositionOptions);
            ScenarioPickOption other = row.PositionOptions.First(o => (o.Value.Length > 0) && (o.Value != row.PositionId));

            picker.SelectedItem = other;
            MainWindowHost.Pump();

            ScenarioEditSession session = window.Editor.Session!;
            Assert.Equal(other.Value, session.Document.Scenario.Atc[0].PositionId);
            Assert.True(session.Undo());
            Assert.False(session.CanUndo);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task SingleClickingARow_OnlySelectsIt()
    {
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), new CountingPrompts());
        try
        {
            await ShowAsync(window);
            ListBoxItem row = OakRowContainer(window);
            Point at = row.TranslatePoint(new Point(20, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            MainWindowHost.Pump();

            Assert.False(window.Editor.IsOpen);
            Assert.Equal(OakId, window.Browser.SelectedLocalRow?.Id);
        }
        finally
        {
            CloseNow(window);
        }
    }

    // The double-tap is raised on the row itself: two timed presses would depend on the wall clock's double-tap window.
    [AvaloniaFact]
    public async Task DoubleClickingARow_OpensItInTheGeneralTab()
    {
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), new CountingPrompts());
        try
        {
            await ShowAsync(window);
            ListBoxItem row = OakRowContainer(window);

            // The browser view's double-tap handler reads only the event's Source, so the event carries no pointer event.
            row.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            await AwaitOpenedAsync(window);

            Assert.True(window.Editor.IsOpen);
            Assert.Equal(OakName, window.FindControl<ScenarioGeneralView>("GeneralView")!.FindControl<TextBox>("NameBox")!.Text);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task EnterOnASelectedRow_OpensIt()
    {
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), new CountingPrompts());
        try
        {
            await ShowAsync(window);
            window.Browser.SelectedTab = ScenarioBrowserTab.Local;
            window.Browser.SelectedLocalRow = window.Browser.LocalRows.Single(r => r.Id == OakId);
            MainWindowHost.Pump();
            Assert.False(window.Editor.IsOpen);

            OakRowContainer(window).Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await AwaitOpenedAsync(window);

            Assert.True(window.Editor.IsOpen);
            Assert.Equal(OakName, window.FindControl<ScenarioGeneralView>("GeneralView")!.FindControl<TextBox>("NameBox")!.Text);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task SaveButton_IsDisabledUntilAnEdit_AndCtrlS_SavesIt()
    {
        LocalScenarioStore store = OakStore();
        ScenarioEditorWindow window = NewEditorWindow(store, new CountingPrompts());
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);
            Button save = window.FindControl<Button>("SaveButton")!;
            Assert.False(save.IsEffectivelyEnabled);

            EditName(window, "OAK renamed");
            Assert.True(save.IsEffectivelyEnabled);

            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            MainWindowHost.Pump();
            await (window.Editor.SaveCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );
            MainWindowHost.Pump();

            JsonNode saved = JsonNode.Parse(File.ReadAllText(store.GetWorkingCopyPath(Zoa, OakId)))!;
            Assert.Equal("OAK renamed", (string?)saved["name"]);
            Assert.False(save.IsEffectivelyEnabled);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlS_SavesAFieldStillBeingTypedIn()
    {
        LocalScenarioStore store = OakStore();
        ScenarioEditorWindow window = NewEditorWindow(store, new CountingPrompts());
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);

            TypeName(window, "OAK typed");
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await AwaitSaveAsync(window);

            JsonNode saved = JsonNode.Parse(File.ReadAllText(store.GetWorkingCopyPath(Zoa, OakId)))!;
            Assert.Equal("OAK typed", (string?)saved["name"]);
            Assert.False(window.Editor.IsDirty);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlS_WithNothingEdited_WritesNothing()
    {
        LocalScenarioStore store = OakStore();
        ScenarioEditorWindow window = NewEditorWindow(store, new CountingPrompts());
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);
            string path = store.GetWorkingCopyPath(Zoa, OakId);
            string before = File.ReadAllText(path);
            DateTime written = File.GetLastWriteTimeUtc(path);

            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await AwaitSaveAsync(window);

            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            Assert.Null(window.Editor.SaveCommand.ExecutionTask);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Closing_AsksAboutUnsavedEdits_OnlyWhenThereAreSome_IncludingAFieldStillBeingTypedIn(bool typed)
    {
        var prompts = new CountingPrompts();
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), prompts);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);
            if (typed)
            {
                TypeName(window, "OAK typed");
            }

            window.Close();
            MainWindowHost.Pump();

            Assert.Equal(typed ? 1 : 0, prompts.UnsavedAsked);
            Assert.True(closed);
        }
        finally
        {
            if (!closed)
            {
                CloseNow(window);
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public async Task Shutdown_WithUnsavedEdits_ClosesWithoutAsking(WindowCloseReason reason)
    {
        var prompts = new CountingPrompts();
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), prompts);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);
            EditName(window, "OAK renamed");
            Assert.True(window.Editor.IsDirty);

            CloseFor(window, reason);
            MainWindowHost.Pump();

            Assert.Equal(0, prompts.UnsavedAsked);
            Assert.True(closed);
        }
        finally
        {
            if (!closed)
            {
                CloseNow(window);
            }
        }
    }

    [AvaloniaFact]
    public async Task UndoAndRedoButtons_FollowTheSession()
    {
        ScenarioEditorWindow window = NewEditorWindow(OakStore(), new CountingPrompts());
        try
        {
            await ShowAsync(window);
            await OpenOakAsync(window);
            Button undo = window.FindControl<Button>("UndoButton")!;
            Button redo = window.FindControl<Button>("RedoButton")!;
            TextBox name = window.FindControl<ScenarioGeneralView>("GeneralView")!.FindControl<TextBox>("NameBox")!;
            Assert.False(undo.IsEffectivelyEnabled);
            Assert.False(redo.IsEffectivelyEnabled);

            EditName(window, "OAK renamed");
            Assert.True(undo.IsEffectivelyEnabled);
            Assert.Equal("Edit name", ToolTip.GetTip(undo));
            Assert.False(redo.IsEffectivelyEnabled);

            Click(undo);
            Assert.False(undo.IsEffectivelyEnabled);
            Assert.True(redo.IsEffectivelyEnabled);
            Assert.Equal("Edit name", ToolTip.GetTip(redo));
            Assert.Equal(OakName, name.Text);

            Click(redo);
            Assert.True(undo.IsEffectivelyEnabled);
            Assert.Equal("Edit name", ToolTip.GetTip(undo));
            Assert.False(redo.IsEffectivelyEnabled);
            Assert.Equal("OAK renamed", name.Text);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplicationShutdown_WithAPendingGuard_ClosesTheWindow()
    {
        ScenarioEditorWindow window = NewWindow(SeededStore(), NeverFinishes, Zoa);
        var never = new TaskCompletionSource<bool>();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await ShowAsync(window);
            window.CanCloseNow = () => never.Task;

            CloseFor(window, WindowCloseReason.ApplicationShutdown);
            MainWindowHost.Pump();

            Assert.True(closed);
        }
        finally
        {
            if (!closed)
            {
                window.CanCloseNow = () => Task.FromResult(true);
                window.Close();
            }
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

    // A raised Click event runs no command (Button runs it from its own OnClick), so the bound command runs here.
    private static void Click(Button button)
    {
        button.Command!.Execute(button.CommandParameter);
        MainWindowHost.Pump();
    }

    // Closes without the unsaved-changes question, which would show a real dialog and hold the window open.
    private static void CloseNow(ScenarioEditorWindow window)
    {
        window.CanCloseNow = () => Task.FromResult(true);
        window.Close();
    }

    // A store holding the committed OAK scenario of the Sim tests' test data as a local working copy.
    private static LocalScenarioStore OakStore()
    {
        var store = new LocalScenarioStore(YaatPaths.Combine("scenario-editor-tests", Guid.NewGuid().ToString("N")));
        store.CreateLocalScenario(Zoa, File.ReadAllText(SimTestData("issue153-s2-oak-5-2-scenario.json")), OakId);
        return store;
    }

    // A window over the store whose data-api answers ZOA's config from the committed snapshot and nothing else; the
    // prompts replace the window's dialogs, so no test shows a real one.
    private static ScenarioEditorWindow NewEditorWindow(LocalScenarioStore store, IScenarioBrowserPrompts prompts)
    {
        var preferences = new UserPreferences();
        string snapshot = File.ReadAllText(SimTestData("artcc-zoa-snapshot.json"));
        var configs = new ArtccConfigSource(
            new HttpClient(new ZoaOnlyHandler(snapshot)),
            YaatPaths.Combine("scenario-editor-tests", Guid.NewGuid().ToString("N"), "artcc"),
            () => DateTime.UtcNow
        );
        return new ScenarioEditorWindow(
            preferences,
            _ =>
                ScenarioBrowserServices.ForApp(preferences, prompts, new StubAccess([Zoa])) with
                {
                    Store = store,
                    Download = NeverFinishes,
                    Configs = configs,
                },
            _ => new FakeFilePickerService()
        );
    }

    // Opens the OAK copy through the browser's Open, as a double-click does, and waits for its pickers.
    private static async Task OpenOakAsync(ScenarioEditorWindow window)
    {
        ScenarioBrowserViewModel browser = window.Browser;
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == OakId);
        await browser.OpenCommand.ExecuteAsync(null);
        await window.Editor.OpenTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await window.Editor.General!.PickersLoaded.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
    }

    // The picker in a General-view list's row whose options are the given list.
    private static ComboBox RowPicker(ScenarioGeneralView general, string list, int index, object options)
    {
        Control row = general.FindControl<ItemsControl>(list)!.ContainerFromIndex(index)!;
        return row.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, options));
    }

    // The value of the option a picker shows selected; null when it shows none.
    private static string? ShownValue(ComboBox picker) => (picker.SelectedItem as ScenarioPickOption)?.Value;

    // The OAK copy's row on the Local tab.
    private static ListBoxItem OakRowContainer(ScenarioEditorWindow window)
    {
        ListBox local = window.FindControl<ScenarioBrowserView>("BrowserView")!.FindControl<ListBox>("LocalList")!;
        int index = window.Browser.LocalRows.ToList().FindIndex(r => r.Id == OakId);
        return Assert.IsType<ListBoxItem>(local.ContainerFromIndex(index));
    }

    // Waits for the browser's Open the view ran, the editor's open and its pickers.
    private static async Task AwaitOpenedAsync(ScenarioEditorWindow window)
    {
        MainWindowHost.Pump();
        await (window.Browser.OpenCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        await window.Editor.OpenTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (window.Editor.General is { } general)
        {
            await general.PickersLoaded.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        MainWindowHost.Pump();
    }

    // Types a name into the General view's name field and commits it with Enter.
    private static void EditName(ScenarioEditorWindow window, string text)
    {
        TextBox name = window.FindControl<ScenarioGeneralView>("GeneralView")!.FindControl<TextBox>("NameBox")!;
        name.Focus();
        name.Text = text;
        MainWindowHost.Pump();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        MainWindowHost.Pump();
    }

    // Types a name into the General view's name field and leaves it focused, not committed.
    private static void TypeName(ScenarioEditorWindow window, string text)
    {
        TextBox name = window.FindControl<ScenarioGeneralView>("GeneralView")!.FindControl<TextBox>("NameBox")!;
        name.Focus();
        name.Text = text;
        MainWindowHost.Pump();
        Assert.False(window.Editor.IsDirty);
    }

    // Waits for the save a key press started, if any.
    private static async Task AwaitSaveAsync(ScenarioEditorWindow window)
    {
        MainWindowHost.Pump();
        await (window.Editor.SaveCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        MainWindowHost.Pump();
    }

    // Avalonia raises ApplicationShutdown only from the desktop lifetime's shutdown, which would end the test process;
    // that shutdown closes each window through Window.CloseCore, called here the same way.
    private static void CloseFor(Window window, WindowCloseReason reason) =>
        typeof(Window).GetMethod("CloseCore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [reason, true, false]);

    // A file of the Sim tests' committed test data, found from the test's output folder up.
    private static string SimTestData(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"{name} is not in tests/Yaat.Sim.Tests/TestData above {AppContext.BaseDirectory}");
    }

    private sealed class StubAccess(string[] artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artccs);
    }

    // The window's dialogs, counting the unsaved-changes question and answering it Discard; nothing else is asked.
    private sealed class CountingPrompts : IScenarioBrowserPrompts
    {
        public int UnsavedAsked { get; private set; }

        public Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> PickImportFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question) =>
            Task.FromException<ImportConflictAnswer>(new NotSupportedException("no import in these tests"));

        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName)
        {
            UnsavedAsked++;
            return Task.FromResult(UnsavedChangesChoice.Discard);
        }

        public Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName) => Task.FromResult(false);
    }

    // data-api answers ZOA with the snapshot and every other ARTCC (a neighbour) as the network being down.
    private sealed class ZoaOnlyHandler(string zoa) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.RequestUri?.AbsolutePath.EndsWith("/" + Zoa, StringComparison.Ordinal) == true)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zoa, Encoding.UTF8, "application/json") })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("the network is down"));
    }
}
