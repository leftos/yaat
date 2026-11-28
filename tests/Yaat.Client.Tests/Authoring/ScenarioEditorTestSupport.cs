using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Authoring.Stamping;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The scenario editor tests' world: a real store in a temp folder, a browser and editor over it with faked prompts, a
/// queued post, and an ARTCC config source over a stubbed data-api. The preferences a test changes are put back.
/// </summary>
internal sealed class ScenarioEditorFixture : IDisposable
{
    public const string Zoa = "ZOA";

    public const string Zny = "ZNY";

    // A position of ZOA's own facility in the committed snapshot: OAK_14_CTR, Sector 14.
    public const string ZoaSector14Id = "01GEAT1S60WNVZ3VQXP7TSQF84";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scenario-editor-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly string _savedArtccId;
    private readonly string? _savedLastArtcc;

    public ScenarioEditorFixture()
    {
        Store = new LocalScenarioStore(Path.Combine(_root, "store"));
        _savedArtccId = Preferences.ArtccId;
        _savedLastArtcc = Preferences.ScenarioEditorLastArtcc;
    }

    public LocalScenarioStore Store { get; }

    public UserPreferences Preferences { get; } = new();

    public FakeEditorPrompts Prompts { get; } = new();

    public QueuedPost Ui { get; } = new();

    /// <summary>Gets or sets the ARTCCs the user may edit.</summary>
    public IReadOnlyList<string> Artccs { get; set; } = [Zoa];

    /// <summary>Gets the folder the editor's ARTCC config source keeps its saved copies in.</summary>
    public string ArtccCacheDir => Path.Combine(_root, "artcc");

    /// <summary>Gets or sets the body data-api answers for an ARTCC config; null answers as the network being down.</summary>
    public string? ConfigBody { get; set; }

    /// <summary>Gets the paths this fixture's config source asked data-api for.</summary>
    public List<string> ConfigRequests { get; } = [];

    /// <summary>A layout source with no map for any airport, so no test fetches one.</summary>
    public static Task<AirportGroundLayout?> NoLayouts(string airportId, CancellationToken ct) => Task.FromResult<AirportGroundLayout?>(null);

    /// <summary>Find Route over no source but a route history with no endpoint, so no test reaches the network.</summary>
    public static RouteFindingServices NoRouteFinding() =>
        new(new HttpClient(new StubArtccHandler(_ => null)), RouteFindingServices.NoEndpoint, () => []);

    /// <summary>A config source whose data-api is never reachable, over its own folder.</summary>
    public static ArtccConfigSource OfflineConfigs(string cacheRoot) =>
        new(new HttpClient(new StubArtccHandler(_ => null)), cacheRoot, () => DateTime.UtcNow);

    /// <summary>A video-map download that answers every request with an empty map, so no test reaches data-api.</summary>
    public static Func<string, VideoMapInfoDto, CancellationToken, Task<VideoMapData?>> EchoVideoMaps() =>
        (_, map, _) => Task.FromResult<VideoMapData?>(new VideoMapData { MapId = map.Id, Lines = [] });

    /// <summary>
    /// A ZOA config with one position, OAK_14_CTR (Sector 14), no neighbours, and a child TRACON whose STARS area lists
    /// SFO and OAK (OAK twice, once in lower case).
    /// </summary>
    public static string ZoaConfig(params string[] neighbourIds)
    {
        var position = new PositionConfig
        {
            Id = ZoaSector14Id,
            Name = "Sector 14",
            Callsign = "OAK_14_CTR",
            RadioName = "Oakland Center",
            Frequency = 127_800_000,
        };
        var tracon = new FacilityConfig
        {
            Id = "NCT",
            Type = "Tracon",
            Name = "NorCal TRACON",
            StarsConfiguration = new StarsConfig
            {
                Areas =
                [
                    new StarsAreaConfig
                    {
                        Id = "A1",
                        Name = "Area A",
                        UnderlyingAirports = ["SFO", "OAK", "oak"],
                    },
                ],
            },
        };
        var root = new ArtccConfigRoot
        {
            Id = Zoa,
            Facility = new FacilityConfig
            {
                Id = Zoa,
                Type = "Artcc",
                Name = "Oakland ARTCC",
                Positions = [position],
                ChildFacilities = [tracon],
                NeighboringFacilityIds = [.. neighbourIds],
            },
        };
        return JsonSerializer.Serialize(root);
    }

    public static string ScenarioJson(string id, string name, string extra = "") =>
        $"{{\"id\": \"{id}\", \"name\": \"{name}\", \"artccId\": \"{Zoa}\"{(extra.Length == 0 ? "" : ", " + extra)}}}";

