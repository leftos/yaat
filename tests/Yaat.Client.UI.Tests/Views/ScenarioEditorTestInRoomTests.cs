using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Test in Room from the Scenario Editor over a real <see cref="MainViewModel"/>: the toolbar button follows
/// <see cref="MainViewModel.CanLoadScenario"/> (no room, a load in flight) through the room link the main window makes,
/// the command asks in the editor before replacing a running scenario and hands the selected working copy's saved JSON and origin id to
/// <see cref="MainViewModel.LoadTestScenarioAsync"/>, and that load neither asks to replace the running scenario in the
/// main window nor enters Recent Scenarios. No load completes here (nothing is connected), so the Recent check runs at
/// the moment the load begins, through <c>RememberLoadedScenario</c>.
/// </summary>
public class ScenarioEditorTestInRoomTests
{
    private const string Zoa = "ZOA";
    private const string MirrorId = "01HB2KQ4XG8T3W5Y9RZ0MNPC1D";
    private const string MirrorName = "SFO_TWR - SFO Tower Rush";
    private const string CopyId = "01HE9T5V1R7MZQ3KJ8W2NXD4GB";
    private const string CopyName = "SMF_APP - Sacramento Arrivals";

    private const string PacedScenario = """
        {
          "aircraftGenerators": [
            { "id": "G1", "runway": "30", "intervalTime": 300 }
          ],
          "aircraft": [
            { "callsign": "A1", "startingConditions": { "type": "Parking", "parking": "A1" } }
          ]
        }
        """;

