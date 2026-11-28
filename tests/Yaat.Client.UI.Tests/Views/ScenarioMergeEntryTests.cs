using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MsBox.Avalonia.ViewModels;
using MsBox.Avalonia.Windows;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The three merge entries reach the real merge window through the Scenario Editor's own prompts, over a real store: Merge
/// server changes… on a server-changed copy (three-way; Apply clears the badge, the copy holds the merged text and the open
/// editor reloads it), the import clash's Compare… (two-way; Apply writes the working copy) and Compare with… over two copies
/// (two-way; Apply writes Mine). Cancel and the close box return null and write nothing. The window is given the merged-aircraft
/// services, so the aircraft edit button is enabled, and its pickers are loaded; Apply runs the editor's checks, so a merged
/// scenario with a command that does not parse shows the "Merged with problems" box.
/// </summary>
public class ScenarioMergeEntryTests
{
    private const string Zoa = "ZOA";
    private const string S1 = "01HMERGEENTRY0000000000001";
    private const string L2 = "01HMERGEENTRY0000000000002";
    private const string AircraftId = "01HMERGEENTRYAIRCRAFT00001";
    private const string OakId = "01HHWYG5C2DCFR09A7S5A0Q0KP";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [AvaloniaFact]
    public async Task MergeServerChanges_OpensTheRealWindow_AndApplyClearsTheBadgeWritesTheMergeAndReloadsTheEditor()
    {
        LocalScenarioStore store = ServerChangedStore(Scenario(S1, "Sierra", null, null), Scenario(S1, "Sierra", "changed on the server", null), S1);
        ScenarioEditorWindow window = NewWindow(store, new FakeFilePickerService());
        try
        {
            await ShowAsync(window);
            Select(window, S1);
            await window.Browser.OpenCommand.ExecuteAsync(null);
            await window.Editor.OpenTask.WaitAsync(Wait, TestContext.Current.CancellationToken);

            Task run = window.Browser.ResolveServerChangesCommand.ExecuteAsync(null);
            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);
            Assert.False(merge.ViewModel.IsTwoWay);
            Task<ScenarioMergeResult?> completion = merge.ViewModel.Completion;

            await ApplyAsync(merge);
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);
            await window.Editor.OpenTask.WaitAsync(Wait, TestContext.Current.CancellationToken);
            MainWindowHost.Pump();

