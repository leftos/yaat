using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The browser's Compare with… over a real store: the picker opens with the right-clicked row preselected, the editor's
/// MergeGuard settles an open copy's unsaved edits, the merge window opens over the pair, and Apply writes the working
/// copy (mine) under its stored id and raises WorkingCopyReplaced. With no working copy in the ARTCC it posts a notice
/// and opens nothing. The picker and the merge window are the faked prompts.
/// </summary>
public sealed class ScenarioBrowserCompareTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;

    private readonly ScenarioEditorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task NoWorkingCopyInTheArtcc_PostsTheNotice_AndOpensNoPicker()
    {
        _fx.Store.ApplyServerBody(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha"));
        ScenarioBrowserViewModel browser = await Browser();

        await Compare(browser);

        Assert.Contains("Compare needs a working copy on the Mine side: start one first.", browser.Notices);
        Assert.Empty(_fx.Prompts.ComparePickers);
        Assert.Empty(_fx.Prompts.MergeRequests);
    }

    [Fact]
    public async Task AnAppliedMerge_WritesMineUnderItsStoredId_AndRaisesWorkingCopyReplaced()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("K1", "Kilo");
        byte[] theirsBefore = _fx.CopyBytes("K1");
        _fx.Prompts.Compare = p => Pick(p, "L1", ScenarioCompareSideKind.WorkingCopy, "K1");
        _fx.Prompts.Merge = WriteThenAnswer(ScenarioEditorFixture.ScenarioJson("K1", "Merged"));
        ScenarioBrowserViewModel browser = await Browser();
        List<(string Artcc, string Id)> replaced = [];
        browser.WorkingCopyReplaced += (_, e) => replaced.Add((e.ArtccId, e.Id));

        await Compare(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Null(request.Texts.Base);
        Assert.Equal("L1", request.ScenarioId);
        Assert.Equal("Compare: Lima with Kilo", request.Captions.Title);
        Assert.Equal("working copy", request.Captions.Mine);
        Assert.Equal("working copy", request.Captions.Theirs);
        Assert.Equal("none", request.Captions.Base);
        Assert.Equal(ScenarioBrowserViewModel.CompareTwoWayNote, request.Captions.TwoWayNote);
        Assert.Null(request.FreshCopy);
        Assert.Equal("L1", _fx.CopyJsonString("L1", "id"));
        Assert.Equal("Merged", _fx.CopyJsonString("L1", "name"));
        Assert.Equal(theirsBefore, _fx.CopyBytes("K1"));
        Assert.Equal([(Zoa, "L1")], replaced);
        Assert.Equal(["K1", "L1"], _fx.Store.Load(Zoa).WorkingCopies.Select(c => c.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACopyWithItsServerScenario_IsThreeWay_TheirsIsTheServerVersion()
    {
        (string baseText, string mine, string theirs) = SeedServerChangedCopy();
        _fx.Prompts.Compare = p => Pick(p, "A1", ScenarioCompareSideKind.Server, "A1");
        ScenarioBrowserViewModel browser = await Browser();

        await Compare(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Equal(baseText, request.Texts.Base);
        Assert.Equal(mine, request.Texts.Mine);
        Assert.Equal(theirs, request.Texts.Theirs);
        Assert.Equal(3, new[] { baseText, mine, theirs }.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("server version", request.Captions.Theirs);
        Assert.Equal("the server version this copy started from", request.Captions.Base);
        Assert.Equal(ScenarioBrowserViewModel.MergeTwoWayNote, request.Captions.TwoWayNote);
        Assert.NotNull(request.FreshCopy);
        Assert.Equal("Compare: Alpha edited with Alpha v2", request.Captions.Title);
    }

    [Fact]
    public async Task ApplyOverACopyWithItsServerScenario_MovesTheBaseForward_AndClearsServerChanged()
    {
        (_, _, string theirs) = SeedServerChangedCopy();
        _fx.Prompts.Compare = p => Pick(p, "A1", ScenarioCompareSideKind.Server, "A1");
        _fx.Prompts.Merge = WriteThenAnswer(ScenarioEditorFixture.ScenarioJson("A1", "Merged"));
        ScenarioBrowserViewModel browser = await Browser();
        Assert.Contains(ScenarioBadge.ServerChanged, browser.LocalRows.Single(r => r.Id == "A1").Badges);
        List<string> replaced = [];
        browser.WorkingCopyReplaced += (_, e) => replaced.Add(e.Id);

        await Compare(browser);

        Assert.Equal("Merged", _fx.CopyJsonString("A1", "name"));
        Assert.Equal(theirs, _fx.Store.ReadBaseText(Zoa, "A1"));
        Assert.False(Assert.Single(_fx.Store.Load(Zoa).WorkingCopies).ServerChanged);
        Assert.DoesNotContain(ScenarioBadge.ServerChanged, browser.LocalRows.Single(r => r.Id == "A1").Badges);
        Assert.Equal(["A1"], replaced);
    }

    [Fact]
    public async Task AnOpenCopyWithUnsavedEditsAsTheirs_AsksTheGuardFirst_CancelOpensNothing()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("K1", "Kilo");
        _fx.Prompts.Compare = p => Pick(p, "L1", ScenarioCompareSideKind.WorkingCopy, "K1");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("K1");
        ScenarioGeneralViewModel general = editor.General ?? throw new InvalidOperationException("nothing is open");
        general.Name = "Kilo edited";
        general.CommitNameCommand.Execute(null);
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Cancel);

        await Compare(browser);

        Assert.Equal(["Kilo edited"], _fx.Prompts.UnsavedAsked);
        Assert.Equal([UnsavedChangesContext.Merge], _fx.Prompts.UnsavedContexts);
        Assert.Empty(_fx.Prompts.MergeRequests);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task AFileNamingAnotherArtcc_PostsTheComparedInNotice()
    {
        _fx.AddCopy("L1", "Lima");
        string fileText = "{\"id\": \"F1\", \"name\": \"Elsewhere\", \"artccId\": \"ZNY\"}";
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("elsewhere.json", fileText)];
        _fx.Prompts.Compare = p => new ScenarioComparePick(
            p.MineOptions.Single(s => s.Id == "L1"),
            p.TheirsOptions.Single(s => s.Kind == ScenarioCompareSideKind.File)
        );
        ScenarioBrowserViewModel browser = await Browser();

        await Compare(browser);

        Assert.Contains("elsewhere.json: its artccId names another ARTCC; compared in ZOA", browser.Notices);
        Assert.Equal(fileText, Assert.Single(_fx.Prompts.MergeRequests).Texts.Theirs);
    }

    [Fact]
    public async Task AFile_IsAskedForWithTheImportPicker_AndComparedTwoWay()
    {
        _fx.AddCopy("L1", "Lima");
        string fileText = ScenarioEditorFixture.ScenarioJson("F1", "From a file");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("other.json", fileText)];
        _fx.Prompts.Compare = p => new ScenarioComparePick(
            p.MineOptions.Single(s => s.Id == "L1"),
            p.TheirsOptions.Single(s => s.Kind == ScenarioCompareSideKind.File)
        );
        ScenarioBrowserViewModel browser = await Browser();

        await Compare(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Null(request.Texts.Base);
        Assert.Equal(fileText, request.Texts.Theirs);
        Assert.Equal("file", request.Captions.Theirs);
        Assert.Equal("Compare: Lima with other.json", request.Captions.Title);
    }

    [Fact]
    public async Task AnApplyOfAFileWithANullTimeOffset_RepairsItInTheStoredCopy()
    {
        _fx.AddCopy("L1", "Lima");
        string aircraft = "\"aircraft\": [{\"id\": \"ac1\", \"presetCommands\": [{\"id\": \"p1\", \"command\": \"FH 010\", \"timeOffset\": null}]}]";
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("other.json", ScenarioEditorFixture.ScenarioJson("F1", "From a file", aircraft))];
        _fx.Prompts.Compare = p => new ScenarioComparePick(
            p.MineOptions.Single(s => s.Id == "L1"),
            p.TheirsOptions.Single(s => s.Kind == ScenarioCompareSideKind.File)
        );
        _fx.Prompts.Merge = WriteThenAnswer(ScenarioEditorFixture.ScenarioJson("L1", "Lima merged", aircraft));
        ScenarioBrowserViewModel browser = await Browser();

        await Compare(browser);

        JsonNode stored = JsonNode.Parse(_fx.Store.ReadWorkingCopyText(Zoa, "L1"))!;
        Assert.Equal(0, stored["aircraft"]![0]!["presetCommands"]![0]!["timeOffset"]!.GetValue<int>());
    }

    [Fact]
    public async Task ACancelledPicker_WritesNothing_AndOpensNoMergeWindow()
    {
        _fx.AddCopy("L1", "Lima");
        byte[] before = _fx.CopyBytes("L1");
        _fx.Prompts.Compare = _ => null;
        ScenarioBrowserViewModel browser = await Browser();

        await Compare(browser);

        Assert.Single(_fx.Prompts.ComparePickers);
        Assert.Empty(_fx.Prompts.MergeRequests);
        Assert.Equal(before, _fx.CopyBytes("L1"));
    }

    [Fact]
    public async Task ACancelledMerge_WritesNothing()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("K1", "Kilo");
        byte[] before = _fx.CopyBytes("L1");
        _fx.Prompts.Compare = p => Pick(p, "L1", ScenarioCompareSideKind.WorkingCopy, "K1");
        ScenarioBrowserViewModel browser = await Browser();
        int replaced = 0;
        browser.WorkingCopyReplaced += (_, _) => replaced++;

        await Compare(browser);

        Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Equal(before, _fx.CopyBytes("L1"));
        Assert.Equal(0, replaced);
    }

    [Fact]
    public async Task AnOpenCopyWithUnsavedEditsAsMine_AsksTheGuardFirst_CancelOpensNothing()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("K1", "Kilo");
        _fx.Prompts.Compare = p => Pick(p, "L1", ScenarioCompareSideKind.WorkingCopy, "K1");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        ScenarioGeneralViewModel general = editor.General ?? throw new InvalidOperationException("nothing is open");
        general.Name = "Lima edited";
        general.CommitNameCommand.Execute(null);
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Cancel);

        await Compare(browser);

        Assert.Equal(["Lima edited"], _fx.Prompts.UnsavedAsked);
        Assert.Equal([UnsavedChangesContext.Merge], _fx.Prompts.UnsavedContexts);
        Assert.Empty(_fx.Prompts.MergeRequests);
        Assert.True(editor.IsDirty);
    }

    // Server A1, its working copy edited, then the server changed: the base, mine and theirs texts.
    private (string Base, string Mine, string Theirs) SeedServerChangedCopy()
    {
        _fx.Store.ApplyServerBody(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha"));
        _fx.Store.CreateWorkingCopy(Zoa, "A1");
        _fx.Store.SaveWorkingCopy(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha edited"));
        string baseText = _fx.Store.ReadMirrorText(Zoa, "A1");
        _fx.Store.ApplyServerBody(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha v2"));
        return (baseText, _fx.Store.ReadWorkingCopyText(Zoa, "A1"), _fx.Store.ReadMirrorText(Zoa, "A1"));
    }

    private async Task<ScenarioBrowserViewModel> Browser()
    {
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        return browser;
    }

    private Task Compare(ScenarioBrowserViewModel browser) => _fx.Ui.Run(browser.CompareCommand.ExecuteAsync(null));

    private static ScenarioComparePick Pick(
        ScenarioComparePickerViewModel picker,
        string mineId,
        ScenarioCompareSideKind theirsKind,
        string theirsId
    ) => new(picker.MineOptions.Single(s => s.Id == mineId), picker.TheirsOptions.Single(s => (s.Kind == theirsKind) && (s.Id == theirsId)));

    // The window as Apply runs it: writes the merged text, then answers what it wrote.
    private static Func<ScenarioMergeRequest, Task<ScenarioMergeResult?>> WriteThenAnswer(string merged) =>
        async request =>
        {
            await request.Write(merged);
            return new ScenarioMergeResult(merged, []);
        };
}