    [AvaloniaFact]
    public async Task Button_FollowsCanLoadScenario_AndStopsFollowingOnceUnlinked()
    {
        var room = new MainViewModel(new FakeFilePickerService());
        (ScenarioEditorWindow window, _, _) = NewWindow(room, SeededStore(withServerCopy: false));
        try
        {
            await ShowAsync(window);
            IDisposable link = ScenarioBrowserRoomLink.Follow(room, window.Browser);
            SelectLocal(window.Browser, CopyId);
            Button button = window.FindControl<Button>("TestInRoomButton")!;
            Assert.False(room.CanLoadScenario);
            Assert.False(button.IsEffectivelyEnabled);

            room.IsConnected = true;
            room.ActiveRoomId = "room-1";
            MainWindowHost.Pump();
            Assert.True(room.CanLoadScenario);
            Assert.True(button.IsEffectivelyEnabled);

            room.LoadOverlay.BeginLocal("other.json");
            MainWindowHost.Pump();
            Assert.True(room.IsRoomLoading);
            Assert.False(button.IsEffectivelyEnabled);

            room.LoadOverlay.ApplyRefusal();
            MainWindowHost.Pump();
            Assert.True(button.IsEffectivelyEnabled);

            link.Dispose();
            room.IsNonMentor = true;
            MainWindowHost.Pump();
            Assert.False(room.CanLoadScenario);
            Assert.True(button.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Execute_WithAScenarioRunning_AsksInTheEditor_ThenLoadsTheCopyWithoutTheMainWindowsQuestion()
    {
        MainViewModel room = MentorInRoom();
        room.ActiveScenarioId = "scenario-1";
        await room.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        LocalScenarioStore store = SeededStore(withServerCopy: true);
        (ScenarioEditorWindow window, YesPrompts prompts, List<(string, string, string?)> loads) = NewWindow(room, store);
        List<string> begun = BeginsOf(room);
        try
        {
            await ShowAsync(window);
            SelectLocal(window.Browser, MirrorId);
            Assert.True(window.Browser.TestInRoomCommand.CanExecute(null));

            await window.Browser.TestInRoomCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            MainWindowHost.Pump();

            Assert.Equal([MirrorName], prompts.ReplaceRunningAsked);
            Assert.False(room.ShowScenarioSwitchConfirmation);
            Assert.Equal([MirrorName], begun);
            Assert.Equal((store.ReadWorkingCopyText(Zoa, MirrorId), MirrorName, MirrorId), Assert.Single(loads));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LoadTestScenario_ClearsAnOrdinaryLoadsSwitchQuestion()
    {
        MainViewModel room = MentorInRoom();
        room.ActiveScenarioId = "scenario-1";
        await room.LoadScenarioFromJsonAsync(Scenario("O1", "Oscar"), "Oscar", "O1");
        Assert.True(room.ShowScenarioSwitchConfirmation);

        await room.LoadTestScenarioAsync(Scenario("T1", "Tango"), "Tango", "A1");

        Assert.False(room.ShowScenarioSwitchConfirmation);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task LoadTestScenario_CancelsAnOrdinaryLoadParkedAtTheSetupDialog()
    {
        // MainViewModel's own UserPreferences reads the per-test file this instance wrote ([PreferencesFilePerTest]).
        var stored = new UserPreferences();
        stored.SetSoloTrainingMode(true);
        MainViewModel room = MentorInRoom();
        await room.LoadScenarioFromJsonAsync(PacedScenario, "Paced", "P1");
        Assert.True(room.ShowScenarioSetup);

        // Solo mode off, so the test load itself needs no setup.
        room.Preferences.SetSoloTrainingMode(false);
        await room.LoadTestScenarioAsync(Scenario("T1", "Tango"), "Tango", "A1");

        Assert.False(room.ShowScenarioSetup);
        List<string> begun = BeginsOf(room);
        await room.ConfirmScenarioSetupCommand.ExecuteAsync(null);
        Assert.Empty(begun);
    }

    [AvaloniaFact]
    public async Task LoadTestScenario_WithAScenarioRunning_AsksNothing_AndNeverEntersRecent()
    {
        MainViewModel room = MentorInRoom();
        room.ActiveScenarioId = "scenario-1";
        await room.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        List<bool> rememberedAtBegin = RememberAtEachBegin(room, "A1");

        await room.LoadTestScenarioAsync(Scenario("T1", "Tango"), "Tango", "A1");

        Assert.False(room.ShowScenarioSwitchConfirmation);
        Assert.Equal([false], rememberedAtBegin);

        // The skip is for that load only: an ordinary load's result enters Recent as before.
        room.RememberLoadedScenario(Result("Tango"), "A1");
        Assert.Contains(room.Preferences.RecentScenarios, r => r.ApiId == "A1");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task LoadTestScenario_ThroughTheSetupDialog_NeverEntersRecent()
    {
        // MainViewModel's own UserPreferences reads the per-test file this instance wrote ([PreferencesFilePerTest]).
        var stored = new UserPreferences();
        stored.SetSoloTrainingMode(true);
        MainViewModel room = MentorInRoom();
        List<bool> rememberedAtBegin = RememberAtEachBegin(room, "A1");

        await room.LoadTestScenarioAsync(PacedScenario, "Paced", "A1");

        Assert.True(room.ShowScenarioSetup);
        await room.ConfirmScenarioSetupCommand.ExecuteAsync(null);

        Assert.Equal([false, false], rememberedAtBegin);
        room.RememberLoadedScenario(Result("Paced"), "A1");
        Assert.Contains(room.Preferences.RecentScenarios, r => r.ApiId == "A1");
    }

    [AvaloniaFact]
    public async Task OrdinaryLoad_WithAScenarioRunning_StillAsksInTheMainWindow()
    {
        MainViewModel room = MentorInRoom();
        room.ActiveScenarioId = "scenario-1";

        await room.LoadScenarioFromJsonAsync(Scenario("T1", "Tango"), "Tango", "A1");

        Assert.True(room.ShowScenarioSwitchConfirmation);
    }

    // Each time a load begins (the overlay goes in flight), offers it a result and records whether Recent took it.
    private static List<bool> RememberAtEachBegin(MainViewModel room, string apiId)
    {
        var remembered = new List<bool>();
        room.LoadOverlay.PropertyChanged += (_, e) =>
        {
            if ((e.PropertyName == nameof(LoadOverlayViewModel.IsLoadInFlight)) && room.LoadOverlay.IsLoadInFlight)
            {
                room.RememberLoadedScenario(Result("Begin"), apiId);
                remembered.Add(room.Preferences.RecentScenarios.Any(r => r.ApiId == apiId));
            }
        };
        return remembered;
    }

    // The title of each load that begins (the overlay goes in flight).
    private static List<string> BeginsOf(MainViewModel room)
    {
        var begun = new List<string>();
        room.LoadOverlay.PropertyChanged += (_, e) =>
        {
            if ((e.PropertyName == nameof(LoadOverlayViewModel.IsLoadInFlight)) && room.LoadOverlay.IsLoadInFlight)
            {
                begun.Add(room.ScenarioFilePath);
            }
        };
        return begun;
    }

    private static MainViewModel MentorInRoom() =>
        new(new FakeFilePickerService())
        {
            IsConnected = true,
            ActiveRoomId = "room-1",
            IsNonMentor = false,
        };

    private static string Scenario(string id, string name) => $$"""{"id":"{{id}}","name":"{{name}}","artccId":"ZOA"}""";

    private static LoadScenarioResultDto Result(string name) => new(true, name, "T1", 0, 0, true, 1, null, [], [], [], null, [], null);

    // The main window's wiring over a test store, with prompts that answer Yes to replacing the running scenario; the room
    // loader is the real one, wrapped to record what it is given.
    private static (ScenarioEditorWindow Window, YesPrompts Prompts, List<(string, string, string?)> Loads) NewWindow(
        MainViewModel room,
        LocalScenarioStore store
    )
    {
        var preferences = new UserPreferences();
        var yes = new YesPrompts();
        var calls = new List<(string, string, string?)>();
        var window = new ScenarioEditorWindow(
            preferences,
            prompts =>
            {
                ScenarioBrowserServices wired = (
                    ScenarioBrowserServices.ForApp(preferences, prompts, new StubAccess(Zoa)) with
                    {
                        Store = store,
                        Download = NeverFinishes,
                        Prompts = yes,
                    }
                ).WithRoom(room);
                Func<string, string, string?, Task> load = wired.LoadInRoom;
                return wired with
                {
                    LoadInRoom = (json, name, originId) =>
                    {
                        calls.Add((json, name, originId));
                        return load(json, name, originId);
                    },
                };
            },
            _ => new FakeFilePickerService(),
            NoLayouts,
            Task.CompletedTask
        );
        return (window, yes, calls);
    }

    // One local-only working copy, and with withServerCopy a server scenario with a working copy of it too.
    private static LocalScenarioStore SeededStore(bool withServerCopy)
    {
        var store = new LocalScenarioStore(YaatPaths.Combine("scenario-editor-tests", Guid.NewGuid().ToString("N")));
        store.CreateLocalScenario(Zoa, $$"""{"id":"{{CopyId}}","name":"{{CopyName}}","artccId":"ZOA"}""", CopyId);
        if (withServerCopy)
        {
            store.ApplyServerBody(Zoa, MirrorId, $$"""{"id":"{{MirrorId}}","name":"{{MirrorName}}","artccId":"ZOA"}""");
            store.CreateWorkingCopy(Zoa, MirrorId);
        }

        return store;
    }

    private static void SelectLocal(ScenarioBrowserViewModel browser, string id)
    {
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == id);
        MainWindowHost.Pump();
    }

    private static async Task ShowAsync(ScenarioEditorWindow window)
    {
        window.Show();
        MainWindowHost.Pump();
        await window.BrowserReady.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
    }

    private static Task<AirportGroundLayout?> NoLayouts(string airportId, CancellationToken ct) => Task.FromResult<AirportGroundLayout?>(null);

    private static async Task<DownloadReport> NeverFinishes(string artccId, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct);
    }

    // Answers Yes to replacing the running scenario and records it; nothing else is asked in these tests.
    private sealed class YesPrompts : IScenarioBrowserPrompts
    {
        public List<string> ReplaceRunningAsked { get; } = [];

        public Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName)
        {
            ReplaceRunningAsked.Add(scenarioName);
            return Task.FromResult(true);
        }

        public Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> PickImportFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<string?> PickExportPathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<string?> PickExportFolderAsync() => Task.FromResult<string?>(null);

        public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question) =>
            Task.FromResult(new ImportConflictAnswer(ImportIdChoice.Skip, false));

        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context) =>
            Task.FromResult(UnsavedChangesChoice.Cancel);

        public Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName) => Task.FromResult(false);
    }

    private sealed class StubAccess(params string[] artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artccs);
    }
}
