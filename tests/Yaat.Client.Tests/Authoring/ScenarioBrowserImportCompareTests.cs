using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// Compare… in the import id-clash question over a real store: the merge window opens two-way over the scenario that
/// holds the id (its working copy, else the server version) and the file, Apply writes the merged text (onto the working
/// copy, or as the server scenario's new working copy), and the clash is asked again afterwards. A Compare answer is
/// never applied to the remaining files. The dialog and the merge window are the faked prompts.
/// </summary>
public sealed class ScenarioBrowserImportCompareTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;

    private static readonly ImportConflictAnswer Compare = new(ImportIdChoice.Skip, ApplyToRemaining: false, Compare: true);
    private static readonly ImportConflictAnswer KeepId = new(ImportIdChoice.KeepId, ApplyToRemaining: false, Compare: false);
    private static readonly ImportConflictAnswer Skip = new(ImportIdChoice.Skip, ApplyToRemaining: false, Compare: false);

    private readonly ScenarioEditorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task CompareThenKeepId_OverAWorkingCopy_MergesTwoWayThenAsksAgain()
    {
        _fx.AddCopy("L1", "Lima");
        string mine = _fx.Store.ReadWorkingCopyText(Zoa, "L1");
        string theirs = ScenarioEditorFixture.ScenarioJson("L1", "Lima", "\"description\": \"from the file\"");
        string merged = ScenarioEditorFixture.ScenarioJson("L1", "Lima merged");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("lima.json", theirs)];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(KeepId);
        string? afterApply = null;
        _fx.Prompts.Merge = async request =>
        {
            await request.Write(merged);
            afterApply = _fx.Store.ReadWorkingCopyText(Zoa, "L1");
            return new ScenarioMergeResult(merged, []);
        };
        ScenarioBrowserViewModel browser = await Browser();

        await Import(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Null(request.Texts.Base);
        Assert.Equal(mine, request.Texts.Mine);
        Assert.Equal(theirs, request.Texts.Theirs);
        Assert.Equal(Zoa, request.ArtccId);
        Assert.Equal("L1", request.ScenarioId);
        Assert.Equal("working copy", request.Captions.Mine);
        Assert.Equal(ScenarioBrowserViewModel.ImportCompareTwoWayNote, request.Captions.TwoWayNote);
        Assert.Equal([ScenarioIdConflict.WorkingCopy, ScenarioIdConflict.WorkingCopy], _fx.Prompts.ConflictsAsked.Select(q => q.Conflict));
        Assert.Equal(merged, afterApply);
        Assert.Equal(theirs, _fx.Store.ReadWorkingCopyText(Zoa, "L1"));
    }

    [Fact]
    public async Task CompareOnAServerOnlyId_MineIsTheServerVersion_ApplyMakesItsWorkingCopy()
    {
        string server = ScenarioEditorFixture.ScenarioJson("A1", "Alpha");
        _fx.Store.ApplyServerBody(Zoa, "A1", server);
        string theirs = ScenarioEditorFixture.ScenarioJson("A1", "Alpha", "\"description\": \"from the file\"");
        string merged = ScenarioEditorFixture.ScenarioJson("A1", "Alpha merged");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("alpha.json", theirs)];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        _fx.Prompts.Merge = async request =>
        {
            await request.Write(merged);
            return new ScenarioMergeResult(merged, []);
        };
        ScenarioBrowserViewModel browser = await Browser();

        await Import(browser);

        ScenarioMergeRequest request = Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Null(request.Texts.Base);
        Assert.Equal(_fx.Store.ReadMirrorText(Zoa, "A1"), request.Texts.Mine);
        Assert.Equal(theirs, request.Texts.Theirs);
        Assert.Equal([ScenarioIdConflict.Mirror, ScenarioIdConflict.Both], _fx.Prompts.ConflictsAsked.Select(q => q.Conflict));
        Assert.Equal(merged, _fx.Store.ReadWorkingCopyText(Zoa, "A1"));
        WorkingCopyEntry copy = Assert.Single(_fx.Store.Load(Zoa).WorkingCopies);
        Assert.Equal("A1", copy.Id);
        Assert.Equal("A1", copy.OriginId);
        Assert.Contains(browser.LocalRows, r => r.Id == "A1");
        Assert.Equal("A1", browser.SelectedRow?.Id);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Cancel)]
    [InlineData(UnsavedChangesChoice.Save)]
    public async Task OpenCopyWithUnsavedEdits_AsksAboutThemBeforeReadingMine(UnsavedChangesChoice answer)
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("lima.json", ScenarioEditorFixture.ScenarioJson("L1", "Lima from the file"))];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "Lima edited");
        _fx.Prompts.Answers.Enqueue(answer);

        await Import(browser);

        Assert.Equal(["Lima edited"], _fx.Prompts.UnsavedAsked);
        Assert.Equal([UnsavedChangesContext.Merge], _fx.Prompts.UnsavedContexts);
        Assert.Equal(2, _fx.Prompts.ConflictsAsked.Count);
        if (answer == UnsavedChangesChoice.Cancel)
        {
            Assert.Empty(_fx.Prompts.MergeRequests);
            Assert.True(editor.IsDirty);
            return;
        }

        Assert.Contains("Lima edited", Assert.Single(_fx.Prompts.MergeRequests).Texts.Mine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterApply_TheOpenEditorReloadsTheMergedCopy()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("lima.json", ScenarioEditorFixture.ScenarioJson("L1", "Lima from the file"))];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        _fx.Prompts.Merge = WriteThenAnswer(ScenarioEditorFixture.ScenarioJson("L1", "Merged"));
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");

        await Import(browser);

        Assert.Equal("Merged", editor.Session?.Document.Scenario.Name);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AFileIdInOtherLetterCase_UsesTheStoredId_AsksTheGuard_AndReloadsTheOpenEditor()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("lima.json", ScenarioEditorFixture.ScenarioJson("l1", "Lima from the file"))];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        _fx.Prompts.Merge = WriteThenAnswer(ScenarioEditorFixture.ScenarioJson("L1", "Merged"));
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "Lima edited");
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Save);

        await Import(browser);

        Assert.Equal(["Lima edited"], _fx.Prompts.UnsavedAsked);
        Assert.Equal("L1", Assert.Single(_fx.Prompts.MergeRequests).ScenarioId);
        Assert.Equal("Merged", editor.Session?.Document.Scenario.Name);
        Assert.False(editor.IsDirty);
        Assert.Equal("L1", Assert.Single(_fx.Store.Load(Zoa).WorkingCopies).Id);
    }

    [Fact]
    public async Task AMergeWithTheIdInOtherLetterCase_IsSavedUnderTheStoredId()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("lima.json", ScenarioEditorFixture.ScenarioJson("l1", "Lima from the file"))];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        _fx.Prompts.Merge = WriteThenAnswer(ScenarioEditorFixture.ScenarioJson("l1", "Merged"));
        ScenarioBrowserViewModel browser = await Browser();

        await Import(browser);

        Assert.Equal("L1", _fx.CopyJsonString("L1", "id"));
        Assert.Equal("Merged", _fx.CopyJsonString("L1", "name"));
        Assert.Equal("L1", Assert.Single(_fx.Store.Load(Zoa).WorkingCopies).Id);
    }

    [Fact]
    public async Task AServerCopyThatIsNotAScenario_ApplyThrowsNotWritten_WritesNothing()
    {
        _fx.Store.ApplyServerBody(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha"));
        File.WriteAllText(_fx.Store.GetMirrorPath(Zoa, "A1"), "{ not json");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("alpha.json", ScenarioEditorFixture.ScenarioJson("A1", "Alpha from the file"))];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        Exception? thrown = null;
        _fx.Prompts.Merge = async request =>
        {
            thrown = await Record.ExceptionAsync(() => request.Write(ScenarioEditorFixture.ScenarioJson("A1", "Merged")));
            return null;
        };
        ScenarioBrowserViewModel browser = await Browser();

        await Import(browser);

        Assert.Equal("{ not json", Assert.Single(_fx.Prompts.MergeRequests).Texts.Mine);
        Assert.IsType<InvalidOperationException>(thrown);
        Assert.StartsWith("Not written: ", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_fx.Store.Load(Zoa).WorkingCopies);
        Assert.Empty(browser.LocalRows);
    }

    [Fact]
    public async Task CompareCancelled_WritesNothing_AsksAgain_SkipImportsNothing()
    {
        _fx.AddCopy("L1", "Lima");
        byte[] before = _fx.CopyBytes("L1");
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("lima.json", ScenarioEditorFixture.ScenarioJson("L1", "Lima from the file"))];
        _fx.Prompts.ImportAnswers.Enqueue(Compare);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        ScenarioBrowserViewModel browser = await Browser();

        await Import(browser);

        Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Equal(2, _fx.Prompts.ConflictsAsked.Count);
        Assert.Equal(before, _fx.CopyBytes("L1"));
        Assert.Equal(["L1"], browser.LocalRows.Select(r => r.Id));
        Assert.Single(_fx.Store.Load(Zoa).WorkingCopies);
    }

    [Fact]
    public async Task ACompareAnswer_IsNeverAppliedToTheRemainingFiles()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Prompts.ImportFiles =
        [
            _fx.WriteImportFile("one.json", ScenarioEditorFixture.ScenarioJson("L1", "One")),
            _fx.WriteImportFile("two.json", ScenarioEditorFixture.ScenarioJson("L1", "Two")),
        ];
        _fx.Prompts.ImportAnswers.Enqueue(Compare with { ApplyToRemaining = true });
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        _fx.Prompts.ImportAnswers.Enqueue(Skip);
        ScenarioBrowserViewModel browser = await Browser();

        await Import(browser);

        Assert.Single(_fx.Prompts.MergeRequests);
        Assert.Equal(["One", "One", "Two"], _fx.Prompts.ConflictsAsked.Select(q => q.ScenarioName));
        Assert.Equal(["L1"], browser.LocalRows.Select(r => r.Id));
    }

    private async Task<ScenarioBrowserViewModel> Browser()
    {
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        return browser;
    }

    private Task Import(ScenarioBrowserViewModel browser) => _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));

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