    public void AddCopy(string id, string name, string extra = "") => Store.CreateLocalScenario(Zoa, ScenarioJson(id, name, extra), id);

    /// <summary>Writes a file for an import to pick, outside the store.</summary>
    public string WriteImportFile(string name, string text)
    {
        string dir = Path.Combine(_root, "files");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    public byte[] CopyBytes(string id) => File.ReadAllBytes(Store.GetWorkingCopyPath(Zoa, id));

    public string? CopyJsonString(string id, string property)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Store.GetWorkingCopyPath(Zoa, id)));
        return doc.RootElement.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;
    }

    public ScenarioBrowserServices Services() =>
        new(
            Preferences,
            Store,
            new FixedArtccs(() => Artccs),
            (artcc, _, _) => Task.FromResult(new DownloadReport(artcc, null, [], [], [], [], [], [], [])),
            Prompts,
            TimeProvider.System,
            new Random(7),
            Ui.Post,
            () => false,
            (_, _, _) => Task.CompletedTask,
            () => false,
            new ArtccConfigSource(new HttpClient(new StubArtccHandler(ZoaOnly)), ArtccCacheDir, () => DateTime.UtcNow),
            NoRouteFinding(),
            EchoVideoMaps(),
            new StampProfileStore(Path.Combine(_root, "stamp-profiles")),
            new RouteLibrary(Path.Combine(_root, "routes"))
        );

    /// <summary>A browser and an editor over it, the browser's first load and resync done.</summary>
    public async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> OpenEditor()
    {
        ScenarioBrowserServices services = Services();
        var browser = new ScenarioBrowserViewModel(services);
        ScenarioEditorViewModel editor = NewEditor(browser, services);
        _owned.Add(browser);
        await Ui.Run(browser.InitializeAsync());
        await Ui.Run(browser.ResyncTask);
        return (browser, editor);
    }

    /// <summary>Another editor over a browser; disposed with the fixture, before the browser.</summary>
    public ScenarioEditorViewModel NewEditor(ScenarioBrowserViewModel browser, ScenarioBrowserServices? services = null)
    {
        var editor = new ScenarioEditorViewModel(browser, services ?? Services(), NoLayouts, Task.CompletedTask);
        _owned.Insert(0, editor);
        return editor;
    }

    /// <summary>An editor with the working copy opened through the browser's Open.</summary>
    public async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> OpenEditorOn(string id)
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await OpenEditor();
        await OpenThrough(browser, editor, id);
        Assert.Equal(id, editor.Session?.ScenarioId);
        return (browser, editor);
    }

    /// <summary>Selects the working copy's row and runs the browser's Open, then the editor's open and its pickers' load.</summary>
    public async Task OpenThrough(ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor, string id)
    {
        await StartOpen(browser, id);
        await Ui.Run(editor.OpenTask);
        if (editor.General is { } general)
        {
            await Ui.Run(general.PickersLoaded);
        }
    }

    /// <summary>Selects the working copy's row and runs the browser's Open; the editor's open may still be pending.</summary>
    public async Task StartOpen(ScenarioBrowserViewModel browser, string id)
    {
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == id);
        await Ui.Run(browser.OpenCommand.ExecuteAsync(null));
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }

        Ui.Dispose();

        // The preferences file is shared by the test process: put back what these tests changed.
        if (Preferences.ArtccId != _savedArtccId)
        {
            Preferences.SetArtccId(_savedArtccId);
        }

        if (Preferences.ScenarioEditorLastArtcc != _savedLastArtcc)
        {
            Preferences.SetScenarioEditorLastArtcc(_savedLastArtcc);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // data-api answers ZOA with ConfigBody and every other ARTCC (a neighbour) as the network being down.
    private string? ZoaOnly(HttpRequestMessage request)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        ConfigRequests.Add(path);
        return path.EndsWith("/" + Zoa, StringComparison.Ordinal) ? ConfigBody : null;
    }

    private sealed class FixedArtccs(Func<IReadOnlyList<string>> artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult(artccs());
    }
}

/// <summary>The checkout the test assembly was built in, for the examples and fixtures read from it.</summary>
internal static class TestRepo
{
    /// <summary>The nearest folder above the test output that holds yaat.slnx.</summary>
    /// <param name="reads">What the caller reads from the checkout, ending the message when there is none.</param>
    /// <returns>The checkout's root folder.</returns>
    /// <exception cref="InvalidOperationException">No folder above the output holds yaat.slnx.</exception>
    public static string Root(string reads)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No yaat.slnx above {AppContext.BaseDirectory}: {reads}");
    }
}

