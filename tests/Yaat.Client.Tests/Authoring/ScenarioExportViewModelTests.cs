using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// Export JSON from the browser: the full export writes the copy as the scenario document writes it and keeps the
/// YAAT-only parts with a notice naming each, the clean export drops them with a notice naming each, a copy with a
/// validation error is refused, and an open copy with unsaved edits asks Save / Discard / Cancel first. Every written
/// export clears the copy's Unexported badge; a cancelled picker, a refusal and a failed write leave it.
/// </summary>
public sealed class ScenarioExportViewModelTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;

    private const string VfrGenerator = "\"vfrArrivalGenerators\": [{\"id\": \"VFR-NORTH\", \"bearingFrom\": 340, \"bearingTo\": 20}]";

    private const string BadPreset =
        "\"aircraft\": [{\"aircraftId\": \"AAL1\", \"aircraftType\": \"B738\", "
        + "\"presetCommands\": [{\"id\": \"p1\", \"command\": \"XYZZY PLUGH\", \"timeOffset\": 0}]}]";

    private readonly ScenarioEditorFixture _fx = new();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "yaat-scenario-export-tests", Guid.NewGuid().ToString("N"));

    public ScenarioExportViewModelTests()
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
    public async Task Export_Full_Valid_WritesTheDocumentAndClearsTheMark()
    {
        _fx.AddCopy("L1", "Lima");
        ScenarioBrowserViewModel browser = await BrowserOn("L1");
        Assert.True(Marked(browser, "L1"));
        string target = Target("lima.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        string expected = ScenarioDocument.Read(_fx.Store.ReadWorkingCopyText(Zoa, "L1")).Write().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(target));
        Assert.Equal(["Lima.json"], _fx.Prompts.ExportSuggested);
        Assert.False(Marked(browser, "L1"));
        Assert.Empty(browser.Notices);
    }

    [Fact]
    public async Task Export_Full_YaatOnlyParts_KeptWithANoticeNamingEach()
    {
        _fx.AddCopy("V1", "Victor", VfrGenerator);
        ScenarioBrowserViewModel browser = await BrowserOn("V1");
        string target = Target("victor.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        JsonObject exported = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
        Assert.Single(exported["vfrArrivalGenerators"]!.AsArray());
        Assert.Contains("Victor: Kept YAAT-only parts: VFR arrival generator VFR-NORTH.", browser.Notices);
        Assert.False(Marked(browser, "V1"));
    }

    [Fact]
    public async Task Export_Clean_DropsYaatOnlyParts_NoticeNamesEach()
    {
        _fx.AddCopy("V1", "Victor", VfrGenerator + ", \"liveSession\": true");
        ScenarioBrowserViewModel browser = await BrowserOn("V1");
        string target = Target("victor.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportCleanJsonCommand.ExecuteAsync(null));

        JsonObject exported = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
        Assert.False(exported.ContainsKey("vfrArrivalGenerators"));
        Assert.False(exported.ContainsKey("liveSession"));
        string notice = Assert.Single(browser.Notices);
        Assert.StartsWith("Victor: Dropped YAAT-only parts: ", notice, StringComparison.Ordinal);
        Assert.Contains("VFR arrival generator VFR-NORTH", notice, StringComparison.Ordinal);
        Assert.Contains("live session flag", notice, StringComparison.Ordinal);
        Assert.False(Marked(browser, "V1"));
    }

    [Fact]
    public async Task Export_ValidationError_RefusesWritesNothing()
    {
        _fx.AddCopy("B1", "Bravo", BadPreset);
        ScenarioBrowserViewModel browser = await BrowserOn("B1");
        string target = Target("bravo.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.False(File.Exists(target));
        Assert.Empty(_fx.Prompts.ExportSuggested);
        string notice = Assert.Single(browser.Notices);
        Assert.StartsWith("Bravo: not exported: ", notice, StringComparison.Ordinal);
        Assert.Contains("XYZZY PLUGH", notice, StringComparison.Ordinal);
        Assert.True(Marked(browser, "B1"));
    }

    [Fact]
    public async Task Export_DirtyCopy_Save_ExportsTheSavedEdit()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLima();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Save);
        string target = Target("lima.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.Equal([UnsavedChangesContext.Export], _fx.Prompts.UnsavedContexts);
        Assert.Equal("Lima edited", NameIn(target));
        Assert.Equal("Lima edited", _fx.CopyJsonString("L1", "name"));
        Assert.False(editor.IsDirty);
        Assert.False(Marked(browser, "L1"));
    }

    [Fact]
    public async Task Export_DirtyCopy_Discard_ExportsLastSavedAndDropsTheEdit()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLima();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Discard);
        string target = Target("lima.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.Equal("Lima", NameIn(target));
        Assert.True(editor.IsOpen);
        Assert.False(editor.IsDirty);
        Assert.Equal("Lima", editor.Session!.Document.Scenario.Name);
        Assert.False(Marked(browser, "L1"));
    }

    [Fact]
    public async Task Export_DirtyCopy_Cancel_WritesNothing()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLima();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Cancel);
        string target = Target("lima.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.Single(_fx.Prompts.UnsavedContexts);
        Assert.False(File.Exists(target));
        Assert.Empty(_fx.Prompts.ExportSuggested);
        Assert.True(editor.IsDirty);
        Assert.Equal("Lima", _fx.CopyJsonString("L1", "name"));
        Assert.True(Marked(browser, "L1"));
    }

    [Fact]
    public async Task Export_PickerCancelled_WritesNothing()
    {
        _fx.AddCopy("L1", "Lima");
        ScenarioBrowserViewModel browser = await BrowserOn("L1");
        _fx.Prompts.ExportPath = null;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.Equal(["Lima.json"], _fx.Prompts.ExportSuggested);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
        Assert.Empty(browser.Notices);
        Assert.True(Marked(browser, "L1"));
    }

    [Fact]
    public async Task Export_WriteFails_NoticeAndMarkUnchanged()
    {
        _fx.AddCopy("L1", "Lima");
        ScenarioBrowserViewModel browser = await BrowserOn("L1");
        string blocker = Target("a-file");
        File.WriteAllText(blocker, "not a folder");
        _fx.Prompts.ExportPath = Path.Combine(blocker, "lima.json");

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.StartsWith("Lima: ", Assert.Single(browser.Notices), StringComparison.Ordinal);
        Assert.Equal(["a-file"], Directory.GetFileSystemEntries(_out).Select(Path.GetFileName));
        Assert.True(Marked(browser, "L1"));
    }

    [Fact]
    public async Task ExportAll_NoneMarked_SaysNothingToExport()
    {
        _fx.Store.ApplyServerBody(Zoa, "S1", ScenarioEditorFixture.ScenarioJson("S1", "Sierra"));
        _fx.Store.CreateWorkingCopy(Zoa, "S1");
        ScenarioBrowserViewModel browser = await BrowserOn("S1");
        Assert.False(Marked(browser, "S1"));
        _fx.Prompts.ExportFolder = _out;

        await _fx.Ui.Run(browser.ExportAllUnexportedCommand.ExecuteAsync(null));

        Assert.Equal(ScenarioBrowserViewModel.NothingToExportText, browser.StatusText);
        Assert.Equal(0, _fx.Prompts.ExportFolderAsked);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public async Task ExportAll_WritesExactlyTheMarkedCopies_UniqueNames()
    {
        _fx.Store.ApplyServerBody(Zoa, "S1", ScenarioEditorFixture.ScenarioJson("S1", "Same"));
        _fx.Store.CreateWorkingCopy(Zoa, "S1");
        _fx.AddCopy("A1", "Same");
        _fx.AddCopy("A2", "Same");
        ScenarioBrowserViewModel browser = await BrowserOn("A1");
        _fx.Prompts.ExportFolder = _out;

        await _fx.Ui.Run(browser.ExportAllUnexportedCommand.ExecuteAsync(null));

        Assert.Equal(["Same-A1.json", "Same-A2.json"], Directory.GetFiles(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("A1", JsonNode.Parse(File.ReadAllText(Target("Same-A1.json")))!["id"]!.GetValue<string>());
        Assert.Equal("A2", JsonNode.Parse(File.ReadAllText(Target("Same-A2.json")))!["id"]!.GetValue<string>());
        Assert.DoesNotContain(browser.LocalRows, r => r.Badges.Contains(ScenarioBadge.Unexported));
        Assert.Equal(1, _fx.Prompts.ExportFolderAsked);
    }

    [Fact]
    public async Task Export_CorruptCopy_CommandsDisabled()
    {
        _fx.AddCopy("C1", "Charlie");
        File.WriteAllText(_fx.Store.GetWorkingCopyPath(Zoa, "C1"), "not json");
        _fx.Store.ApplyServerBody(Zoa, "S1", ScenarioEditorFixture.ScenarioJson("S1", "Sierra"));
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();

        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == "C1");
        Assert.True(browser.SelectedLocalRow.IsCorrupt);
        AssertExportsDisabled(browser);

        browser.SelectedTab = ScenarioBrowserTab.Server;
        browser.SelectedServerRow = browser.ServerRows.Single(r => r.Id == "S1");
        Assert.False(browser.SelectedServerRow.HasWorkingCopy);
        AssertExportsDisabled(browser);
    }

    [Fact]
    public async Task ImportUnderEdits_ThenExportSave_NextSaveDoesNotAskAboutTheImport()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        _fx.Prompts.ImportChoice = ImportIdChoice.KeepId;
        _fx.Prompts.ImportFiles = [_fx.WriteImportFile("L1.json", ScenarioEditorFixture.ScenarioJson("L1", "Lima Imported"))];
        Func<string, string, Task<bool>> guard = browser.ReplaceGuard;
        browser.ReplaceGuard = async (artcc, id) =>
        {
            bool replace = await guard(artcc, id);
            Rename(editor, "Lima Two");
            return replace;
        };
        await _fx.Ui.Run(browser.ImportCommand.ExecuteAsync(null));
        Assert.Equal(ScenarioEditorViewModel.ReplacedByImportText, editor.SaveMessage);
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Save);
        string target = Target("lima.json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));
        Assert.Equal("Lima Two", NameIn(target));
        Rename(editor, "Lima Three");
        await _fx.Ui.Run(editor.SaveCommand.ExecuteAsync(null));

        Assert.Equal([UnsavedChangesContext.Export], _fx.Prompts.UnsavedContexts);
        Assert.Equal("Lima Three", _fx.CopyJsonString("L1", "name"));
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task ExportAll_OpenDirtyMarkedCopy_Cancel_WritesNothing()
    {
        _fx.AddCopy("A1", "Alpha");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLima();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Cancel);
        _fx.Prompts.ExportFolder = _out;

        await _fx.Ui.Run(browser.ExportAllUnexportedCommand.ExecuteAsync(null));

        Assert.Equal(1, _fx.Prompts.ExportFolderAsked);
        Assert.Equal([UnsavedChangesContext.Export], _fx.Prompts.UnsavedContexts);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
        Assert.True(Marked(browser, "L1"));
        Assert.True(Marked(browser, "A1"));
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task ExportAll_OpenDirtyMarkedCopy_Save_ExportsTheEdit()
    {
        _fx.AddCopy("A1", "Alpha");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLima();
        _fx.Prompts.Answers.Enqueue(UnsavedChangesChoice.Save);
        _fx.Prompts.ExportFolder = _out;

        await _fx.Ui.Run(browser.ExportAllUnexportedCommand.ExecuteAsync(null));

        Assert.Equal([UnsavedChangesContext.Export], _fx.Prompts.UnsavedContexts);
        Assert.Equal(["Alpha-A1.json", "Lima-L1.json"], Directory.GetFiles(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("Lima edited", NameIn(Target("Lima-L1.json")));
        Assert.False(editor.IsDirty);
        Assert.Equal("Exported 2 of 2 scenarios to " + _out + ".", browser.StatusText);
        Assert.DoesNotContain(browser.LocalRows, r => r.HasUnexportedChanges);
    }

    [Fact]
    public async Task ExportAll_OpenDirtyUnmarkedCopy_NotAsked()
    {
        _fx.Store.ApplyServerBody(Zoa, "S1", ScenarioEditorFixture.ScenarioJson("S1", "Sierra"));
        _fx.Store.CreateWorkingCopy(Zoa, "S1");
        _fx.AddCopy("A1", "Alpha");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("S1");
        Rename(editor, "Sierra edited");
        Assert.True(editor.IsDirty);
        Assert.False(Marked(browser, "S1"));
        _fx.Prompts.ExportFolder = _out;

        await _fx.Ui.Run(browser.ExportAllUnexportedCommand.ExecuteAsync(null));

        Assert.Empty(_fx.Prompts.UnsavedContexts);
        Assert.Equal(["Alpha-A1.json"], Directory.GetFiles(_out).Select(Path.GetFileName));
        Assert.Equal("Exported 1 of 1 scenario to " + _out + ".", browser.StatusText);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task Export_TargetUnderVnasCache_RefusedWithNotice()
    {
        _fx.AddCopy("L1", "Lima");
        ScenarioBrowserViewModel browser = await BrowserOn("L1");
        string target = YaatPaths.Combine("cache", "export-" + Guid.NewGuid().ToString("N") + ".json");
        _fx.Prompts.ExportPath = target;

        await _fx.Ui.Run(browser.ExportJsonCommand.ExecuteAsync(null));

        Assert.False(File.Exists(target));
        string notice = Assert.Single(browser.Notices);
        Assert.StartsWith("Lima: ", notice, StringComparison.Ordinal);
        Assert.Contains("vNAS cache", notice, StringComparison.Ordinal);
        Assert.True(Marked(browser, "L1"));
    }

    [Fact]
    public async Task Dispose_GivesBackTheExportAndReplaceGuards()
    {
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await DirtyLima();
        Assert.False(await browser.ExportGuard(Zoa, "L1"));
        Assert.Single(_fx.Prompts.UnsavedContexts);

        editor.Dispose();

        Assert.True(await browser.ExportGuard(Zoa, "L1"));
        Assert.True(await browser.ReplaceGuard(Zoa, "L1"));
        Assert.Single(_fx.Prompts.UnsavedContexts);
        Assert.Empty(_fx.Prompts.ReplaceAsked);
    }

    private static void Rename(ScenarioEditorViewModel editor, string name)
    {
        ScenarioGeneralViewModel general = editor.General ?? throw new InvalidOperationException("nothing is open");
        general.Name = name;
        general.CommitNameCommand.Execute(null);
    }

    private static void AssertExportsDisabled(ScenarioBrowserViewModel browser)
    {
        Assert.False(browser.ExportJsonCommand.CanExecute(null));
        Assert.False(browser.ExportCleanJsonCommand.CanExecute(null));
        Assert.False(browser.ExportAllUnexportedCommand.CanExecute(null));
    }

    private static bool Marked(ScenarioBrowserViewModel browser, string id) =>
        browser.LocalRows.Single(r => r.Id == id).Badges.Contains(ScenarioBadge.Unexported);

    private static string? NameIn(string path) => JsonNode.Parse(File.ReadAllText(path))!["name"]?.GetValue<string>();

    private string Target(string name) => Path.Combine(_out, name);

    private async Task<ScenarioBrowserViewModel> BrowserOn(string id)
    {
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == id);
        return browser;
    }

    // Lima open in the editor with its name edited and not saved, its row selected.
    private async Task<(ScenarioBrowserViewModel Browser, ScenarioEditorViewModel Editor)> DirtyLima()
    {
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        Assert.True(editor.Session!.Edit("Edit name", s => s.Name = "Lima edited"));
        Assert.True(editor.IsDirty);
        Assert.True(Marked(browser, "L1"));
        return (browser, editor);
    }
}