            ScenarioMergeResult result = Assert.IsType<ScenarioMergeResult>(await completion);
            Assert.Equal(result.MergedJson, store.ReadWorkingCopyText(Zoa, S1));
            Assert.Contains("changed on the server", result.MergedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(ScenarioBadge.ServerChanged, window.Browser.LocalRows.Single(r => r.Id == S1).Badges);
            Assert.Contains("changed on the server", window.Editor.Session!.Document.Write(), StringComparison.Ordinal);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task ImportClash_CompareDefaultButton_OpensTheRealWindowTwoWay_AndApplyWritesTheWorkingCopy()
    {
        LocalScenarioStore store = NewStore();
        store.CreateLocalScenario(Zoa, Scenario(S1, "Sierra", null, null), S1);
        string path = Path.Combine(Path.GetTempPath(), $"merge-entry-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, Scenario(S1, "Sierra", "from the file", null), TestContext.Current.CancellationToken);
        var picker = new FakeFilePickerService();
        picker.QueueOpenFiles([path]);
        ScenarioEditorWindow window = NewWindow(store, picker);
        try
        {
            await ShowAsync(window);
            Task run = window.Browser.ImportCommand.ExecuteAsync(null);
            ImportConflictDialog clash = await OwnedAsync<ImportConflictDialog>(window);
            Button compare = clash.FindControl<Button>("CompareButton")!;
            Assert.True(compare.IsDefault);
            compare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);
            Assert.True(merge.ViewModel.IsTwoWay);
            merge.ViewModel.TakeTheirsForEverythingCommand.Execute(null);
            await ApplyAsync(merge);

            // After an applied Compare the clash is asked again; Skip ends the import.
            ImportConflictDialog again = await OwnedAsync<ImportConflictDialog>(window);
            again.FindControl<Button>("SkipButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

            Assert.Contains("from the file", store.ReadWorkingCopyText(Zoa, S1), StringComparison.Ordinal);
        }
        finally
        {
            CloseNow(window);
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task CompareWith_TwoCopies_OpensTheRealWindowTwoWay_AndApplyWritesMine()
    {
        LocalScenarioStore store = NewStore();
        store.CreateLocalScenario(Zoa, Scenario(S1, "Lima", null, null), S1);
        string theirs = Scenario(L2, "Mike", "the other copy", null);
        store.CreateLocalScenario(Zoa, theirs, L2);
        string theirsStored = store.ReadWorkingCopyText(Zoa, L2);
        ScenarioEditorWindow window = NewWindow(store, new FakeFilePickerService());
        try
        {
            await ShowAsync(window);
            Select(window, S1);
            Task run = window.Browser.CompareCommand.ExecuteAsync(null);
            ScenarioComparePickerWindow picker = await OwnedAsync<ScenarioComparePickerWindow>(window);
            ListBox theirsList = picker.FindControl<ListBox>("TheirsList")!;
            theirsList.SelectedItem = theirsList
                .ItemsSource!.Cast<ScenarioCompareSide>()
                .Single(s => (s.Kind == ScenarioCompareSideKind.WorkingCopy) && (s.Id == L2));
            MainWindowHost.Pump();
            picker.FindControl<Button>("CompareButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);
            Assert.True(merge.ViewModel.IsTwoWay);
            merge.ViewModel.TakeTheirsForEverythingCommand.Execute(null);
            await ApplyAsync(merge);
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

            JsonObject mine = JsonNode.Parse(store.ReadWorkingCopyText(Zoa, S1))!.AsObject();
            Assert.Equal(S1, mine["id"]!.GetValue<string>());
            Assert.Equal("the other copy", mine["description"]!.GetValue<string>());
            Assert.Equal(theirsStored, store.ReadWorkingCopyText(Zoa, L2));
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndTheCloseBox_ReturnNull_AndWriteNothing(bool closeBox)
    {
        Window owner = ShowOwner();
        var written = new List<string>();
        var request = new ScenarioMergeRequest(
            Zoa,
            S1,
            new ScenarioMergeTexts(
                Scenario(S1, "Sierra", null, null),
                Scenario(S1, "Sierra", null, null),
                Scenario(S1, "Sierra", "changed on the server", null)
            ),
            new ScenarioMergeCaptions("Merge server changes: Sierra", "working copy", "server version", "base", "note"),
            merged =>
            {
                written.Add(merged);
                return Task.CompletedTask;
            }
        );
        var prompts = new ScenarioBrowserPrompts(owner, new FakeFilePickerService(), new UserPreferences())
        {
            MergeServices = MergeServices(NewStore(), owner, Configs()),
        };
        try
        {
            Task<ScenarioMergeResult?> pending = prompts.ShowMergeAsync(request);
            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(owner);
            Dismiss(merge, closeBox);

            Assert.Null(await pending.WaitAsync(Wait, TestContext.Current.CancellationToken));
            Assert.Empty(written);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeServerChanges_CancelOrCloseBox_LeavesTheCopyAndTheBadge(bool closeBox)
    {
        LocalScenarioStore store = ServerChangedStore(Scenario(S1, "Sierra", null, null), Scenario(S1, "Sierra", "changed on the server", null), S1);
        string before = store.ReadWorkingCopyText(Zoa, S1);
        ScenarioEditorWindow window = NewWindow(store, new FakeFilePickerService());
        try
        {
            await ShowAsync(window);
            Select(window, S1);
            Task run = window.Browser.ResolveServerChangesCommand.ExecuteAsync(null);
            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);
            Dismiss(merge, closeBox);
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

            Assert.Equal(before, store.ReadWorkingCopyText(Zoa, S1));
            Assert.Contains(ScenarioBadge.ServerChanged, window.Browser.LocalRows.Single(r => r.Id == S1).Badges);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task TheWindow_HasTheAircraftServices_SoTheEditButtonIsEnabled_AndLoadsTheAirports()
    {
        JsonObject oak = Oak();
        JsonObject changed = oak.DeepClone().AsObject();
        JsonObject first = changed["aircraft"]![0]!.AsObject();
        first["spawnDelay"] = 999;
        string itemKey = first["id"]!.GetValue<string>();
        LocalScenarioStore store = ServerChangedStore(oak.ToJsonString(), changed.ToJsonString(), OakId);
        ScenarioEditorWindow window = NewWindow(store, new FakeFilePickerService());
        try
        {
            await ShowAsync(window);
            Select(window, OakId);
            Task run = window.Browser.ResolveServerChangesCommand.ExecuteAsync(null);
            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);
            Assert.NotNull(merge.AircraftServices);
            await UntilAsync(() => merge.ViewModel.Pickers.Airports is not null);
            Assert.NotEmpty(merge.ViewModel.Pickers.Airports!);

            merge.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
            MainWindowHost.Pump();
            ScenarioMergeAircraftLineViewModel line = Assert.Single(merge.ViewModel.AircraftLines, l => l.ItemKey == itemKey);
            merge.FindControl<ListBox>("AircraftList")!.SelectedItem = line;
            merge.UpdateLayout();
            MainWindowHost.Pump();
            Button edit = Assert.Single(
                merge.GetVisualDescendants().OfType<Button>(),
                b => b.Classes.Contains("aircraftEdit") && ReferenceEquals(b.DataContext, line)
            );
            Assert.True(edit.IsEnabled);

            Dismiss(merge, closeBox: false);
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task Apply_WithACommandThatDoesNotParse_ShowsTheMergedWithProblemsBox()
    {
        string theirs = Scenario(S1, "Sierra", "changed on the server", BadAircraft());
        LocalScenarioStore store = ServerChangedStore(Scenario(S1, "Sierra", null, null), theirs, S1);
        ScenarioEditorWindow window = NewWindow(store, new FakeFilePickerService());
        try
        {
            await ShowAsync(window);
            Select(window, S1);
            Task run = window.Browser.ResolveServerChangesCommand.ExecuteAsync(null);
            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);

            ClickApply(merge);
            MsBoxWindow box = await OwnedAsync<MsBoxWindow>(merge);
            Assert.Equal("Merged with problems", box.Title);
            string text = Assert.IsType<MsBoxStandardViewModel>(box.DataContext).ContentMessage;
            Assert.Contains("N123AB", text, StringComparison.Ordinal);
            Assert.Contains("does not parse", text, StringComparison.Ordinal);

            ClickOk(box);
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);
            Assert.Contains("XYZZY", store.ReadWorkingCopyText(Zoa, S1), StringComparison.Ordinal);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task Apply_OnTheOakMerge_WithGroundTaxiPresets_ShowsNoBox()
    {
        JsonObject oak = Oak();
        JsonObject changed = oak.DeepClone().AsObject();
        changed["aircraft"]![0]!["spawnDelay"] = 999;
        LocalScenarioStore store = ServerChangedStore(oak.ToJsonString(), changed.ToJsonString(), OakId);
        ScenarioEditorWindow window = NewWindow(store, new FakeFilePickerService());
        try
        {
            await ShowAsync(window);
            Select(window, OakId);
            Task run = window.Browser.ResolveServerChangesCommand.ExecuteAsync(null);
            ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(window);

            // The unfiltered checks over the merged text do find not-checked findings (the ground TAXI presets, with no
            // layouts to route them on), so Apply showing no box proves the filter dropped them rather than that there were
            // none to drop.
            string merged = merge.ViewModel.Plan.Apply(merge.ViewModel.Choices);
            var inputs = new ScenarioEditInputs(
                () => NavigationDatabase.InstanceOrNull is not null,
                merge.ViewModel.Pickers.CheckedPositionIds,
                null
            );
            Assert.Contains(
                ScenarioMergeViewModel.CheckMerged(merge.ViewModel.Request, merged, inputs),
                issue => issue.Severity == ScenarioEditIssueSeverity.NotChecked
            );

            ClickApply(merge);
            await UntilAsync(() => !merge.IsVisible || merge.OwnedWindows.OfType<MsBoxWindow>().Any());

            Assert.Empty(merge.OwnedWindows.OfType<MsBoxWindow>());
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(ScenarioBadge.ServerChanged, window.Browser.LocalRows.Single(r => r.Id == OakId).Badges);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task Apply_FlagsAnAtcPositionTheConfigDoesNotList_WhenEveryNeighbourIsRead()
    {
        const string unknown = "01HMERGEENTRYNOTAPOSITION1";
        Window owner = ShowOwner();
        try
        {
            (Task<ScenarioMergeResult?> pending, ScenarioMergeWindow merge, List<string> written) = await OpenDirectAsync(
                owner,
                WithAtc(unknown),
                Configs(new EveryArtccHandler(ZoaSnapshot()))
            );
            await UntilAsync(() => merge.ViewModel.Pickers.CheckedPositionIds is not null);

            ClickApply(merge);
            MsBoxWindow box = await OwnedAsync<MsBoxWindow>(merge);
            string text = Assert.IsType<MsBoxStandardViewModel>(box.DataContext).ContentMessage;
            Assert.Contains($"{unknown} is not in the ARTCC's config or its neighbours'", text, StringComparison.Ordinal);

            ClickOk(box);
            Assert.NotNull(await pending.WaitAsync(Wait, TestContext.Current.CancellationToken));
            Assert.Single(written);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Apply_DoesNotFlagAPositionTheConfigDoesNotList_WhileANeighbourIsUnread()
    {
        const string neighbours = "01HMERGEENTRYNEIGHBOURPOS1";
        Window owner = ShowOwner();
        try
        {
            (Task<ScenarioMergeResult?> pending, ScenarioMergeWindow merge, List<string> written) = await OpenDirectAsync(
                owner,
                WithAtc(neighbours),
                Configs()
            );
            await UntilAsync(() => merge.ViewModel.Pickers.Positions is not null);
            Assert.False(merge.ViewModel.Pickers.NeighboursRead);
            Assert.Null(merge.ViewModel.Pickers.CheckedPositionIds);

            await ApplyAsync(merge);

            ScenarioMergeResult result = Assert.IsType<ScenarioMergeResult>(await pending.WaitAsync(Wait, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(result.Warnings, w => w.Message.Contains(neighbours, StringComparison.Ordinal));
            Assert.Single(written);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheConfigAnsweringAfterTheWindowCloses_LeavesThePickersFreeText()
    {
        Window owner = ShowOwner();
        var handler = new HeldHandler(ZoaSnapshot());
        try
        {
            (Task<ScenarioMergeResult?> pending, ScenarioMergeWindow merge, _) = await OpenDirectAsync(owner, WithAtc("unused"), Configs(handler));
            await UntilAsync(() => handler.Asked);

            Dismiss(merge, closeBox: true);
            await UntilAsync(() => !merge.IsVisible);
            MainWindowHost.Pump();
            // The handler still holds its answer, so only the window's own finally awaiting the pickers' load keeps the
            // completion open: a pickers task started and forgotten would have completed it already.
            Assert.False(pending.IsCompleted);

            handler.Release();

            Assert.Null(await pending.WaitAsync(Wait, TestContext.Current.CancellationToken));
            Assert.Same(ScenarioMergePickers.FreeText, merge.ViewModel.Pickers);
        }
        finally
        {
            handler.Release();
            owner.Close();
        }
    }

    // The base and mine with no ATC; theirs adds one ATC row of the given position, so the merge takes it by itself.
    private static ScenarioMergeTexts WithAtc(string positionId)
    {
        JsonObject theirs = JsonNode.Parse(Scenario(S1, "Sierra", null, null))!.AsObject();
        theirs["atc"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "01HMERGEENTRYATC0000000001",
                ["artccId"] = Zoa,
                ["facilityId"] = "OAK",
                ["positionId"] = positionId,
            }
        );
        return new ScenarioMergeTexts(Scenario(S1, "Sierra", null, null), Scenario(S1, "Sierra", null, null), theirs.ToJsonString());
    }

    // The prompts' merge window over the texts, shown over the owner, with merged-aircraft services over the configs; the
    // texts Apply writes are recorded.
    private static async Task<(Task<ScenarioMergeResult?> Pending, ScenarioMergeWindow Merge, List<string> Written)> OpenDirectAsync(
        Window owner,
        ScenarioMergeTexts texts,
        ArtccConfigSource configs
    )
    {
        var written = new List<string>();
        var request = new ScenarioMergeRequest(
            Zoa,
            S1,
            texts,
            new ScenarioMergeCaptions("Merge server changes: Sierra", "working copy", "server version", "base", "note"),
            merged =>
            {
                written.Add(merged);
                return Task.CompletedTask;
            }
        );
        var prompts = new ScenarioBrowserPrompts(owner, new FakeFilePickerService(), new UserPreferences())
        {
            MergeServices = MergeServices(NewStore(), owner, configs),
        };
        Task<ScenarioMergeResult?> pending = prompts.ShowMergeAsync(request);
        ScenarioMergeWindow merge = await OwnedAsync<ScenarioMergeWindow>(owner);
        return (pending, merge, written);
    }

    private static Window ShowOwner()
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        MainWindowHost.Pump();
        return owner;
    }

    // A scenario with the given name, description (none when null) and aircraft (none when null), and no ATC.
    private static string Scenario(string id, string name, string? description, JsonArray? aircraft)
    {
        var scenario = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["artccId"] = Zoa,
            ["primaryAirportId"] = "OAK",
            ["aircraft"] = aircraft ?? [],
            ["atc"] = new JsonArray(),
            ["initializationTriggers"] = new JsonArray(),
            ["aircraftGenerators"] = new JsonArray(),
            ["flightStripConfigurations"] = new JsonArray(),
        };
        if (description is not null)
        {
            scenario["description"] = description;
        }

        return scenario.ToJsonString();
    }

    // One aircraft whose only preset command does not parse.
    private static JsonArray BadAircraft() =>
        new(
            new JsonObject
            {
                ["id"] = AircraftId,
                ["aircraftId"] = "N123AB",
                ["presetCommands"] = new JsonArray(new JsonObject { ["id"] = "01HMERGEENTRYPRESET0000001", ["command"] = "XYZZY" }),
            }
        );

    // The committed OAK scenario of the Sim tests' test data.
    private static JsonObject Oak() => JsonNode.Parse(File.ReadAllText(SimTestData("issue153-s2-oak-5-2-scenario.json")))!.AsObject();

    private static LocalScenarioStore NewStore() => new(YaatPaths.Combine("scenario-merge-entry-tests", Guid.NewGuid().ToString("N")));

    // A working copy started from the server's base text; the server then changed it to theirs, so it reads server-changed.
    private static LocalScenarioStore ServerChangedStore(string baseText, string theirs, string id)
    {
        LocalScenarioStore store = NewStore();
        store.ApplyServerBody(Zoa, id, baseText);
        store.CreateWorkingCopy(Zoa, id);
        store.ApplyServerBody(Zoa, id, theirs);
        return store;
    }

    // A Scenario Editor window over the store with its real prompts; data-api answers ZOA's config from the committed
    // snapshot and nothing else, and no airport has a ground layout.
    private static ScenarioEditorWindow NewWindow(LocalScenarioStore store, FakeFilePickerService picker)
    {
        var preferences = new UserPreferences();
        ArtccConfigSource configs = Configs();
        return new ScenarioEditorWindow(
            preferences,
            prompts =>
                ScenarioBrowserServices.ForApp(preferences, prompts, new StubAccess([Zoa])) with
                {
                    Store = store,
                    Download = NeverFinishes,
                    Configs = configs,
                    DownloadVideoMaps = (_, _, _) => Task.FromResult<VideoMapData?>(null),
                },
            _ => picker,
            NoLayouts,
            Task.CompletedTask
        );
    }

    // The merged-aircraft services over the store, as the Scenario Editor window builds them.
    private static ScenarioMergedAircraftServices MergeServices(LocalScenarioStore store, Window owner, ArtccConfigSource configs)
    {
        var preferences = new UserPreferences();
        ScenarioBrowserServices browser = ScenarioBrowserServices.ForApp(
            preferences,
            new ScenarioBrowserPrompts(owner, new FakeFilePickerService(), preferences),
            new StubAccess([Zoa])
        ) with
        {
            Store = store,
            Configs = configs,
        };
        return new ScenarioMergedAircraftServices(browser, NoLayouts, Task.CompletedTask);
    }

    private static ArtccConfigSource Configs() => Configs(new ZoaOnlyHandler(ZoaSnapshot()));

    private static string ZoaSnapshot() => File.ReadAllText(SimTestData("artcc-zoa-snapshot.json"));

    private static ArtccConfigSource Configs(HttpMessageHandler handler) =>
        new(new HttpClient(handler), YaatPaths.Combine("scenario-merge-entry-tests", Guid.NewGuid().ToString("N"), "artcc"), () => DateTime.UtcNow);

    private static Task<AirportGroundLayout?> NoLayouts(string airportId, CancellationToken ct) => Task.FromResult<AirportGroundLayout?>(null);

    private static async Task<DownloadReport> NeverFinishes(string artccId, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct);
    }

    private static async Task ShowAsync(ScenarioEditorWindow window)
    {
        window.Show();
        MainWindowHost.Pump();
        await window.BrowserReady.WaitAsync(Wait, TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
    }

    private static void Select(ScenarioEditorWindow window, string id)
    {
        window.Browser.SelectedTab = ScenarioBrowserTab.Local;
        window.Browser.SelectedLocalRow = window.Browser.LocalRows.Single(r => r.Id == id);
    }

    // The window of the given kind the owner shows, once it opens; the entries reach it through the store and the UI post.
    private static async Task<T> OwnedAsync<T>(Window owner)
        where T : Window
    {
        T? found = null;
        await UntilAsync(() => (found = owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible)) is not null);
        return found!;
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + Wait;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "hung: the window or state the test waits for never came");
            MainWindowHost.Pump();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        MainWindowHost.Pump();
    }

    private static void ClickApply(ScenarioMergeWindow merge)
    {
        Button apply = merge.FindControl<Button>("ApplyButton")!;
        Assert.True(apply.IsEnabled);
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MainWindowHost.Pump();
    }

    // Apply, answering OK when the checks' findings box shows; finishes once the window has closed.
    private static async Task ApplyAsync(ScenarioMergeWindow merge)
    {
        ClickApply(merge);
        await UntilAsync(() => !merge.IsVisible || merge.OwnedWindows.OfType<MsBoxWindow>().Any());
        if (merge.OwnedWindows.OfType<MsBoxWindow>().FirstOrDefault() is { } box)
        {
            ClickOk(box);
        }

        await UntilAsync(() => !merge.IsVisible);
    }

    private static void ClickOk(MsBoxWindow box)
    {
        box.UpdateLayout();
        MainWindowHost.Pump();
        Button ok = box.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && ((b.Content as string) == "OK"));
        Point center = ok.TranslatePoint(new Point(ok.Bounds.Width / 2, ok.Bounds.Height / 2), box)!.Value;
        box.MouseDown(center, MouseButton.Left);
        box.MouseUp(center, MouseButton.Left);
        MainWindowHost.Pump();
    }

    // Cancel (its button runs the bound command) or the close box.
    private static void Dismiss(ScenarioMergeWindow merge, bool closeBox)
    {
        if (closeBox)
        {
            merge.Close();
        }
        else
        {
            merge.FindControl<Button>("CancelButton")!.Command!.Execute(null);
        }

        MainWindowHost.Pump();
    }

    // Closes without the unsaved-changes question, which would show a real dialog and hold the window open.
    private static void CloseNow(ScenarioEditorWindow window)
    {
        foreach (Window owned in window.OwnedWindows.ToList())
        {
            owned.Close();
        }

        window.CanCloseNow = () => Task.FromResult(true);
        window.Close();
        MainWindowHost.Pump();
    }

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

    // data-api answers every ARTCC with the ZOA snapshot, so every neighbour's config reads.
    private sealed class EveryArtccHandler(string zoa) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zoa, Encoding.UTF8, "application/json") });
    }

    // data-api holds every answer until the test releases it, then answers ZOA with the snapshot and the rest as the network
    // being down; it ignores the token, so a cancelled load still gets its answer late.
    private sealed class HeldHandler(string zoa) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Asked { get; private set; }

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = true;
            await _release.Task.ConfigureAwait(false);
            return (request.RequestUri?.AbsolutePath.EndsWith("/" + Zoa, StringComparison.Ordinal) == true)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zoa, Encoding.UTF8, "application/json") }
                : throw new HttpRequestException("the network is down");
        }
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
