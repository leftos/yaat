using Xunit;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Scenario Editor's close and switch-ARTCC guard: the shown ARTCC's rows are re-read, then one question is asked.
/// With the open copy holding unsaved edits it is the combined unsaved-and-unexported question, listing the copies with
/// the Unexported badge and the open copy (corrupt ones left out); else, with copies marked, the unexported-changes
/// question (Export all, Leave, Cancel); with neither it answers at once without asking. Nothing is saved, discarded or
/// exported before the answer. A real store in a temp folder, faked prompts and a queued post, as the editor view
/// model's tests use.
/// </summary>
public sealed class ScenarioUnexportedGuardTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;
    private const string Zny = ScenarioEditorFixture.Zny;

    private readonly ScenarioEditorFixture _fx = new();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "yaat-unexported-guard-tests", Guid.NewGuid().ToString("N"));

    public ScenarioUnexportedGuardTests()
    {
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        _fx.Dispose();
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    [Fact]
    public async Task CanClose_WithOneUnexportedCopy_AsksOnceNamingIt()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        _fx.Prompts.UnexportedAnswers.Enqueue(UnexportedChangesChoice.Leave);

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal(["Lima"], Assert.Single(_fx.Prompts.UnexportedAsked));
        Assert.Equal([UnexportedChangesContext.Close], _fx.Prompts.UnexportedContexts);
        Assert.Empty(_fx.Prompts.CombinedAsked);
    }

    [Fact]
    public async Task CanClose_NothingUnexported_TrueWithoutAsking()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        _fx.Store.RecordExport(Zoa, "L1", DateTimeOffset.UtcNow);
        _fx.Store.RecordExport(Zoa, "L2", DateTimeOffset.UtcNow);
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();

        Task<bool> answer = browser.CanCloseAsync();

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.True(await answer);
        Assert.Empty(_fx.Prompts.UnexportedAsked);
    }

    [Fact]
    public async Task CanSwitchArtcc_Cancel_ReturnsFalse_SelectorSnapsBack()
    {
        _fx.Artccs = [Zny, Zoa];
        _fx.Preferences.SetArtccId(Zoa);
        _fx.AddCopy("L1", "Lima");
        _fx.Store.CreateLocalScenario(Zny, ScenarioEditorFixture.ScenarioJson("Z1", "Zulu"), "Z1");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        Assert.Equal(Zoa, browser.SelectedArtcc);
        string? lastPick = _fx.Preferences.ScenarioEditorLastArtcc;
        _fx.Prompts.UnexportedAnswers.Enqueue(UnexportedChangesChoice.Cancel);
        int snaps = 0;
        browser.PropertyChanged += (_, e) => snaps += (e.PropertyName == nameof(ScenarioBrowserViewModel.SelectedArtcc)) ? 1 : 0;

        browser.SelectedArtcc = Zny;
        await _fx.Ui.Run(browser.ArtccSwitch);

        Assert.Equal(1, snaps);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Assert.Equal(lastPick, _fx.Preferences.ScenarioEditorLastArtcc);
        Assert.Equal(["Lima"], Assert.Single(_fx.Prompts.UnexportedAsked));
        Assert.Equal([UnexportedChangesContext.SwitchArtcc], _fx.Prompts.UnexportedContexts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanClose_ExportAll_WritesEveryMarkedCopyThenCloses(bool folderPicked)
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        _fx.Prompts.UnexportedAnswers.Enqueue(UnexportedChangesChoice.ExportAll);
        _fx.Prompts.ExportFolder = folderPicked ? _out : null;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.Equal(folderPicked, await answer);
        Assert.Equal(1, _fx.Prompts.ExportFolderAsked);
        if (folderPicked)
        {
            Assert.Equal(["Lima-L1.json", "Mike-L2.json"], Directory.GetFiles(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(browser.LocalRows, r => r.HasUnexportedChanges);
        }
        else
        {
            Assert.Empty(Directory.GetFileSystemEntries(_out));
            Assert.All(browser.LocalRows, r => Assert.True(r.HasUnexportedChanges));
        }
    }

    [Fact]
    public async Task CanClose_ExportAll_OneWriteFails_StaysOpenNamingIt()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        Directory.CreateDirectory(Path.Combine(_out, "Mike-L2.json"));
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        _fx.Prompts.UnexportedAnswers.Enqueue(UnexportedChangesChoice.ExportAll);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.False(await answer);
        Assert.StartsWith("Could not export Mike: ", browser.StatusText);
        Assert.True(File.Exists(Path.Combine(_out, "Lima-L1.json")));
        Assert.False(browser.LocalRows.Single(r => r.Id == "L1").HasUnexportedChanges);
        Assert.True(browser.LocalRows.Single(r => r.Id == "L2").HasUnexportedChanges);
    }

    [Fact]
    public async Task CanClose_DirtyAndUnexported_Cancel_ChangesNothing()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLimaBesideMike();
        byte[] before = _fx.CopyBytes("L1");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.Cancel);

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.False(await answer);
        (string openName, IReadOnlyList<string> names) = Assert.Single(_fx.Prompts.CombinedAsked);
        Assert.Equal("LimaTwo", openName);
        Assert.Equal(["LimaTwo", "Mike"], names);
        Assert.Equal([UnexportedChangesContext.Close], _fx.Prompts.CombinedContexts);
        Assert.Empty(_fx.Prompts.UnsavedAsked);
        Assert.Empty(_fx.Prompts.UnexportedAsked);
        Assert.True(editor.IsDirty);
        Assert.Equal("LimaTwo", editor.General?.Name);
        Assert.Equal(before, _fx.CopyBytes("L1"));
        Assert.Equal(0, _fx.Prompts.ExportFolderAsked);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public async Task CanClose_DirtyAndUnexported_SaveAndExportAll_SavesExportsAndCloses()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLimaBesideMike();
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndExportAll);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal("LimaTwo", _fx.CopyJsonString("L1", "name"));
        Assert.False(editor.IsDirty);
        Assert.Equal(["LimaTwo-L1.json", "Mike-L2.json"], Directory.GetFiles(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(browser.LocalRows, r => r.HasUnexportedChanges);
    }

    [Fact]
    public async Task CanClose_DirtyAndUnexported_SaveAndExportAll_OneExportFails_StaysOpenNamingIt()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLimaBesideMike();
        Directory.CreateDirectory(Path.Combine(_out, "Mike-L2.json"));
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndExportAll);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.False(await answer);
        Assert.Equal("LimaTwo", _fx.CopyJsonString("L1", "name"));
        Assert.True(editor.IsOpen);
        Assert.StartsWith("Could not export Mike: ", browser.StatusText);
        Assert.True(browser.LocalRows.Single(r => r.Id == "L2").HasUnexportedChanges);
    }

    [Fact]
    public async Task CanClose_DirtyAndUnexported_SaveAndLeave_SavesWithoutExporting()
    {
        (ScenarioBrowserViewModel browser, _) = await DirtyLimaBesideMike();
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndLeave);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal("LimaTwo", _fx.CopyJsonString("L1", "name"));
        Assert.Equal(0, _fx.Prompts.ExportFolderAsked);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
        Assert.True(browser.LocalRows.Single(r => r.Id == "L1").HasUnexportedChanges);
    }

    [Fact]
    public async Task CanClose_DirtyAndUnexported_DiscardAndLeave_DropsTheEditsWithoutExporting()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLimaBesideMike();
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.DiscardAndLeave);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.False(editor.IsOpen);
        Assert.Equal("Lima", _fx.CopyJsonString("L1", "name"));
        Assert.Equal(0, _fx.Prompts.ExportFolderAsked);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public async Task CanClose_DirtyWithEveryCopyExported_StillAsksTheCombinedQuestion_ListingTheOpenCopy()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Store.RecordExport(Zoa, "L1", DateTimeOffset.UtcNow);
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "LimaTwo");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.Cancel);

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.False(await answer);
        Assert.Equal(["LimaTwo"], Assert.Single(_fx.Prompts.CombinedAsked).Names);
        Assert.Empty(_fx.Prompts.UnsavedAsked);
        Assert.Empty(_fx.Prompts.UnexportedAsked);
    }

    [Fact]
    public async Task CanSwitchArtcc_DirtyAndUnexported_OneCombinedQuestion_CancelSnapsBack()
    {
        _fx.Artccs = [Zny, Zoa];
        _fx.Preferences.SetArtccId(Zoa);
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "LimaTwo");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.Cancel);
        int snaps = 0;
        browser.PropertyChanged += (_, e) => snaps += (e.PropertyName == nameof(ScenarioBrowserViewModel.SelectedArtcc)) ? 1 : 0;

        browser.SelectedArtcc = Zny;
        await _fx.Ui.Run(browser.ArtccSwitch);

        Assert.Equal(1, snaps);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Assert.Equal([UnexportedChangesContext.SwitchArtcc], _fx.Prompts.CombinedContexts);
        Assert.Empty(_fx.Prompts.UnexportedAsked);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task CanSwitchArtcc_DirtyAndUnexported_SaveAndExportAll_SavesExportsAndSwitches()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await TwoArtccsWithDirtyLimaBesideMike();
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndExportAll);
        _fx.Prompts.ExportFolder = _out;

        browser.SelectedArtcc = Zny;
        await _fx.Ui.Run(browser.ArtccSwitch);
        await _fx.Ui.Run(browser.ResyncTask);

        Assert.Equal(Zny, browser.SelectedArtcc);
        Assert.Equal([UnexportedChangesContext.SwitchArtcc], _fx.Prompts.CombinedContexts);
        Assert.Equal("LimaTwo", _fx.CopyJsonString("L1", "name"));
        Assert.False(editor.IsDirty);
        Assert.Equal(["LimaTwo-L1.json", "Mike-L2.json"], Directory.GetFiles(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CanSwitchArtcc_DirtyAndUnexported_SaveAndLeave_SavesAndSwitchesWithoutExporting()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await TwoArtccsWithDirtyLimaBesideMike();
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndLeave);
        _fx.Prompts.ExportFolder = _out;

        browser.SelectedArtcc = Zny;
        await _fx.Ui.Run(browser.ArtccSwitch);
        await _fx.Ui.Run(browser.ResyncTask);

        Assert.Equal(Zny, browser.SelectedArtcc);
        Assert.Equal("LimaTwo", _fx.CopyJsonString("L1", "name"));
        Assert.False(editor.IsDirty);
        Assert.Equal(0, _fx.Prompts.ExportFolderAsked);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public async Task CanClose_SaveAndExportAll_ExportsTheOpenCopyOfAnotherArtcc()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await TwoArtccsWithDirtyLimaBesideMike();
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndLeave);
        browser.SelectedArtcc = Zny;
        await _fx.Ui.Run(browser.ArtccSwitch);
        await _fx.Ui.Run(browser.ResyncTask);
        Assert.Equal(Zny, browser.SelectedArtcc);
        Assert.True(editor.IsOpen);
        Rename(editor, "LimaThree");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndExportAll);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal(["LimaThree"], _fx.Prompts.CombinedAsked[1].Names);
        Assert.Equal("LimaThree", _fx.CopyJsonString("L1", "name"));
        Assert.Equal(["LimaThree-L1.json"], Directory.GetFiles(_out).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CanClose_SaveAndExportAll_SameNameAndIdInTwoArtccs_WritesOneFileForEach()
    {
        _fx.Artccs = [Zny, Zoa];
        _fx.Preferences.SetArtccId(Zoa);
        _fx.AddCopy("L1", "Lima");
        _fx.Store.CreateLocalScenario(Zny, ScenarioEditorFixture.ScenarioJson("L1", "LimaThree"), "L1");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "LimaTwo");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndLeave);
        browser.SelectedArtcc = Zny;
        await _fx.Ui.Run(browser.ArtccSwitch);
        await _fx.Ui.Run(browser.ResyncTask);
        Assert.Equal(Zny, browser.SelectedArtcc);
        Rename(editor, "LimaThree");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndExportAll);
        _fx.Prompts.ExportFolder = _out;

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal(
            [$"{Zny}-LimaThree-L1.json", $"{Zoa}-LimaThree-L1.json"],
            Directory.GetFiles(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal)
        );
        Assert.DoesNotContain(browser.LocalRows, r => r.HasUnexportedChanges);
        browser.SelectedArtcc = Zoa;
        await _fx.Ui.Run(browser.ArtccSwitch);
        await _fx.Ui.Run(browser.ResyncTask);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Assert.False(browser.LocalRows.Single(r => r.Id == "L1").HasUnexportedChanges);
    }

    [Fact]
    public async Task CanSwitchArtcc_PickedAgainWhileTheCombinedAnswerIsPending_AsksOnce_SelectorSnapsBack()
    {
        _fx.Artccs = [Zny, Zoa];
        _fx.Preferences.SetArtccId(Zoa);
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "LimaTwo");
        var pending = new TaskCompletionSource<UnsavedAndUnexportedChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fx.Prompts.AskCombined = () => pending.Task;
        browser.SelectedArtcc = Zny;
        Task firstSwitch = browser.ArtccSwitch;
        int snaps = 0;
        browser.PropertyChanged += (_, e) => snaps += (e.PropertyName == nameof(ScenarioBrowserViewModel.SelectedArtcc)) ? 1 : 0;

        browser.SelectedArtcc = Zny;

        Assert.Equal(1, snaps);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Assert.Single(_fx.Prompts.CombinedAsked);
        Assert.Same(firstSwitch, browser.ArtccSwitch);
        pending.SetResult(UnsavedAndUnexportedChoice.Cancel);
        await _fx.Ui.Run(firstSwitch);
        Assert.Single(_fx.Prompts.CombinedAsked);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task CanClose_CombinedPromptThrows_AnswersCancelWithANotice()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLimaBesideMike();
        _fx.Prompts.AskCombined = () => Task.FromException<UnsavedAndUnexportedChoice>(new InvalidOperationException("the box failed"));

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.False(await answer);
        Assert.Contains("Unexported changes: the box failed", browser.Notices);
        Assert.True(editor.IsDirty);
        Assert.Equal("Lima", _fx.CopyJsonString("L1", "name"));
    }

    [Fact]
    public async Task CanClose_CorruptCopy_IsNeverListed()
    {
        _fx.AddCopy("C1", "Charlie");
        File.WriteAllText(_fx.Store.GetWorkingCopyPath(Zoa, "C1"), "not json");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        ScenarioBrowserRowViewModel corrupt = browser.LocalRows.Single(r => r.Id == "C1");
        Assert.True(corrupt.IsCorrupt);

        Task<bool> answer = browser.CanCloseAsync();

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.True(await answer);
        Assert.Empty(_fx.Prompts.UnexportedAsked);
    }

    [Fact]
    public async Task CanClose_SixCopies_ListsFiveAndOneMore()
    {
        string[] names = ["Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"];
        for (int i = 0; i < names.Length; i++)
        {
            _fx.AddCopy($"N{i}", names[i]);
        }

        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta", "Echo", "and 1 more"], Assert.Single(_fx.Prompts.UnexportedAsked));
    }

    [Fact]
    public async Task CanClose_PromptThrows_AnswersCancel()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        _fx.Prompts.AskUnexported = () => Task.FromException<UnexportedChangesChoice>(new InvalidOperationException("the box failed"));

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.False(await answer);
        Assert.Single(_fx.Prompts.UnexportedAsked);
        Assert.Contains("Unexported changes: the box failed", browser.Notices);
    }

    [Fact]
    public async Task CanClose_RowsRereadFails_AnswersCancelWithANotice()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        browser.LocalRows.CollectionChanged += (_, _) => throw new IOException("the disk failed");

        Task<bool> answer = browser.CanCloseAsync();

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.False(await answer);
        Assert.Contains("Unexported changes: the disk failed", browser.Notices);
        Assert.Empty(_fx.Prompts.UnexportedAsked);
    }

    [Fact]
    public async Task CanClose_RowsReloadedBeforeAsking()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.Store.RecordExport(Zoa, "L1", DateTimeOffset.UtcNow);
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        _fx.AddCopy("L2", "Mike");
        Assert.DoesNotContain(browser.LocalRows, r => r.Id == "L2");

        Task<bool> answer = browser.CanCloseAsync();
        await _fx.Ui.Run(answer);

        Assert.True(await answer);
        Assert.Equal(["Mike"], Assert.Single(_fx.Prompts.UnexportedAsked));
    }

    private static void Rename(ScenarioEditorViewModel editor, string name)
    {
        ScenarioGeneralViewModel general = editor.General ?? throw new InvalidOperationException("nothing is open");
        general.Name = name;
        general.CommitNameCommand.Execute(null);
    }

    // ZOA and ZNY editable, ZOA shown, with L1 open and renamed "LimaTwo" (unsaved) beside L2 "Mike".
    private async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> TwoArtccsWithDirtyLimaBesideMike()
    {
        _fx.Artccs = [Zny, Zoa];
        _fx.Preferences.SetArtccId(Zoa);
        return await DirtyLimaBesideMike();
    }

    // L1 "Lima" open and renamed "LimaTwo" (unsaved), L2 "Mike" listed beside it; neither ever exported.
    private async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> DirtyLimaBesideMike()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "LimaTwo");
        return (browser, editor);
    }
}
