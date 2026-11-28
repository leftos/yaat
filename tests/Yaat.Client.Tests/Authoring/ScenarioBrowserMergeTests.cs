using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The browser's Merge server changes command over a real store: the request it hands the merge window (texts read
/// from the store, captions, the fresh-copy hook), what Apply writes through it, the open editor's unsaved edits asked
/// about before mine is read, and the editor reloading the merged copy. The window itself is the faked prompt.
/// </summary>
public sealed class ScenarioBrowserMergeTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;
    private const string S1 = "S1";

    private readonly ScenarioEditorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ResolveServerChanges_BuildsThreeWayRequest()
    {
        ServerChangedCopy();
        ScenarioBrowserViewModel browser = await BrowserOn(S1);

        await Resolve(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.NotNull(request.Texts.Base);
        Assert.Equal(_fx.Store.ReadBaseText(Zoa, S1), request.Texts.Base);
        Assert.Equal(_fx.Store.ReadWorkingCopyText(Zoa, S1), request.Texts.Mine);
        Assert.Equal(_fx.Store.ReadMirrorText(Zoa, S1), request.Texts.Theirs);
        Assert.Equal("Merge server changes: Sierra", request.Captions.Title);
        Assert.Equal("working copy", request.Captions.Mine);
        Assert.Equal("server version", request.Captions.Theirs);
        Assert.Equal("the server version this copy started from", request.Captions.Base);
        Assert.Equal(Zoa, request.ArtccId);
        Assert.Equal(S1, request.ScenarioId);
        Assert.NotNull(request.FreshCopy);
    }

    [Fact]
    public async Task ResolveServerChanges_NoBaseText_TwoWay()
    {
        ServerChangedCopy();
        File.Delete(Path.Combine(Path.GetDirectoryName(_fx.Store.GetWorkingCopyPath(Zoa, S1))!, S1 + ".base"));
        ScenarioBrowserViewModel browser = await BrowserOn(S1);

        await Resolve(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Null(request.Texts.Base);
        Assert.Equal("This copy has no saved base version, so every difference is yours to choose.", request.Captions.TwoWayNote);
    }

    [Fact]
    public async Task ResolveServerChanges_Cancelled_WritesNothing()
    {
        ServerChangedCopy();
        byte[] before = _fx.CopyBytes(S1);
        ScenarioBrowserViewModel browser = await BrowserOn(S1);

        await Resolve(browser);

        Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Equal(before, _fx.CopyBytes(S1));
        Assert.True(Entry(S1).ServerChanged);
        Assert.Contains(ScenarioBadge.ServerChanged, browser.LocalRows.Single(r => r.Id == S1).Badges);
    }

    [Fact]
    public async Task ResolveServerChanges_Applied_ClearsBadgeAndKeepsMergedText()
    {
        string theirs = ServerChangedCopy();
        string merged = ScenarioEditorFixture.ScenarioJson(S1, "Merged", "\"description\": \"changed on the server\"");
        _fx.Prompts.Merge = WriteThenAnswer(merged);
        ScenarioBrowserViewModel browser = await BrowserOn(S1);

        await Resolve(browser);

        Assert.Equal(merged, _fx.Store.ReadWorkingCopyText(Zoa, S1));
        WorkingCopyEntry copy = Entry(S1);
        Assert.False(copy.ServerChanged);
        Assert.Equal(LocalScenarioStore.ComputeVersion(theirs), copy.BaseVersion);
        Assert.DoesNotContain(ScenarioBadge.ServerChanged, browser.LocalRows.Single(r => r.Id == S1).Badges);
    }

    [Fact]
    public async Task ResolveServerChanges_EnabledOnlyForANonCorruptLocalRowWithTheServerChangedBadge()
    {
        ServerChangedCopy();
        _fx.AddCopy("L1", "Lima");
        _fx.Store.ApplyServerBody(Zoa, "C1", ScenarioEditorFixture.ScenarioJson("C1", "Charlie"));
        _fx.Store.CreateWorkingCopy(Zoa, "C1");
        _fx.Store.ApplyServerBody(Zoa, "C1", ScenarioEditorFixture.ScenarioJson("C1", "Charlie", "\"description\": \"new\""));
        File.WriteAllText(_fx.Store.GetWorkingCopyPath(Zoa, "C1"), "{ not json");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();

        Assert.False(browser.ResolveServerChangesCommand.CanExecute(null));
        SelectLocal(browser, "L1");
        Assert.False(browser.ResolveServerChangesCommand.CanExecute(null));
        SelectLocal(browser, "C1");
        Assert.True(browser.LocalRows.Single(r => r.Id == "C1").IsCorrupt);
        Assert.False(browser.ResolveServerChangesCommand.CanExecute(null));
        browser.SelectedTab = ScenarioBrowserTab.Server;
        browser.SelectedServerRow = browser.ServerRows.Single(r => r.Id == S1);
        Assert.False(browser.ResolveServerChangesCommand.CanExecute(null));
        SelectLocal(browser, S1);
        Assert.True(browser.ResolveServerChangesCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Cancel)]
    [InlineData(UnsavedChangesChoice.Save)]
    [InlineData(UnsavedChangesChoice.Discard)]
    public async Task OpenDirtyCopy_AsksAboutTheUnsavedEditsBeforeReadingMine(UnsavedChangesChoice answer)
    {
        ServerChangedCopy();
        string saved = _fx.Store.ReadWorkingCopyText(Zoa, S1);
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn(S1);
        Rename(editor, "Sierra edited");
        _fx.Prompts.Answers.Enqueue(answer);

        await Resolve(browser);

        Assert.Equal(["Sierra edited"], _fx.Prompts.UnsavedAsked);
        Assert.Equal([UnsavedChangesContext.Merge], _fx.Prompts.UnsavedContexts);
        if (answer == UnsavedChangesChoice.Cancel)
        {
            Assert.Empty(_fx.Prompts.MergeRequests);
            Assert.True(editor.IsDirty);
            return;
        }

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        if (answer == UnsavedChangesChoice.Save)
        {
            Assert.Contains("Sierra edited", request.Texts.Mine, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(saved, request.Texts.Mine);
        }

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AfterApply_OpenEditorReloads()
    {
        ServerChangedCopy();
        string merged = ScenarioEditorFixture.ScenarioJson(S1, "Merged");
        _fx.Prompts.Merge = WriteThenAnswer(merged);
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn(S1);

        await Resolve(browser);

        Assert.Equal("Merged", editor.Session?.Document.Scenario.Name);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task FreshCopy_MakesALocalOnlyCopyOfTheServerVersion_AndSelectsIt()
    {
        ServerChangedCopy();
        byte[] before = _fx.CopyBytes(S1);
        ScenarioBrowserViewModel browser = await BrowserOn(S1);
        await Resolve(browser);
        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);

        await _fx.Ui.Run(request.FreshCopy!());

        Assert.Equal(2, browser.LocalRows.Count);
        ScenarioBrowserRowViewModel fresh = browser.LocalRows.Single(r => r.Id != S1);
        Assert.True(LocalScenarioStore.IsValidId(fresh.Id));
        Assert.Null(Entry(fresh.Id).OriginId);
        JsonObject freshJson = JsonNode.Parse(_fx.Store.ReadWorkingCopyText(Zoa, fresh.Id))!.AsObject();
        Assert.Equal(fresh.Id, freshJson["id"]!.GetValue<string>());
        freshJson["id"] = S1;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(_fx.Store.ReadMirrorText(Zoa, S1)), freshJson));
        Assert.Equal(before, _fx.CopyBytes(S1));
        Assert.Equal(ScenarioBrowserTab.Local, browser.SelectedTab);
        Assert.Same(fresh, browser.SelectedLocalRow);
    }

    [Fact]
    public async Task FreshCopy_MirrorGone_ThrowsToTheWindow_NoNotice()
    {
        ServerChangedCopy();
        ScenarioBrowserViewModel browser = await BrowserOn(S1);
        await Resolve(browser);
        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        File.Delete(_fx.Store.GetMirrorPath(Zoa, S1));
        int noticesBefore = browser.Notices.Count;

        Exception? thrown = await Record.ExceptionAsync(() => _fx.Ui.Run(request.FreshCopy!()));

        Assert.IsType<FileNotFoundException>(thrown);
        Assert.Equal(noticesBefore, browser.Notices.Count);
        Assert.Equal([S1], browser.LocalRows.Select(r => r.Id));
        Assert.Single(_fx.Store.Load(Zoa).WorkingCopies);
    }

    [Fact]
    public async Task LocalOnlyCopy_ApplyMerge_WriteThrowsToTheWindow_WritesNothing()
    {
        ServerChangedCopy();
        string localText = "";
        Exception? thrown = null;
        _fx.Prompts.Merge = async request =>
        {
            // The copy lost its server origin while the window was open: Apply has no base to merge onto.
            _fx.Store.DeleteWorkingCopy(Zoa, S1);
            _fx.Store.CreateLocalScenario(Zoa, ScenarioEditorFixture.ScenarioJson(S1, "Local"), S1);
            localText = _fx.Store.ReadWorkingCopyText(Zoa, S1);
            thrown = await Record.ExceptionAsync(() => request.Write(ScenarioEditorFixture.ScenarioJson(S1, "Merged")));
            return null;
        };
        ScenarioBrowserViewModel browser = await BrowserOn(S1);
        int noticesBefore = browser.Notices.Count;

        await Resolve(browser);

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(localText, _fx.Store.ReadWorkingCopyText(Zoa, S1));
        Assert.Null(Entry(S1).OriginId);
        Assert.Equal(noticesBefore, browser.Notices.Count);
    }

    // S1 started from the server's "Sierra"; the server then changed its description, so the copy reads server-changed.
    private string ServerChangedCopy()
    {
        _fx.Store.ApplyServerBody(Zoa, S1, ScenarioEditorFixture.ScenarioJson(S1, "Sierra"));
        _fx.Store.CreateWorkingCopy(Zoa, S1);
        string theirs = ScenarioEditorFixture.ScenarioJson(S1, "Sierra", "\"description\": \"changed on the server\"");
        _fx.Store.ApplyServerBody(Zoa, S1, theirs);
        return theirs;
    }

    private async Task<ScenarioBrowserViewModel> BrowserOn(string id)
    {
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        SelectLocal(browser, id);
        return browser;
    }

    private static void SelectLocal(ScenarioBrowserViewModel browser, string id)
    {
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == id);
    }

    private Task Resolve(ScenarioBrowserViewModel browser) => _fx.Ui.Run(browser.ResolveServerChangesCommand.ExecuteAsync(null));

    private WorkingCopyEntry Entry(string id) => _fx.Store.Load(Zoa).WorkingCopies.Single(c => c.Id == id);

    // The window as Apply runs it: writes the merged text, then answers what it wrote.
    private static Func<ScenarioMergeRequest, Task<ScenarioMergeResult?>> WriteThenAnswer(string merged) =>
        async request =>
        {
            await request.Write(merged);
            return new ScenarioMergeResult(merged, []);
        };

    private static void Rename(ScenarioEditorViewModel editor, string name)
    {
        ScenarioGeneralViewModel general = editor.General ?? throw new InvalidOperationException("nothing is open");
        general.Name = name;
        general.CommitNameCommand.Execute(null);
    }
}
