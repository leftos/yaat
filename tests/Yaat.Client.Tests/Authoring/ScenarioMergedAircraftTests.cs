using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The merge window's MERGED AIRCRAFT editor over real scenario JSON: the merge so far pre-fills it, Keep this stores the
/// aircraft (a replacement for one both sides hold, an edit on the item row for one only one side holds), the rows under a
/// replaced aircraft read as resolved, a per-field or whole-scenario choice drops the replacement, Apply places it by the
/// aircraft's identity and names the aircraft in a refusal, and nothing is written while the editor is open.
/// </summary>
public sealed class ScenarioMergedAircraftTests : IDisposable
{
    private const string OakId = "01H08FSWF5NCDXTQMQ3BWB0BND";

    private const string NksId = "01H08FSWF5NCDXTQMQ3BWB0BNP";

    private const string ExtraId = "01HZZZZZZZZZZZZZZZZZZZZZZZ";

    private const string TheirsOnlyId = "01HYYYYYYYYYYYYYYYYYYYYYYY";

    private const string FirstPresetId = "01HPRESETNKS00000000000001";

    private const string SecondPresetId = "01HPRESETNKS00000000000002";

    private const string ThirdPresetId = "01HPRESETNKS00000000000003";

    private const string MissingId = "01HXXXXXXXXXXXXXXXXXXXXXXX";

    private const string RemovedText = "The merged aircraft NKS2904 was removed in the editor; Cancel to close it.";

    private static readonly string ExamplesDir = Path.Combine(
        TestRepo.Root("the scenario examples are read from the checkout."),
        "docs",
        "atctrainer-scenario-examples"
    );

    private static readonly ScenarioMergeCaptions Captions = new(
        "Merge server changes",
        "working copy",
        "server version",
        "server version of 2 Oct 2026",
        "There is no base version: choose a side for every difference."
    );

    private readonly ScenarioEditorFixture _fx = new();

    private readonly List<string> _written = [];

