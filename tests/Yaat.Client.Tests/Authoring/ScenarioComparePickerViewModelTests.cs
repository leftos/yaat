using Xunit;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Compare picker over a real store's browser rows: Mine lists the working copies only (a server scenario with a copy
/// is that copy), Theirs lists the server scenarios, the working copies and File…; a copy with its own server scenario is
/// three-way over the copy's stored base, every other pair is two-way; the same scenario on both sides disables Compare.
/// </summary>
public sealed class ScenarioComparePickerViewModelTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;

    private readonly ScenarioEditorFixture _fx = new();

    // The server text A1's copy was started from, before the server changed it.
    private string _baseText = "";

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task MineListsOnlyWorkingCopies_TheirsListsServerScenariosCopiesAndFile()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();

        ScenarioComparePickerViewModel picker = Picker(browser, null);

        Assert.All(picker.MineOptions, s => Assert.Equal(ScenarioCompareSideKind.WorkingCopy, s.Kind));
        Assert.Equal(["A1", "L1"], picker.MineOptions.Select(s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Server:A1", "Server:B1", "WorkingCopy:A1", "WorkingCopy:L1", "File:"],
            picker
                .TheirsOptions.Select(s => $"{s.Kind}:{s.Id}")
                .OrderBy(k => k.StartsWith("File", StringComparison.Ordinal))
                .ThenBy(k => k, StringComparer.Ordinal)
        );
        Assert.Single(picker.TheirsOptions, s => s.Kind == ScenarioCompareSideKind.File);
        Assert.Equal(ScenarioCompareSideKind.File, picker.TheirsOptions[^1].Kind);
    }

    [Fact]
    public async Task AServerRowWithACopy_ShowsAsTheCopyOnMine()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();

        ScenarioComparePickerViewModel picker = Picker(browser, browser.ServerRows.Single(r => r.Id == "A1"));

        Assert.Equal(ScenarioCompareSideKind.WorkingCopy, picker.SelectedMine?.Kind);
        Assert.Equal("A1", picker.SelectedMine?.Id);
        Assert.Equal("A1", picker.SelectedMine?.OriginId);
        Assert.Null(picker.SelectedTheirs);
        Assert.DoesNotContain(picker.MineOptions, s => s.Kind == ScenarioCompareSideKind.Server);
    }

    [Fact]
    public async Task APreselectedRow_GoesOnTheSideItCanTake()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();

        ScenarioComparePickerViewModel copy = Picker(browser, browser.LocalRows.Single(r => r.Id == "L1"));
        ScenarioComparePickerViewModel serverOnly = Picker(browser, browser.ServerRows.Single(r => r.Id == "B1"));

        Assert.Equal("L1", copy.SelectedMine?.Id);
        Assert.Null(copy.SelectedTheirs);
        Assert.Equal(ScenarioCompareSideKind.Server, serverOnly.SelectedTheirs?.Kind);
        Assert.Equal("B1", serverOnly.SelectedTheirs?.Id);
        Assert.Equal(ScenarioCompareSideKind.WorkingCopy, serverOnly.SelectedMine?.Kind);
    }

    [Fact]
    public async Task ACopyWithItsOwnServerScenario_IsThreeWayOverTheCopysStoredBase()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();
        ScenarioComparePickerViewModel picker = Picker(browser, null);

        ScenarioMergeTexts texts = Texts(picker, Mine(picker, "A1"), Theirs(picker, ScenarioCompareSideKind.Server, "A1"), null);

        string mine = _fx.Store.ReadWorkingCopyText(Zoa, "A1");
        string theirs = _fx.Store.ReadMirrorText(Zoa, "A1");
        Assert.Equal(_baseText, _fx.Store.ReadBaseText(Zoa, "A1"));
        Assert.Equal(_baseText, texts.Base);
        Assert.Equal(mine, texts.Mine);
        Assert.Equal(theirs, texts.Theirs);
        Assert.Contains("\"Alpha\"", _baseText, StringComparison.Ordinal);
        Assert.Contains("Alpha edited", mine, StringComparison.Ordinal);
        Assert.Contains("Alpha v2", theirs, StringComparison.Ordinal);
        Assert.Equal(3, new[] { _baseText, mine, theirs }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task EveryOtherPair_IsTwoWay()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();
        ScenarioComparePickerViewModel picker = Picker(browser, null);
        string fileText = ScenarioEditorFixture.ScenarioJson("F1", "From a file");
        string path = _fx.WriteImportFile("f1.json", fileText);

        ScenarioMergeTexts otherServer = Texts(picker, Mine(picker, "A1"), Theirs(picker, ScenarioCompareSideKind.Server, "B1"), null);
        ScenarioMergeTexts otherCopy = Texts(picker, Mine(picker, "A1"), Theirs(picker, ScenarioCompareSideKind.WorkingCopy, "L1"), null);
        ScenarioMergeTexts file = Texts(picker, Mine(picker, "A1"), picker.TheirsOptions.Single(s => s.Kind == ScenarioCompareSideKind.File), path);

        Assert.Null(otherServer.Base);
        Assert.Equal(_fx.Store.ReadMirrorText(Zoa, "B1"), otherServer.Theirs);
        Assert.Null(otherCopy.Base);
        Assert.Equal(_fx.Store.ReadWorkingCopyText(Zoa, "L1"), otherCopy.Theirs);
        Assert.Null(file.Base);
        Assert.Equal(fileText, file.Theirs);
    }

    [Fact]
    public async Task TheSameScenarioOnBothSides_DisablesCompare()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();
        ScenarioComparePickerViewModel picker = Picker(browser, null);

        picker.SelectedMine = Mine(picker, "L1");
        picker.SelectedTheirs = Theirs(picker, ScenarioCompareSideKind.WorkingCopy, "L1");
        bool same = picker.CanCompare;
        ScenarioComparePick? samePick = picker.Pick;
        picker.SelectedTheirs = Theirs(picker, ScenarioCompareSideKind.Server, "B1");
        bool different = picker.CanCompare;

        Assert.False(same);
        Assert.Null(samePick);
        Assert.True(different);
        Assert.Equal("B1", picker.Pick?.Theirs.Id);
    }

    [Fact]
    public async Task AnEmptySide_DisablesCompare()
    {
        ScenarioBrowserViewModel browser = await SeededBrowser();
        ScenarioComparePickerViewModel picker = Picker(browser, null);

        picker.SelectedMine = Mine(picker, "L1");
        picker.SelectedTheirs = null;
        bool noTheirs = picker.CanCompare;
        picker.SelectedTheirs = Theirs(picker, ScenarioCompareSideKind.Server, "B1");
        picker.SelectedMine = null;

        Assert.False(noTheirs);
        Assert.False(picker.CanCompare);
        Assert.Null(picker.Pick);
    }

    // Server A1 (changed since) with its edited working copy A1, server-only B1, local-only L1.
    private async Task<ScenarioBrowserViewModel> SeededBrowser()
    {
        _fx.Store.ApplyServerBody(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha"));
        _fx.Store.CreateWorkingCopy(Zoa, "A1");
        _fx.Store.SaveWorkingCopy(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha edited"));
        _baseText = _fx.Store.ReadMirrorText(Zoa, "A1");
        _fx.Store.ApplyServerBody(Zoa, "A1", ScenarioEditorFixture.ScenarioJson("A1", "Alpha v2"));
        _fx.Store.ApplyServerBody(Zoa, "B1", ScenarioEditorFixture.ScenarioJson("B1", "Bravo"));
        _fx.AddCopy("L1", "Lima");
        (ScenarioBrowserViewModel browser, _) = await _fx.OpenEditor();
        return browser;
    }

    private static ScenarioComparePickerViewModel Picker(ScenarioBrowserViewModel browser, ScenarioBrowserRowViewModel? preselect) =>
        new(browser.LocalRows, browser.ServerRows, preselect);

    private static ScenarioCompareSide Mine(ScenarioComparePickerViewModel picker, string id) => picker.MineOptions.Single(s => s.Id == id);

    private static ScenarioCompareSide Theirs(ScenarioComparePickerViewModel picker, ScenarioCompareSideKind kind, string id) =>
        picker.TheirsOptions.Single(s => (s.Kind == kind) && (s.Id == id));

    private ScenarioMergeTexts Texts(ScenarioComparePickerViewModel picker, ScenarioCompareSide mine, ScenarioCompareSide theirs, string? path)
    {
        picker.SelectedMine = mine;
        picker.SelectedTheirs = theirs;
        ScenarioComparePick pick = picker.Pick ?? throw new InvalidOperationException("Compare is disabled");
        return ScenarioComparePickerViewModel.ReadTexts(_fx.Store, Zoa, pick, path);
    }
}