/// <summary>Answers each data-api request with the body it is given for it, or fails it as a network down when there is none.</summary>
/// <param name="body">The body to answer a request with; null fails the request.</param>
internal sealed class StubArtccHandler(Func<HttpRequestMessage, string?> body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        body(request) is { } text
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") })
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("the network is down"));
}

/// <summary>
/// The editor's prompts: the unsaved-changes question answered by <see cref="Ask"/> when set, else from the queue
/// (Cancel when it is empty), the unexported-changes question answered by <see cref="AskUnexported"/> when set, else
/// from its queue (Leave when it is empty), and a fixed delete answer.
/// </summary>
internal sealed class FakeEditorPrompts : IScenarioBrowserPrompts
{
    public Queue<UnsavedChangesChoice> Answers { get; } = new();

    public List<string> UnsavedAsked { get; } = [];

    /// <summary>Gets what each unsaved-changes question was asked before: closing or exporting.</summary>
    public List<UnsavedChangesContext> UnsavedContexts { get; } = [];

    /// <summary>Gets or sets the path the export picker answers; null as the user cancelling it.</summary>
    public string? ExportPath { get; set; }

    /// <summary>Gets the file names the export picker was offered.</summary>
    public List<string> ExportSuggested { get; } = [];

    /// <summary>Gets or sets the folder the export-all picker answers; null as the user cancelling it.</summary>
    public string? ExportFolder { get; set; }

    public int ExportFolderAsked { get; private set; }

    /// <summary>Gets or sets the answer to the unsaved-changes question, for a pending or failing prompt.</summary>
    public Func<Task<UnsavedChangesChoice>>? Ask { get; set; }

    public bool ConfirmDelete { get; set; }

    /// <summary>Gets or sets the answer to the replace-open-copy question.</summary>
    public bool ConfirmReplace { get; set; }

    public List<string> ReplaceAsked { get; } = [];

    public IReadOnlyList<string> ImportFiles { get; set; } = [];

    /// <summary>Gets or sets the answer to every import id clash.</summary>
    public ImportIdChoice ImportChoice { get; set; } = ImportIdChoice.Skip;