    public ScenarioMergedAircraftTests() => TestVnasData.EnsureInitialized();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Nks2904_PrefilledFromMine_AnEditMergingBothPresetsIsKept_AndApplyWritesIt()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        Assert.Equal(MergeRowKind.AutoMine, Row(vm, NksPath("/startingConditions/parking")).Kind);
        Assert.True(Row(vm, NksPath("/presetCommands/1/command")).IsOpen);

        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);

        ScenarioAircraft prefilled = Edited(editor);
        Assert.Equal(NksId, editor.Aircraft.Selected?.Id);
        Assert.Equal("12", prefilled.StartingConditions.Parking);
        Assert.Equal(["CM 50", "FH 020"], prefilled.PresetCommands.Select(p => p.Command));
        editor.Session.Edit(
            "Merge presets",
            s => s.Aircraft[editor.Index].PresetCommands.Add(new PresetCommand { Id = ThirdPresetId, Command = "FH 030" })
        );
        editor.KeepThisCommand.Execute(null);

        Assert.Null(vm.MergedAircraft);
        Assert.True(vm.CanApply);
        Assert.NotNull(await vm.ApplyAsync(Checks()));
        JsonObject written = ById(Written(), NksId);
        Assert.Equal(["CM 50", "FH 020", "FH 030"], Commands(written));
        Assert.Equal("12", written["startingConditions"]!["parking"]!.GetValue<string>());
    }

    [Fact]
    public async Task IdenticalCallsign_EditedInTheMergedEditor_SurvivesApply()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        Assert.DoesNotContain(vm.Rows, row => row.FieldKey == "aircraftId");

        editor.Session.Edit("Callsign", s => s.Aircraft[editor.Index].AircraftId = "NKS2999");
        editor.KeepThisCommand.Execute(null);

        Assert.NotNull(await vm.ApplyAsync(Checks()));
        JsonObject written = ById(Written(), NksId);
        Assert.Equal("NKS2999", written["aircraftId"]!.GetValue<string>());
        Assert.Equal("12", written["startingConditions"]!["parking"]!.GetValue<string>());
    }

    [Fact]
    public void N4471K_OnlyInTheirs_KeepThis_GivesAnEditOnItsItemRow()
    {
        ScenarioMergeViewModel vm = Merge(Text(Oak()), Oak(), WithTheirsOnly(Oak()));
        ScenarioMergeRowViewModel item = Assert.Single(vm.Rows, r => (r.Section == "Aircraft") && r.IsItemRow);
        ScenarioMergedAircraftViewModel editor = Open(vm, TheirsOnlyId);
        Assert.Equal("N4471K", Edited(editor).AircraftId);

        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 240);
        editor.KeepThisCommand.Execute(null);

        MergeChoice choice = vm.Choices[item.Path];
        Assert.Equal(MergeChoiceKind.Edit, choice.Kind);
        Assert.Equal(240, choice.Value!["spawnDelay"]!.GetValue<int>());
        Assert.Equal("N4471K", choice.Value["aircraftId"]!.GetValue<string>());
        Assert.Empty(vm.AircraftEdits);
        Assert.True(item.IsEditPressed);
    }

    [Fact]
    public void KeepThis_OnAnAircraftBothSidesHold_ItsRowsShowEditPressed_AreNotOpen_AndTheCountsDrop()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: true);
        Assert.Equal((2, 1), (vm.OpenClashCount, vm.AircraftOpenClashCount));
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);

        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 90);
        editor.KeepThisCommand.Execute(null);

        List<ScenarioMergeRowViewModel> rows = [.. vm.Rows.Where(row => row.ItemKey == NksId)];
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal((true, false), (row.IsEditPressed, row.IsOpen)));
        Assert.Contains(NksId, vm.AircraftEdits.Keys);
        Assert.Equal((1, 0), (vm.OpenClashCount, vm.AircraftOpenClashCount));
        Assert.False(vm.CanApply);

        vm.Choose(Row(vm, "/autoDeleteMode"), MergeChoice.KeepMine);

        Assert.True(vm.CanApply);
    }

    [Fact]
    public void KeepMine_OnOneFieldOfAReplacedAircraft_DropsTheReplacement_AndItsRowsReturnToTheirPriorState()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 90);
        editor.KeepThisCommand.Execute(null);
        Assert.Equal(0, vm.OpenClashCount);
        ScenarioMergeRowViewModel parking = Row(vm, NksPath("/startingConditions/parking"));
        ScenarioMergeRowViewModel preset = Row(vm, NksPath("/presetCommands/1/command"));

        parking.KeepMineCommand.Execute(null);

        Assert.Empty(vm.AircraftEdits);
        Assert.Equal((true, false), (parking.IsMinePressed, parking.IsEditPressed));
        Assert.Equal((true, false), (preset.IsOpen, preset.IsEditPressed));
        Assert.Equal(1, vm.OpenClashCount);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void TakeTheirsForEverything_AfterAMergedEdit_DropsIt()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 90);
        editor.KeepThisCommand.Execute(null);
        Assert.NotEmpty(vm.AircraftEdits);

        vm.TakeTheirsForEverythingCommand.Execute(null);

        Assert.Empty(vm.AircraftEdits);
        Assert.All(vm.Rows, row => Assert.Equal((true, false), (row.IsTheirsPressed, row.IsEditPressed)));
    }

    [Fact]
    public async Task Replacement_MineInsertsBeforeAndTheirsRemovesAnother_EditsOnlyThatAircraft()
    {
        (JsonObject baseSide, JsonObject mine, JsonObject theirs) = NksSides(withAutoDeleteClash: false);
        mine["aircraft"]!.AsArray().Insert(0, Extra(mine));
        theirs["aircraft"]!.AsArray().Remove(Craft(theirs, "FDX440"));
        ScenarioMergeViewModel vm = Merge(Text(baseSide), mine, theirs);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 300);
        editor.KeepThisCommand.Execute(null);
        var unreplaced = new Dictionary<string, MergeChoice>(StringComparer.Ordinal)
        {
            [NksPath("/presetCommands/1/command", 9)] = MergeChoice.KeepMine,
        };
        JsonArray expected = JsonNode.Parse(vm.Plan.Apply(unreplaced))!["aircraft"]!.AsArray();

        Assert.NotNull(await vm.ApplyAsync(Checks()));

        JsonArray written = Written()["aircraft"]!.AsArray();
        Assert.Equal(expected.Count, written.Count);
        Assert.Equal(300, ById(Written(), NksId)["spawnDelay"]!.GetValue<int>());
        for (int i = 0; i < written.Count; i++)
        {
            if (written[i]!["id"]!.GetValue<string>() != NksId)
            {
                Assert.True(JsonNode.DeepEquals(expected[i], written[i]), $"aircraft {i} changed");
            }
        }
    }

    [Fact]
    public async Task Replacement_AircraftWithNoId_IsPlacedThroughMinesElement()
    {
        JsonObject mine = WithoutNksId(Oak());
        Craft(mine, "NKS2904")["spawnDelay"] = 60;
        JsonObject theirs = WithoutNksId(Oak());
        Craft(theirs, "SWA5456")["spawnDelay"] = 120;
        ScenarioMergeViewModel vm = Merge(Text(WithoutNksId(Oak())), mine, theirs);
        Assert.Equal("#8", Assert.Single(vm.Rows, row => (row.FieldKey == "spawnDelay") && (row.Kind == MergeRowKind.AutoMine)).ItemKey);
        ScenarioMergedAircraftViewModel editor = Open(vm, "#8");
        Assert.Equal(("NKS2904", 60), (Edited(editor).AircraftId, Edited(editor).SpawnDelay));

        editor.Session.Edit("Callsign", s => s.Aircraft[editor.Index].AircraftId = "NKS2999");
        editor.KeepThisCommand.Execute(null);
        Assert.Contains("#8", vm.AircraftEdits.Keys);

        Assert.NotNull(await vm.ApplyAsync(Checks()));
        JsonArray written = Written()["aircraft"]!.AsArray();
        Assert.Equal(30, written.Count);
        Assert.Equal(("NKS2999", 60), (written[8]!["aircraftId"]!.GetValue<string>(), written[8]!["spawnDelay"]!.GetValue<int>()));
        Assert.Equal(120, Craft(Written(), "SWA5456")["spawnDelay"]!.GetValue<int>());
    }

    [Fact]
    public async Task Replacement_AircraftWithNoId_TheirsAppendsOne_LandsAtItsPosition()
    {
        JsonObject theirs = WithTwin(Oak(), "NKS2904");
        theirs["aircraft"]!.AsArray().Add(Extra(theirs));
        ScenarioMergeViewModel vm = Merge(Text(WithTwin(Oak(), "NKS2905")), WithTwin(Oak(), "NKS2906"), theirs);
        ScenarioMergeRowViewModel twin = Assert.Single(vm.Rows, row => row.Path == "/aircraft/9/aircraftId");
        ScenarioMergedAircraftViewModel editor = Open(vm, "#8");
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 300);
        editor.KeepThisCommand.Execute(null);
        twin.KeepTheirsCommand.Execute(null);
        Assert.Contains("#8", vm.AircraftEdits.Keys);

        Assert.NotNull(await vm.ApplyAsync(Checks()));

        JsonArray written = Written()["aircraft"]!.AsArray();
        Assert.Equal(32, written.Count);
        Assert.Equal(("NKS2904", 300), (written[8]!["aircraftId"]!.GetValue<string>(), written[8]!["spawnDelay"]!.GetValue<int>()));
        Assert.Equal("NKS2904", written[9]!["aircraftId"]!.GetValue<string>());
        Assert.NotEqual("300", written[9]!["spawnDelay"]?.ToJsonString());
        Assert.Equal("N172SP", written[31]!["aircraftId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refusal_OnAnAircraftField_NamesTheAircraft()
    {
        ScenarioMergeViewModel vm = Merge(Text(Oak()), Oak(), WithTheirsOnly(Oak()));
        ScenarioMergeRowViewModel item = Assert.Single(vm.Rows, r => (r.Section == "Aircraft") && r.IsItemRow);
        vm.EditRowCommand.Execute(item);
        ScenarioMergedValueViewModel value = Assert.IsType<ScenarioMergedValueViewModel>(item.Editor);
        JsonObject edited = JsonNode.Parse(value.Text)!.AsObject();
        edited["spawnDelay"] = "soon";
        value.Text = edited.ToJsonString();
        value.KeepCommand.Execute(null);

        Assert.Null(await vm.ApplyAsync(Checks()));

        Assert.Empty(_written);
        Assert.StartsWith("Can't apply: Spawn delay", vm.ApplyError, StringComparison.Ordinal);
        Assert.EndsWith(" of N4471K does not hold a value a scenario can read.", vm.ApplyError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_IsDisabledWhileTheMergedAircraftEditorIsOpen()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        vm.Choose(Row(vm, NksPath("/presetCommands/1/command")), MergeChoice.KeepMine);
        Assert.True(vm.CanApply);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);

        Assert.False(vm.CanApply);
        Assert.Contains(nameof(ScenarioMergeViewModel.CanApply), changes);
        Assert.Null(await vm.ApplyAsync(Checks()));
        Assert.Empty(_written);
        editor.CancelCommand.Execute(null);
        Assert.Null(vm.MergedAircraft);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Cancel_ChangesNothing()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: true);
        vm.Choose(Row(vm, "/autoDeleteMode"), MergeChoice.KeepTheirs);
        (string, string, int, int, string) before = State(vm);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 90);

        editor.CancelCommand.Execute(null);

        Assert.Null(vm.MergedAircraft);
        Assert.Equal(before, State(vm));
    }

    [Fact]
    public async Task TheStore_IsNeverWrittenWhileEditing()
    {
        (JsonObject baseSide, JsonObject mine, JsonObject theirs) = NksSides(withAutoDeleteClash: false);
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, Text(mine), OakId);
        byte[] stored = _fx.CopyBytes(OakId);
        ScenarioMergeViewModel vm = Merge(Text(baseSide), mine, theirs);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = 90);
        Assert.Equal(stored, _fx.CopyBytes(OakId));

        editor.KeepThisCommand.Execute(null);

        Assert.Equal(stored, _fx.CopyBytes(OakId));
        Assert.Empty(_written);
        Assert.NotNull(await vm.ApplyAsync(Checks()));
        Assert.Equal(stored, _fx.CopyBytes(OakId));
    }

    [Fact]
    public void ChoiceCommands_WhileTheMergedAircraftEditorIsOpen_AreDisabled_AndChooseThrows()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: true);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        (string, string, int, int, string) before = State(vm);
        ScenarioMergeRowViewModel autoDelete = Row(vm, "/autoDeleteMode");
        ScenarioMergeAircraftLineViewModel line = Line(vm, NksId);

        Assert.False(vm.TakeTheirsForEverythingCommand.CanExecute(null));
        vm.TakeTheirsForEverythingCommand.Execute(null);

        Assert.Equal(before, State(vm));
        Assert.False(vm.KeepMineForEveryClashCommand.CanExecute(null));
        Assert.False(vm.EditEveryClashCommand.CanExecute(null));
        Assert.False(vm.EditRowCommand.CanExecute(autoDelete));
        Assert.False(autoDelete.KeepMineCommand.CanExecute(null));
        Assert.False(line.KeepTheirsCommand.CanExecute(null));
        autoDelete.KeepMineCommand.Execute(null);
        line.KeepTheirsCommand.Execute(null);
        Assert.Equal(before, State(vm));
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => vm.Choose(autoDelete, MergeChoice.KeepMine));
        Assert.Equal("Close the merged aircraft editor first.", thrown.Message);

        editor.CancelCommand.Execute(null);

        Assert.True(vm.TakeTheirsForEverythingCommand.CanExecute(null));
        Assert.True(autoDelete.KeepMineCommand.CanExecute(null));
        Assert.True(line.KeepTheirsCommand.CanExecute(null));
    }

    [Fact]
    public async Task WindowCancel_ClosesTheMergedAircraftEditor_AndDisposesItsValidation()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);

        vm.CancelCommand.Execute(null);

        Assert.Null(vm.MergedAircraft);
        Task last = editor.Validation.Recomputed;
        editor.Validation.Recompute();
        Assert.Same(last, editor.Validation.Recomputed);
        Assert.Null(await vm.Completion);
    }

    [Fact]
    public void KeepTheirsForEveryClash_LeavesTheRowsOfAReplacedAircraftAsTheyWere()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: true);
        _ = KeepSpawnDelay(vm, NksId, 90);

        vm.KeepTheirsForEveryClashCommand.Execute(null);

        Assert.False(vm.Choices.ContainsKey(NksPath("/presetCommands/1/command")));
        Assert.Contains(NksId, vm.AircraftEdits.Keys);
        Assert.All(vm.Rows.Where(row => row.ItemKey == NksId), row => Assert.Equal((true, false), (row.IsEditPressed, row.IsOpen)));
        Assert.Equal(MergeChoiceKind.KeepTheirs, vm.Choices["/autoDeleteMode"].Kind);
    }

    [Fact]
    public void EditEveryClash_OpensNoEditorUnderAReplacedAircraft()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: true);
        _ = KeepSpawnDelay(vm, NksId, 90);

        vm.EditEveryClashCommand.Execute(null);

        Assert.Null(Row(vm, NksPath("/presetCommands/1/command")).Editor);
        Assert.NotNull(Row(vm, "/autoDeleteMode").Editor);
    }

    [Fact]
    public void KeepThis_AfterTheSeededAircraftWasRemovedInTheEditor_RefusesAndStoresNothing()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Remove", s => s.Aircraft.RemoveAt(editor.Index));

        editor.KeepThisCommand.Execute(null);

        Assert.Same(editor, vm.MergedAircraft);
        Assert.Empty(vm.AircraftEdits);
        Assert.Equal(RemovedText, vm.ApplyError);
    }

    [Fact]
    public void Cancel_AfterAnEarlierRefusal_KeepsTheApplyError()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Remove", s => s.Aircraft.RemoveAt(editor.Index));
        editor.KeepThisCommand.Execute(null);

        editor.CancelCommand.Execute(null);

        Assert.Null(vm.MergedAircraft);
        Assert.Empty(vm.AircraftEdits);
        Assert.Equal(RemovedText, vm.ApplyError);
    }

    [Fact]
    public void ASecondCancel_AfterKeepThis_DoesNotCloseANewlyOpenedEditor()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel first = KeepSpawnDelay(vm, NksId, 90);
        ScenarioMergedAircraftViewModel second = Open(vm, NksId);

        first.CancelCommand.Execute(null);
        first.KeepThisCommand.Execute(null);

        Assert.Same(second, vm.MergedAircraft);
        Assert.Equal(90, Assert.Single(vm.AircraftEdits).Value["spawnDelay"]!.GetValue<int>());
    }

    [Fact]
    public void KeepThis_OnAnIdenticalAircraft_MarksItsLineReplaced_AndOnlyDifferencesKeepsItShown()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        List<ScenarioMergeAircraftLineViewModel> same = [.. vm.AircraftLines.Where(line => !line.IsDifferent)];
        ScenarioMergeAircraftLineViewModel line = same[0];
        var changes = new List<string?>();
        line.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(line.IsReplaced);

        _ = KeepSpawnDelay(vm, line.ItemKey, 45);
        vm.OnlyDifferences = true;

        Assert.True(line.IsReplaced);
        Assert.Contains(nameof(ScenarioMergeAircraftLineViewModel.IsReplaced), changes);
        Assert.True(line.IsVisible);
        Assert.False(same[1].IsVisible);
    }

    [Fact]
    public void EditAircraft_OnAnAircraftNotInTheMerge_ReportsItCouldNotBeFound()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);

        Assert.Null(vm.EditAircraft(MissingId, Services()));

        Assert.Null(vm.MergedAircraft);
        Assert.Equal($"Can't edit the merged aircraft {MissingId}: it could not be found.", vm.ApplyError);
    }

    [Fact]
    public void FieldEditor_UnderAReplacedAircraft_PrefillsFromTheReplacement()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Preset", s => s.Aircraft[editor.Index].PresetCommands[1].Command = "FH 040");
        editor.KeepThisCommand.Execute(null);
        ScenarioMergeRowViewModel preset = Row(vm, NksPath("/presetCommands/1/command"));

        vm.EditRowCommand.Execute(preset);

        Assert.Equal("FH 040", Assert.IsType<ScenarioMergedValueViewModel>(preset.Editor).Text);
    }

    [Fact]
    public void HeaderKeepTheirs_OnAReplacedAircraft_DropsItsEdit()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        _ = KeepSpawnDelay(vm, NksId, 90);

        Line(vm, NksId).KeepTheirsCommand.Execute(null);

        Assert.Empty(vm.AircraftEdits);
        Assert.All(vm.Rows.Where(row => row.ItemKey == NksId), row => Assert.Equal((true, false), (row.IsTheirsPressed, row.IsEditPressed)));
    }

    [Fact]
    public void KeepEdit_OnAFieldOfAReplacedAircraft_DropsItsEdit()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        _ = KeepSpawnDelay(vm, NksId, 90);
        ScenarioMergeRowViewModel preset = Row(vm, NksPath("/presetCommands/1/command"));
        vm.EditRowCommand.Execute(preset);

        Assert.IsType<ScenarioMergedValueViewModel>(preset.Editor).KeepCommand.Execute(null);

        Assert.Empty(vm.AircraftEdits);
        Assert.True(preset.IsEditPressed);
        ScenarioMergeRowViewModel parking = Row(vm, NksPath("/startingConditions/parking"));
        Assert.Equal((true, false), (parking.IsMinePressed, parking.IsEditPressed));
    }

    [Fact]
    public void Reopening_TheEditor_ShowsTheKeptReplacement()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        _ = KeepSpawnDelay(vm, NksId, 90);

        ScenarioMergedAircraftViewModel again = Open(vm, NksId);

        Assert.Equal(90, Edited(again).SpawnDelay);
    }

    [Fact]
    public void KeepThis_WithNoChange_StoresNothing()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        (string, string, int, int, string) before = State(vm);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);

        editor.KeepThisCommand.Execute(null);

        Assert.Null(vm.MergedAircraft);
        Assert.Empty(vm.AircraftEdits);
        Assert.Equal(before, State(vm));
    }

    [Fact]
    public void FieldEditor_UnderAReplacedAircraft_FindsAnIdMatchedPresetByItsId()
    {
        JsonObject baseSide = WithNksPresets(Oak(), "FH 010");
        JsonObject mine = WithNksPresets(Oak(), "FH 010");
        Craft(mine, "NKS2904")["presetCommands"]![0]!["command"] = "CM 60";
        JsonObject theirs = Oak();
        Craft(theirs, "NKS2904")["presetCommands"] = new JsonArray(
            new JsonObject { ["id"] = SecondPresetId, ["command"] = "FH 010" },
            new JsonObject { ["id"] = FirstPresetId, ["command"] = "CM 70" }
        );
        ScenarioMergeViewModel vm = Merge(Text(baseSide), mine, theirs);
        Assert.Single(vm.Rows, row => row.IsOrderRow && (row.ItemKey == NksId)).KeepTheirsCommand.Execute(null);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Preset", s => s.Aircraft[editor.Index].PresetCommands.Single(p => p.Id == FirstPresetId).Command = "CM 80");
        editor.KeepThisCommand.Execute(null);
        ScenarioMergeRowViewModel first = Row(vm, NksPath("/presetCommands/0/command"));

        vm.EditRowCommand.Execute(first);

        Assert.Equal("CM 80", Assert.IsType<ScenarioMergedValueViewModel>(first.Editor).Text);
    }

    [Fact]
    public void FieldEditor_UnderAReplacedAircraft_ResolvesAgainstTheReplacementsFlightPlan()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Destination", s => s.Aircraft[editor.Index].FlightPlan!.Destination = "KSMF");
        editor.KeepThisCommand.Execute(null);
        ScenarioMergeRowViewModel preset = Row(vm, NksPath("/presetCommands/1/command"));

        vm.EditRowCommand.Execute(preset);

        Assert.Equal("KSMF", Assert.IsType<ScenarioMergedValueViewModel>(preset.Editor).Context?.Destination);
    }

    [Fact]
    public void EditAircraft_OnlyInTheirs_IsNotTakenForAnAircraftCarryingThePlaceMember()
    {
        JsonObject baseSide = Oak();
        JsonObject mine = Oak();
        JsonObject theirs = WithTheirsOnly(Oak());
        foreach (JsonObject side in (JsonObject[])[baseSide, mine, theirs])
        {
            side["aircraft"]![0]!["$yaatMergePlace"] = "hand-edited";
        }

        ScenarioMergeViewModel vm = Merge(Text(baseSide), mine, theirs);

        Assert.Equal("N4471K", Edited(Open(vm, TheirsOnlyId)).AircraftId);
    }

    [Fact]
    public void ShowBaseAndOnlyDifferences_WhileTheEditorIsOpen_KeepTheRemovedRefusal()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Remove", s => s.Aircraft.RemoveAt(editor.Index));
        editor.KeepThisCommand.Execute(null);

        vm.ShowBase = true;
        vm.OnlyDifferences = true;

        Assert.Equal(RemovedText, vm.ApplyError);
    }

    [Fact]
    public void KeepThis_WithNoChangeAfterTheRemovalIsUndone_ClosesTheEditor_AndClearsTheRefusal()
    {
        ScenarioMergeViewModel vm = NksMerge(withAutoDeleteClash: false);
        ScenarioMergedAircraftViewModel editor = Open(vm, NksId);
        editor.Session.Edit("Remove", s => s.Aircraft.RemoveAt(editor.Index));
        editor.KeepThisCommand.Execute(null);
        Assert.True(editor.Session.Undo());

        editor.KeepThisCommand.Execute(null);

        Assert.Null(vm.MergedAircraft);
        Assert.Empty(vm.AircraftEdits);
        Assert.Null(vm.ApplyError);
    }

    private static ScenarioMergeRowViewModel Row(ScenarioMergeViewModel vm, string path) => Assert.Single(vm.Rows, row => row.Path == path);

    private static string NksPath(string member, int index = 8) => $"/aircraft/{index}{member}";

    private static (string, string, int, int, string) State(ScenarioMergeViewModel vm) =>
        (
            string.Join(",", vm.Choices.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value.Kind}")),
            string.Join(",", vm.AircraftEdits.Keys.Order(StringComparer.Ordinal)),
            vm.OpenClashCount,
            vm.AircraftOpenClashCount,
            string.Join(",", vm.Rows.Select(row => $"{row.PressedSide}:{row.IsOpen}"))
        );

    private static ScenarioMergeAircraftLineViewModel Line(ScenarioMergeViewModel vm, string key) =>
        Assert.Single(vm.AircraftLines, line => line.ItemKey == key);

    private ScenarioMergedAircraftServices Services() => new(_fx.Services(), ScenarioEditorFixture.NoLayouts, Task.CompletedTask);

    private ScenarioMergedAircraftViewModel KeepSpawnDelay(ScenarioMergeViewModel vm, string key, int delay)
    {
        ScenarioMergedAircraftViewModel editor = Open(vm, key);
        editor.Session.Edit("Spawn delay", s => s.Aircraft[editor.Index].SpawnDelay = delay);
        editor.KeepThisCommand.Execute(null);
        return editor;
    }

    private ScenarioMergedAircraftViewModel Open(ScenarioMergeViewModel vm, string itemKey)
    {
        ScenarioMergedAircraftViewModel? editor = vm.EditAircraft(itemKey, Services());
        Assert.NotNull(editor);
        Assert.Same(editor, vm.MergedAircraft);
        return editor;
    }

    private static ScenarioAircraft Edited(ScenarioMergedAircraftViewModel editor) => editor.Session.Document.Scenario.Aircraft[editor.Index];

    private JsonObject Written() => JsonNode.Parse(Assert.Single(_written))!.AsObject();

    private ScenarioMergeViewModel NksMerge(bool withAutoDeleteClash)
    {
        (JsonObject baseSide, JsonObject mine, JsonObject theirs) = NksSides(withAutoDeleteClash);
        return Merge(Text(baseSide), mine, theirs);
    }

    private ScenarioMergeViewModel Merge(string? baseText, JsonObject mine, JsonObject theirs) =>
        ScenarioMergeViewModel.Create(new ScenarioMergeRequest("ZOA", OakId, new(baseText, Text(mine), Text(theirs)), Captions, Recorded));

    private Task Recorded(string json)
    {
        _written.Add(json);
        return Task.CompletedTask;
    }

    private static ScenarioMergeChecks Checks() => new(_ => [], _ => Task.CompletedTask);

    // NKS2904's parking changed in mine only (11 to 12) and its second preset changed on both sides.
    private static (JsonObject Base, JsonObject Mine, JsonObject Theirs) NksSides(bool withAutoDeleteClash)
    {
        JsonObject baseSide = WithNksPresets(Oak(), "FH 010");
        JsonObject mine = WithNksPresets(Oak(), "FH 020");
        Craft(mine, "NKS2904")["startingConditions"]!["parking"] = "12";
        JsonObject theirs = WithNksPresets(Oak(), "FH 030");
        if (withAutoDeleteClash)
        {
            mine["autoDeleteMode"] = "OnLanding";
            theirs["autoDeleteMode"] = "Parked";
        }

        return (baseSide, mine, theirs);
    }

    private static JsonObject WithNksPresets(JsonObject scenario, string second)
    {
        Craft(scenario, "NKS2904")["presetCommands"] = new JsonArray(
            new JsonObject { ["id"] = FirstPresetId, ["command"] = "CM 50" },
            new JsonObject { ["id"] = SecondPresetId, ["command"] = second }
        );
        return scenario;
    }

    private static JsonObject WithTheirsOnly(JsonObject scenario)
    {
        JsonObject added = Craft(scenario, "N3796G").DeepClone().AsObject();
        added["id"] = TheirsOnlyId;
        added["aircraftId"] = "N4471K";
        scenario["aircraft"]!.AsArray().Add(added);
        return scenario;
    }

    private static JsonObject Extra(JsonObject scenario)
    {
        JsonObject added = scenario["aircraft"]![0]!.DeepClone().AsObject();
        added["id"] = ExtraId;
        added["aircraftId"] = "N172SP";
        return added;
    }

    private static JsonObject WithoutNksId(JsonObject scenario)
    {
        Craft(scenario, "NKS2904").Remove("id");
        return scenario;
    }

    // A copy of NKS2904 right after it, its id repeated so the list is matched by position, under another callsign.
    private static JsonObject WithTwin(JsonObject scenario, string callsign)
    {
        JsonObject twin = Craft(scenario, "NKS2904").DeepClone().AsObject();
        twin["aircraftId"] = callsign;
        scenario["aircraft"]!.AsArray().Insert(9, twin);
        return scenario;
    }

    private static List<string> Commands(JsonObject aircraft) =>
        [.. aircraft["presetCommands"]!.AsArray().Select(preset => preset!["command"]!.GetValue<string>())];

    private static JsonObject ById(JsonObject scenario, string id) =>
        scenario["aircraft"]!.AsArray().Single(aircraft => aircraft!["id"]?.GetValue<string>() == id)!.AsObject();

    private static JsonObject Craft(JsonObject scenario, string callsign) =>
        scenario["aircraft"]!.AsArray().Single(aircraft => aircraft!["aircraftId"]!.GetValue<string>() == callsign)!.AsObject();

    // The OAK scenario holding NKS2904 (the ninth aircraft): 30 aircraft.
    private static JsonObject Oak() => JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, OakId + ".json")))!.AsObject();

    private static string Text(JsonObject scenario) => scenario.ToJsonString();
}
