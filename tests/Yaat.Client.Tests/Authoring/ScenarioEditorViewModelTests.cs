using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Scenario Editor's editor view model over a real store in a temp folder and a real browser: a working copy opens
/// on the browser's Open, Save writes it and re-reads the browser's rows, Undo and Redo re-read the General tab, and
/// leaving a copy with unsaved edits (another open, the window closing, an ARTCC switch) asks first, re-checking after
/// every await that the copy it asked about is still the one open. Prompts are faked (some pending on a
/// TaskCompletionSource) and the post queues, so each test runs the UI work when it chooses.
/// </summary>
public sealed class ScenarioEditorViewModelTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;
    private const string Zny = ScenarioEditorFixture.Zny;

    private readonly ScenarioEditorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task OpenRequested_OpensTheCopy_ShowsItsName_WritesNothing()
    {
        _fx.AddCopy("L1", "Lima");
        byte[] before = _fx.CopyBytes("L1");

        (_, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");

        Assert.True(editor.IsOpen);
        Assert.Equal("Lima", editor.General?.Name);
        Assert.False(editor.IsDirty);
        Assert.Null(editor.OpenError);
        Assert.Equal(before, _fx.CopyBytes("L1"));
    }

    [Fact]
    public async Task OpenAnUnreadableCopy_SetsOpenError_OpensNothing_WritesNothing()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditor();
        string path = _fx.Store.GetWorkingCopyPath(Zoa, "L1");
        File.WriteAllText(path, "{ not json");

        await _fx.OpenThrough(browser, editor, "L1");

        Assert.False(editor.IsOpen);
        Assert.Null(editor.General);
        Assert.StartsWith("Couldn't open L1: ", editor.OpenError);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public async Task CommitNameThenSave_WritesTheName_ReloadsTheBrowserRow_Clean()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");

        Rename(editor, "Lima Two");
        Assert.True(editor.IsDirty);
        Assert.True(editor.SaveCommand.CanExecute(null));
        await _fx.Ui.Run(editor.SaveCommand.ExecuteAsync(null));

        Assert.Equal("Lima Two", _fx.CopyJsonString("L1", "name"));
        Assert.Equal("Lima Two", Assert.Single(browser.LocalRows).Name);
        Assert.Equal("L1", browser.SelectedLocalRow?.Id);
        Assert.Equal(ScenarioBrowserTab.Local, browser.SelectedTab);
        Assert.False(editor.IsDirty);
        Assert.Null(editor.SaveMessage);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_AfterANameCommit_RereadsTheName_RedoRestoresIt()
    {
        _fx.AddCopy("L1", "Lima");
        (_, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "Lima Two");

        Assert.Equal("Edit name", editor.UndoDescription);
        Assert.True(editor.UndoCommand.CanExecute(null));
        editor.UndoCommand.Execute(null);

        Assert.Equal("Lima", editor.General?.Name);
        Assert.False(editor.IsDirty);
        Assert.Equal("Edit name", editor.RedoDescription);
        Assert.True(editor.RedoCommand.CanExecute(null));
        editor.RedoCommand.Execute(null);

        Assert.Equal("Lima Two", editor.General?.Name);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task SaveWithNoEdits_IsDisabled_FileUnchanged()
    {
        _fx.AddCopy("L1", "Lima");
        byte[] before = _fx.CopyBytes("L1");
        (_, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");

        Assert.False(editor.SaveCommand.CanExecute(null));
        await _fx.Ui.Run(editor.SaveCommand.ExecuteAsync(null));

        Assert.Equal(before, _fx.CopyBytes("L1"));
    }

    [Fact]
    public async Task OpenAnotherWhileDirty_Cancel_KeepsTheFirstOpen()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Cancel);

        await _fx.OpenThrough(browser, editor, "L2");

        Assert.Equal(["Lima Two"], _fx.Prompts.UnsavedAsked);
        Assert.Equal("L1", editor.Session?.ScenarioId);
        Assert.Equal("Lima Two", editor.General?.Name);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task OpenAnotherWhileDirty_Discard_OpensTheSecond_FirstFileUnchanged()
    {
        byte[] first = [];
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers(() => first = _fx.CopyBytes("L1"));
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Discard);

        await _fx.OpenThrough(browser, editor, "L2");

        Assert.Single(_fx.Prompts.UnsavedAsked);
        Assert.Equal("L2", editor.Session?.ScenarioId);
        Assert.Equal("Mike", editor.General?.Name);
        Assert.Equal(first, _fx.CopyBytes("L1"));
    }

    [Fact]
    public async Task OpenAnotherWhileDirty_Save_WritesTheFirst_ThenOpensTheSecond_SelectionStaysOnTheSecond()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Save);

        await _fx.OpenThrough(browser, editor, "L2");

        Assert.Single(_fx.Prompts.UnsavedAsked);
        Assert.Equal("Lima Two", _fx.CopyJsonString("L1", "name"));
        Assert.Equal("L2", editor.Session?.ScenarioId);
        Assert.False(editor.IsDirty);
        Assert.Equal("L2", browser.SelectedLocalRow?.Id);
        Assert.Contains(browser.LocalRows, r => r.Name == "Lima Two");
    }

    [Fact]
    public async Task OpenRace_SaveAtThePrompt_ANewerOpenDuringTheReload_Wins()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        var answer = new TaskCompletionSource<UnsavedChangesChoice>();
        _fx.Prompts.Ask = () => answer.Task;

        await _fx.StartOpen(browser, "L2");
        Task openL2 = editor.OpenTask;
        Assert.False(openL2.IsCompleted);
        _fx.Prompts.Ask = null;
        answer.SetResult(UnsavedChangesChoice.Save);
        Assert.False(openL2.IsCompleted);
        Task openL3 = editor.OpenAsync(Zoa, "L3");
        await _fx.Ui.Run(openL2);
        await _fx.Ui.Run(openL3);

        Assert.Equal("L3", editor.Session?.ScenarioId);
        Assert.Equal("November", editor.General?.Name);
        Assert.Equal("Lima Two", _fx.CopyJsonString("L1", "name"));
        Assert.Single(_fx.Prompts.UnsavedAsked);
        Assert.Null(editor.OpenError);
    }

    [Fact]
    public async Task ReopeningTheOpenCopy_DoesNotAsk_KeepsItsEdits()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();

        await _fx.OpenThrough(browser, editor, "L1");

        Assert.Empty(_fx.Prompts.UnsavedAsked);
        Assert.True(editor.IsDirty);
        Assert.Equal("Lima Two", editor.General?.Name);
    }

    [Theory]
    [InlineData(UnsavedAndUnexportedChoice.SaveAndLeave, true, true)]
    [InlineData(UnsavedAndUnexportedChoice.DiscardAndLeave, true, true)]
    [InlineData(UnsavedAndUnexportedChoice.Cancel, false, true)]
    [InlineData(UnsavedAndUnexportedChoice.SaveAndLeave, true, false)]
    [InlineData(UnsavedAndUnexportedChoice.DiscardAndLeave, true, false)]
    [InlineData(UnsavedAndUnexportedChoice.Cancel, false, false)]
    public async Task CanCloseAndCanSwitch_WhileDirty_FollowThePrompt(UnsavedAndUnexportedChoice choice, bool expected, bool closing)
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "Lima Two");
        _fx.Prompts.CombinedAnswers.Enqueue(choice);

        Task<bool> answer = closing ? browser.CanCloseAsync() : browser.CanSwitchArtccAsync();
        await _fx.Ui.Run(answer);

        Assert.Equal(expected, await answer);
        Assert.Equal("Lima Two", Assert.Single(_fx.Prompts.CombinedAsked).OpenName);
        Assert.Equal(closing ? UnexportedChangesContext.Close : UnexportedChangesContext.SwitchArtcc, Assert.Single(_fx.Prompts.CombinedContexts));
        Assert.Empty(_fx.Prompts.UnsavedAsked);
        Assert.Equal(choice == UnsavedAndUnexportedChoice.SaveAndLeave ? "Lima Two" : "Lima", _fx.CopyJsonString("L1", "name"));
        Assert.Equal(choice != UnsavedAndUnexportedChoice.DiscardAndLeave, editor.IsOpen);
    }

    [Fact]
    public async Task CanCloseAndCanSwitch_NotDirty_TrueWithoutAsking()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditorOn("L1");

        Assert.True(await browser.CanCloseAsync());
        Assert.True(await browser.CanSwitchArtccAsync());
        Assert.Empty(_fx.Prompts.UnsavedAsked);
    }

    [Fact]
    public async Task APromptThatThrows_AnswersCancel()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _fx.Prompts.AskCombined = () => Task.FromException<UnsavedAndUnexportedChoice>(new InvalidOperationException("the box failed"));

        Assert.False(await browser.CanCloseAsync());

        Assert.True(editor.IsOpen);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task ARefusedSaveAtThePrompt_KeepsTheCopyOpen_WithTheSaveMessage()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _ = editor.Session!.Edit("Edit name", s => s.Name = "");
        _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.SaveAndLeave);

        Assert.False(await browser.CanCloseAsync());

        Assert.True(editor.IsOpen);
        Assert.False(string.IsNullOrEmpty(editor.SaveMessage));
        Assert.Equal("Lima", _fx.CopyJsonString("L1", "name"));
    }

    [Fact]
    public async Task ArtccSwitch_APendingPrompt_SwitchesOnceDiscardIsAnswered()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await TwoArtccsWithL1Dirty();
        var answer = new TaskCompletionSource<UnsavedAndUnexportedChoice>();
        _fx.Prompts.AskCombined = () => answer.Task;

        browser.SelectedArtcc = Zny;
        Task switching = browser.ArtccSwitch;
        Assert.False(switching.IsCompleted);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        answer.SetResult(UnsavedAndUnexportedChoice.DiscardAndLeave);
        await _fx.Ui.Run(switching);
        await _fx.Ui.Run(browser.ResyncTask);

        Assert.Equal(Zny, browser.SelectedArtcc);
        Assert.False(editor.IsOpen);
        Assert.Equal("Lima", _fx.CopyJsonString("L1", "name"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtccSwitch_ARefusingGuard_SnapsTheSelectorBack(bool pending)
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await TwoArtccsWithL1Dirty();
        string? lastPick = _fx.Preferences.ScenarioEditorLastArtcc;
        var answer = new TaskCompletionSource<UnsavedAndUnexportedChoice>();
        if (pending)
        {
            _fx.Prompts.AskCombined = () => answer.Task;
        }
        else
        {
            _fx.Prompts.CombinedAnswers.Enqueue(UnsavedAndUnexportedChoice.Cancel);
        }

        int snaps = 0;
        browser.PropertyChanged += (_, e) => snaps += (e.PropertyName == nameof(ScenarioBrowserViewModel.SelectedArtcc)) ? 1 : 0;
        browser.SelectedArtcc = Zny;
        answer.SetResult(UnsavedAndUnexportedChoice.Cancel);
        await _fx.Ui.Run(browser.ArtccSwitch);

        Assert.Equal(1, snaps);
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Assert.Equal(lastPick, _fx.Preferences.ScenarioEditorLastArtcc);
        Assert.True(editor.IsDirty);
        Assert.Single(_fx.Prompts.CombinedAsked);
    }

    [Fact]
    public async Task DeletingTheOpenCopy_ClosesTheSession_NoPrompt_FileNotRecreated()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "Lima Two");
        _fx.Prompts.ConfirmDelete = true;

        await _fx.Ui.Run(browser.DeleteCommand.ExecuteAsync(null));

        Assert.False(editor.IsOpen);
        Assert.Null(editor.General);
        Assert.False(editor.IsDirty);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Empty(_fx.Prompts.UnsavedAsked);
        Assert.False(File.Exists(_fx.Store.GetWorkingCopyPath(Zoa, "L1")));
        Assert.True(await browser.CanCloseAsync());
    }

    [Fact]
    public async Task DeletingAnotherCopy_LeavesTheOpenOne()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _fx.Prompts.ConfirmDelete = true;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == "L2");

        await _fx.Ui.Run(browser.DeleteCommand.ExecuteAsync(null));

        Assert.False(File.Exists(_fx.Store.GetWorkingCopyPath(Zoa, "L2")));
        Assert.Equal("L1", editor.Session?.ScenarioId);
        Assert.True(editor.IsDirty);
        Assert.Empty(_fx.Prompts.UnsavedAsked);
    }

    [Fact]
    public async Task ImportKeepingTheId_OverTheCleanOpenCopy_ReloadsIt_WithoutAsking()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        ImportOver("L1", "Lima Imported");

        await _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));

        Assert.Empty(_fx.Prompts.ReplaceAsked);
        Assert.Equal("L1", editor.Session?.ScenarioId);
        Assert.Equal("Lima Imported", editor.General?.Name);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task ImportKeepingTheId_OverTheDirtyOpenCopy_Replace_ReloadsTheImport()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _fx.Prompts.ConfirmReplace = true;
        ImportOver("L1", "Lima Imported");

        await _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));

        Assert.Equal(["Lima Two"], _fx.Prompts.ReplaceAsked);
        Assert.Equal("Lima Imported", _fx.CopyJsonString("L1", "name"));
        Assert.Equal("Lima Imported", editor.General?.Name);
        Assert.False(editor.IsDirty);
        Assert.False(editor.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ImportKeepingTheId_OverTheDirtyOpenCopy_Cancel_KeepsTheEdits_SkipsOnlyThatFile()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();
        _fx.Prompts.ConfirmReplace = false;
        _fx.Prompts.ImportChoice = ImportIdChoice.KeepId;
        _fx.Prompts.ImportFiles =
        [
            _fx.WriteImportFile("l1.json", ScenarioEditorFixture.ScenarioJson("L1", "Lima Imported")),
            _fx.WriteImportFile("l2.json", ScenarioEditorFixture.ScenarioJson("L2", "Mike Imported")),
        ];

        await _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));

        Assert.Equal(["Lima Two"], _fx.Prompts.ReplaceAsked);
        Assert.Equal("Lima", _fx.CopyJsonString("L1", "name"));
        Assert.Equal("Mike Imported", _fx.CopyJsonString("L2", "name"));
        Assert.Equal("Lima Two", editor.General?.Name);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task ImportOverTheCleanOpenCopy_AnEditAfterTheGuardAnswered_IsKept_SaveAsksFirst()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        ImportOver("L1", "Lima Imported");
        Func<string, string, Task<bool>> guard = browser.ReplaceGuard;
        browser.ReplaceGuard = async (artcc, id) =>
        {
            bool replace = await guard(artcc, id);
            Rename(editor, "Lima Two");
            return replace;
        };

        await _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));

        Assert.Empty(_fx.Prompts.ReplaceAsked);
        Assert.Equal("Lima Imported", _fx.CopyJsonString("L1", "name"));
        Assert.Equal("Lima Two", editor.General?.Name);
        Assert.True(editor.IsDirty);
        Assert.Equal(ScenarioEditorViewModel.ReplacedByImportText, editor.SaveMessage);

        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Cancel);
        await _fx.Ui.Run(editor.SaveCommand.ExecuteAsync(null));
        Assert.Equal("Lima Imported", _fx.CopyJsonString("L1", "name"));
        Assert.Equal(["Lima Two"], _fx.Prompts.UnsavedAsked);

        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Save);
        await _fx.Ui.Run(editor.SaveCommand.ExecuteAsync(null));
        Assert.Equal("Lima Two", _fx.CopyJsonString("L1", "name"));
        Assert.Null(editor.SaveMessage);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task ImportOverACopyOpenedAndEditedAfterTheGuardAnswered_IsKept_DiscardAtSaveReloadsTheImport()
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L2");
        ImportOver("L1", "Lima Imported");
        Func<string, string, Task<bool>> guard = browser.ReplaceGuard;
        browser.ReplaceGuard = async (artcc, id) =>
        {
            bool replace = await guard(artcc, id);
            await editor.OpenAsync(Zoa, "L1");
            Rename(editor, "Lima Two");
            return replace;
        };

        await _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));

        Assert.Equal("L1", editor.Session?.ScenarioId);
        Assert.Equal("Lima Two", editor.General?.Name);
        Assert.Equal(ScenarioEditorViewModel.ReplacedByImportText, editor.SaveMessage);
        Assert.Equal("Lima Imported", _fx.CopyJsonString("L1", "name"));

        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Discard);
        await _fx.Ui.Run(editor.SaveCommand.ExecuteAsync(null));

        Assert.Equal("Lima Imported", editor.General?.Name);
        Assert.False(editor.IsDirty);
        Assert.Null(editor.SaveMessage);
        Assert.Equal("Lima Imported", _fx.CopyJsonString("L1", "name"));
    }

    [Fact]
    public async Task Dispose_UnhooksTheBrowser_AndGivesBackItsUnsavedCopy()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyFirstWithOthers();

        editor.Dispose();

        Assert.False(editor.IsOpen);
        Assert.True(await browser.CanCloseAsync());
        await _fx.OpenThrough(browser, editor, "L2");
        Assert.False(editor.IsOpen);
        Assert.Empty(_fx.Prompts.UnsavedAsked);
    }

    [Fact]
    public async Task Dispose_LeavesAnUnsavedCopyThatIsNotItsOwn()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditor();
        Func<UnsavedOpenCopy?> other = () => null;
        browser.UnsavedCopy = other;

        editor.Dispose();

        Assert.Same(other, browser.UnsavedCopy);
    }

    // One file to import that keeps the id of a working copy, its clash answered Keep id.
    private void ImportOver(string id, string name)
    {
        _fx.Prompts.ImportChoice = ImportIdChoice.KeepId;
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile(id + ".json", ScenarioEditorFixture.ScenarioJson(id, name))];
    }

    private static void Rename(ScenarioEditorViewModel editor, string name)
    {
        ScenarioGeneralViewModel general = editor.General ?? throw new InvalidOperationException("nothing is open");
        general.Name = name;
        general.CommitNameCommand.Execute(null);
    }

    // L1 "Lima" open and renamed "Lima Two" (unsaved), L2 "Mike" and L3 "November" listed beside it.
    private async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> DirtyFirstWithOthers(Action? afterAdd = null)
    {
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        _fx.AddCopy("L3", "November");
        afterAdd?.Invoke();
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Rename(editor, "Lima Two");
        return (browser, editor);
    }

    // ZOA and ZNY editable, ZOA shown (the Settings ARTCC), L1 open on ZOA and renamed (unsaved).
    private async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> TwoArtccsWithL1Dirty()
    {
        _fx.Artccs = [Zny, Zoa];
        _fx.Preferences.SetArtccId(Zoa);
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Assert.Equal(Zoa, browser.SelectedArtcc);
        Rename(editor, "Lima Two");
        return (browser, editor);
    }
}