    public Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName)
    {
        ReplaceAsked.Add(scenarioName);
        return Task.FromResult(ConfirmReplace);
    }

    public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context)
    {
        UnsavedAsked.Add(scenarioName);
        UnsavedContexts.Add(context);
        if (Ask is { } ask)
        {
            return ask();
        }

        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : UnsavedChangesChoice.Cancel);
    }

    public Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row) => Task.FromResult(ConfirmDelete);

    /// <summary>Gets or sets the answer to the replace-the-running-scenario question Test in Room asks.</summary>
    public bool ConfirmReplaceRunning { get; set; }

    public Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName) => Task.FromResult(ConfirmReplaceRunning);

    public Task<IReadOnlyList<string>> PickImportFilesAsync() => Task.FromResult(ImportFiles);

    public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

    public Task<string?> PickExportPathAsync(string suggestedFileName)
    {
        ExportSuggested.Add(suggestedFileName);
        return Task.FromResult(ExportPath);
    }

    public Task<string?> PickExportFolderAsync()
    {
        ExportFolderAsked++;
        return Task.FromResult(ExportFolder);
    }

    /// <summary>Gets the answers to the import id clashes, in order; <see cref="ImportChoice"/> once it is empty.</summary>
    public Queue<ImportConflictAnswer> ImportAnswers { get; } = new();

    /// <summary>Gets each import id clash asked.</summary>
    public List<ImportConflictQuestion> ConflictsAsked { get; } = [];

    public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question)
    {
        ConflictsAsked.Add(question);
        return Task.FromResult(ImportAnswers.Count > 0 ? ImportAnswers.Dequeue() : new ImportConflictAnswer(ImportChoice, false, false));
    }

    /// <summary>Gets the answers to the unexported-changes question, in order; Leave once it is empty.</summary>
    public Queue<UnexportedChangesChoice> UnexportedAnswers { get; } = new();

    /// <summary>Gets the names each unexported-changes question listed.</summary>
    public List<IReadOnlyList<string>> UnexportedAsked { get; } = [];

    /// <summary>Gets what each unexported-changes question was asked before: the window closing or the ARTCC switching.</summary>
    public List<UnexportedChangesContext> UnexportedContexts { get; } = [];

    /// <summary>Gets or sets the answer to the unexported-changes question, for a pending or failing prompt.</summary>
    public Func<Task<UnexportedChangesChoice>>? AskUnexported { get; set; }

    public Task<UnexportedChangesChoice> AskUnexportedChangesAsync(IReadOnlyList<string> names, UnexportedChangesContext context)
    {
        UnexportedAsked.Add(names);
        UnexportedContexts.Add(context);
        if (AskUnexported is { } ask)
        {
            return ask();
        }

        return Task.FromResult(UnexportedAnswers.Count > 0 ? UnexportedAnswers.Dequeue() : UnexportedChangesChoice.Leave);
    }

    /// <summary>Gets the answers to the combined unsaved-and-unexported question, in order; DiscardAndLeave once it is empty.</summary>
    public Queue<UnsavedAndUnexportedChoice> CombinedAnswers { get; } = new();

    /// <summary>Gets each combined question asked: the open copy's name and the names it listed.</summary>
    public List<(string OpenName, IReadOnlyList<string> Names)> CombinedAsked { get; } = [];

    /// <summary>Gets what each combined question was asked before: the window closing or the ARTCC switching.</summary>
    public List<UnexportedChangesContext> CombinedContexts { get; } = [];

    /// <summary>Gets or sets the answer to the combined question, for a pending or failing prompt.</summary>
    public Func<Task<UnsavedAndUnexportedChoice>>? AskCombined { get; set; }

    public Task<UnsavedAndUnexportedChoice> AskUnsavedAndUnexportedAsync(
        string openName,
        IReadOnlyList<string> names,
        UnexportedChangesContext context
    )
    {
        CombinedAsked.Add((openName, names));
        CombinedContexts.Add(context);
        if (AskCombined is { } ask)
        {
            return ask();
        }

        return Task.FromResult(CombinedAnswers.Count > 0 ? CombinedAnswers.Dequeue() : UnsavedAndUnexportedChoice.DiscardAndLeave);
    }

    /// <summary>Gets the answers the merge window gives, in order; null (cancelled) once it is empty.</summary>
    public Queue<ScenarioMergeResult?> MergeAnswers { get; } = new();

    /// <summary>Gets each request the merge window was opened over.</summary>
    public List<ScenarioMergeRequest> MergeRequests { get; } = [];

    /// <summary>Gets or sets the merge window's behaviour, for one that writes before it answers; else the queue answers.</summary>
    public Func<ScenarioMergeRequest, Task<ScenarioMergeResult?>>? Merge { get; set; }

    public Task<ScenarioMergeResult?> ShowMergeAsync(ScenarioMergeRequest request)
    {
        MergeRequests.Add(request);
        if (Merge is { } merge)
        {
            return merge(request);
        }

        return Task.FromResult(MergeAnswers.Count > 0 ? MergeAnswers.Dequeue() : null);
    }

    /// <summary>Gets each Compare picker opened, as the browser built it.</summary>
    public List<ScenarioComparePickerViewModel> ComparePickers { get; } = [];

    /// <summary>Gets or sets the Compare picker's answer; null (or unset) as the author cancelling it.</summary>
    public Func<ScenarioComparePickerViewModel, ScenarioComparePick?>? Compare { get; set; }

    public Task<ScenarioComparePick?> PickCompareAsync(ScenarioComparePickerViewModel picker)
    {
        ComparePickers.Add(picker);
        return Task.FromResult(Compare?.Invoke(picker));
    }
}

/// <summary>The UI thread as a queue: each test runs the posted actions when, and in the order, it chooses.</summary>
internal sealed class QueuedPost : IDisposable
{
    // Every wait's backstop: it fires only on a hang, so it is minutes, far past any load delay (load-proof-tests).
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly List<Action> _pending = [];
    private readonly SemaphoreSlim _posted = new(0);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Post(Action action)
    {
        lock (_gate)
        {
            _pending.Add(action);
        }

        _posted.Release();
    }

    public void RunAt(int index)
    {
        Action action;
        lock (_gate)
        {
            action = _pending[index];
            _pending.RemoveAt(index);
        }

        action();
    }

    public void Drain()
    {
        while (Count > 0)
        {
            RunAt(0);
        }
    }

    // Runs posted actions until the task completes.
    public async Task Run(Task task)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            Drain();
            if (task.IsCompleted)
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "Hung: the task never completed.");
            await Task.WhenAny(task, _posted.WaitAsync(50));
        }

        await task;
    }

    // Waits, running nothing, until at least count actions are queued.
    public async Task WaitForPending(int count)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Hung: {count} posted actions never arrived.");
            await _posted.WaitAsync(50);
        }
    }

    public void Dispose() => _posted.Dispose();
}
