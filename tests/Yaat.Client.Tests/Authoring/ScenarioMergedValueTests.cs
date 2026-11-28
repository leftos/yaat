using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The merge window's MERGED VALUE editors over real scenario JSON: each opens pre-filled from the side the row takes,
/// shows its field's error when the box is left, keeps a typed value as the row's edit, and offers the editor's pickers,
/// free text when the ARTCC config cannot load.
/// </summary>
public sealed class ScenarioMergedValueTests : IDisposable
{
    private const string PresetId = "01HPRESET00000000000000001";

    private static readonly string SamplePath = Path.Combine(
        TestRepo.Root("the scenario examples are read from the checkout."),
        "docs",
        "atctrainer-scenario-examples",
        "01H06NVK7VN8BS7MCDXHKJZ7MQ.json"
    );

    private static readonly ScenarioMergeCaptions Captions = new(
        "Merge server changes",
        "working copy",
        "server version",
        "server version of 2 Oct 2026",
        "There is no base version: choose a side for every difference."
    );

    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioMergedValueTests() => TestVnasData.EnsureInitialized();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void TriggerTime_EightMinutes_KeepsFourHundredEightySeconds()
    {
        ScenarioMergeViewModel vm = Merge(Text(WithTrigger(Sample(), 0)), WithTrigger(Sample(), 60), WithTrigger(Sample(), 120));
        ScenarioMergeRowViewModel row = Row(vm, "/initializationTriggers/0/timeOffset");
        ScenarioMergedValueViewModel editor = Open(vm, row);
        Assert.Equal((ScenarioMergeEditorKind.Time, "1:00"), (editor.Kind, editor.Text));

        editor.Text = "8:00";
        editor.CommitText();
        editor.KeepCommand.Execute(null);

        MergeChoice choice = vm.Choices[row.Path];
        Assert.Equal((MergeChoiceKind.Edit, "480"), (choice.Kind, choice.Value?.ToJsonString()));
        Assert.Null(row.Editor);
    }

    [Fact]
    public void NumberBox_NotAWholeNumber_ShowsItsErrorAndKeepIsDisabled()
    {
        JsonObject mine = Sample();
        mine["aircraft"]![0]!["flightplan"]!["cruiseAltitude"] = 31000;
        JsonObject theirs = Sample();
        theirs["aircraft"]![0]!["flightplan"]!["cruiseAltitude"] = 33000;
        ScenarioMergeViewModel vm = Merge(Text(Sample()), mine, theirs);
        ScenarioMergeRowViewModel row = Row(vm, "/aircraft/0/flightplan/cruiseAltitude");
        ScenarioMergedValueViewModel editor = Open(vm, row);
        Assert.Equal("31000", editor.Text);

        editor.Text = "45OO";
        editor.CommitText();

        Assert.Equal("Enter a whole number.", editor.Error);
        Assert.False(editor.KeepCommand.CanExecute(null));
        editor.KeepCommand.Execute(null);
        Assert.False(vm.Choices.ContainsKey(row.Path));
        Assert.Same(editor, row.Editor);
    }

    [Fact]
    public void AutoDeleteCombo_OffersTheModes_AndKeepsOnLanding()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel row = Row(vm, "/autoDeleteMode");
        row.KeepTheirsCommand.Execute(null);
        ScenarioMergedValueViewModel editor = Open(vm, row);
        Assert.Equal("Parked", editor.Text);
        Assert.Equal(["Default", "Never", "OnLanding", "Parked"], editor.Options!.Select(o => o.Label));

        editor.Selection = editor.Options!.Single(o => o.Value == "OnLanding");
        editor.KeepCommand.Execute(null);

        Assert.Equal("\"OnLanding\"", vm.Choices[row.Path].Value?.ToJsonString());
    }

    [Fact]
    public void PresetCommand_ThatDoesNotParse_ShowsItsParseError()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergedValueViewModel editor = Open(vm, Row(vm, "/aircraft/1/presetCommands/0/command"));
        Assert.Equal(("FH 020", ScenarioMergeEditorKind.Command), (editor.Text, editor.Kind));

        editor.Text = "XYZZY 42";
        editor.CommitText();

        Assert.StartsWith("The command does not parse: ", editor.Error, StringComparison.Ordinal);
        Assert.True(editor.KeepCommand.CanExecute(null));
    }

    [Fact]
    public void ClashEditedAndKept_PressesEdit_AndCanApplyFollows()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel mode = Row(vm, "/autoDeleteMode");
        ScenarioMergeRowViewModel preset = Row(vm, "/aircraft/1/presetCommands/0/command");

        Open(vm, mode).KeepCommand.Execute(null);

        Assert.Equal((true, false, false, false), (mode.IsEditPressed, mode.IsMinePressed, mode.IsTheirsPressed, mode.IsOpen));
        Assert.Equal((1, false), (vm.OpenClashCount, vm.CanApply));
        ScenarioMergedValueViewModel editor = Open(vm, preset);
        editor.Text = "FH 025";
        editor.KeepCommand.Execute(null);
        Assert.Equal((0, true), (vm.OpenClashCount, vm.CanApply));
        Assert.Equal("\"FH 025\"", vm.Choices[preset.Path].Value?.ToJsonString());
    }

    [Fact]
    public void EditEveryClash_OpensAnEditorUnderEachClash_SkippingTheOrderRow()
    {
        JsonObject mine = Moved(SampleMergeTexts.Mine(), 0, 1);
        JsonObject theirs = Moved(SampleMergeTexts.Theirs(), 1, 2);
        ScenarioMergeViewModel vm = Merge(Text(SampleMergeTexts.Base()), mine, theirs);
        ScenarioMergeRowViewModel order = Row(vm, "/aircraft" + ScenarioMerge.OrderPathSuffix);
        Assert.True(order.IsClash);

        vm.EditEveryClashCommand.Execute(null);

        List<ScenarioMergeRowViewModel> clashes = [.. vm.Rows.Where(row => row.IsClash && !row.IsOrderRow)];
        Assert.Equal(2, clashes.Count);
        Assert.All(clashes, row => Assert.NotNull(row.Editor));
        Assert.Null(order.Editor);
        Assert.All(vm.Rows.Where(row => !row.IsClash), row => Assert.Null(row.Editor));
    }

    [Fact]
    public async Task ConfigUnavailable_StudentPositionFallsBackToFreeText()
    {
        _fx.ConfigBody = null;
        JsonObject mine = Sample();
        mine["studentPositionId"] = "OAK_14";
        JsonObject theirs = Sample();
        theirs["studentPositionId"] = "OAK_15";
        ScenarioMergeViewModel vm = Merge(null, mine, theirs);

        await _fx.Ui.Run(vm.LoadPickersAsync(_fx.Services(), CancellationToken.None));
        ScenarioMergedValueViewModel editor = Open(vm, Row(vm, "/studentPositionId"));

        Assert.Equal((ScenarioMergeEditorKind.Choice, true, "OAK_14"), (editor.Kind, editor.IsFreeText, editor.Text));
        Assert.Null(editor.Options);
    }

    [Fact]
    public void GeneratorEnabled_TheirsAbsent_EditedAndKept_WritesNull()
    {
        JsonObject mine = Sample();
        mine["aircraftGenerators"] = new JsonArray(new JsonObject { ["id"] = "01HGEN0000000000000000001", ["enabled"] = true });
        JsonObject theirs = Sample();
        theirs["aircraftGenerators"] = new JsonArray(new JsonObject { ["id"] = "01HGEN0000000000000000001" });
        ScenarioMergeViewModel vm = Merge(null, mine, theirs);
        ScenarioMergeRowViewModel row = Row(vm, "/aircraftGenerators/0/enabled");
        row.KeepTheirsCommand.Execute(null);

        ScenarioMergedValueViewModel editor = Open(vm, row);
        Assert.Equal((ScenarioMergeEditorKind.OptionalBool, null), (editor.Kind, editor.IsChecked));
        editor.KeepCommand.Execute(null);

        MergeChoice choice = vm.Choices[row.Path];
        Assert.Equal((MergeChoiceKind.Edit, null), (choice.Kind, choice.Value));
    }

    [Fact]
    public void SnapHemisphericAltitude_Absent_OpensCheckedAndKeepsTrue()
    {
        JsonObject mine = Sample();
        mine["overflightGenerators"] = new JsonArray(new JsonObject { ["id"] = "01HOVER000000000000000001" });
        JsonObject theirs = Sample();
        theirs["overflightGenerators"] = new JsonArray(new JsonObject { ["id"] = "01HOVER000000000000000001", ["snapHemisphericAltitude"] = false });
        ScenarioMergeViewModel vm = Merge(null, mine, theirs);
        ScenarioMergeRowViewModel row = Row(vm, "/overflightGenerators/0/snapHemisphericAltitude");

        ScenarioMergedValueViewModel editor = Open(vm, row);
        Assert.Equal(true, editor.IsChecked);
        editor.KeepCommand.Execute(null);

        Assert.Equal("true", vm.Choices[row.Path].Value?.ToJsonString());
    }

    [Fact]
    public void PresetCommand_ResolvingThroughTheAircraftsFlightPlan_ShowsNoError()
    {
        ScenarioMergeViewModel vm = Merge(
            Text(WithPlan(SampleMergeTexts.Base())),
            WithPlan(SampleMergeTexts.Mine()),
            WithPlan(SampleMergeTexts.Theirs())
        );
        ScenarioMergedValueViewModel editor = Open(vm, Row(vm, "/aircraft/1/presetCommands/0/command"));

        editor.Text = "CFIX CFBMS 1400";
        editor.CommitText();

        Assert.Null(editor.Error);
    }

    [Fact]
    public void CommaList_MineNull_LeftBlankAndUnchanged_KeepsNull()
    {
        JsonObject mine = Sample();
        mine["atc"]![0]!["autoTrackAirportIds"] = null;
        JsonObject theirs = Sample();
        theirs["atc"]![0]!["autoTrackAirportIds"] = new JsonArray("OAK");
        ScenarioMergeViewModel vm = Merge(null, mine, theirs);
        ScenarioMergeRowViewModel row = Row(vm, "/atc/0/autoTrackAirportIds");

        ScenarioMergedValueViewModel editor = Open(vm, row);
        Assert.Equal("", editor.Text);
        editor.CommitText();
        editor.KeepCommand.Execute(null);

        Assert.Equal((MergeChoiceKind.Edit, null), (vm.Choices[row.Path].Kind, vm.Choices[row.Path].Value));
    }

    [Fact]
    public void Cancel_ClosesTheEditor_AndLeavesTheChoice()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel row = Row(vm, "/autoDeleteMode");
        row.KeepTheirsCommand.Execute(null);
        ScenarioMergedValueViewModel editor = Open(vm, row);
        editor.Text = "Never";

        editor.CancelCommand.Execute(null);

        Assert.Null(row.Editor);
        Assert.Same(MergeChoice.KeepTheirs, vm.Choices[row.Path]);
        Assert.True(row.IsTheirsPressed);
    }

    [Fact]
    public void ReopeningAnEditedRow_PrefillsTheEdit()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel row = Row(vm, "/autoDeleteMode");
        ScenarioMergedValueViewModel first = Open(vm, row);
        first.Text = "Never";
        first.KeepCommand.Execute(null);

        Assert.Equal("Never", Open(vm, row).Text);
    }

    [Fact]
    public async Task ConfigLoads_PositionsAndAirportsAreOffered()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();
        JsonObject mine = Sample();
        mine["studentPositionId"] = "OAK_14";
        mine["primaryAirportId"] = "SFO";
        JsonObject theirs = Sample();
        theirs["studentPositionId"] = "OAK_15";
        theirs["primaryAirportId"] = "OAK";
        ScenarioMergeViewModel vm = Merge(null, mine, theirs);

        await _fx.Ui.Run(vm.LoadPickersAsync(_fx.Services(), CancellationToken.None));
        ScenarioMergedValueViewModel student = Open(vm, Row(vm, "/studentPositionId"));
        ScenarioMergedValueViewModel airport = Open(vm, Row(vm, "/primaryAirportId"));

        Assert.False(student.IsFreeText);
        Assert.Equal(
            [
                new ScenarioPickOption("", "None"),
                new ScenarioPickOption(ScenarioEditorFixture.ZoaSector14Id, "OAK_14_CTR — Sector 14 (ZOA)"),
                new ScenarioPickOption("OAK_14", "OAK_14 (not in the ARTCC config)"),
            ],
            student.Options
        );
        Assert.Equal(["OAK", "SFO"], airport.Options!.Select(o => o.Value));
        Assert.Equal("SFO", airport.Selection?.Value);
    }

    private static ScenarioMergedValueViewModel Open(ScenarioMergeViewModel vm, ScenarioMergeRowViewModel row)
    {
        vm.EditRowCommand.Execute(row);
        return Assert.IsType<ScenarioMergedValueViewModel>(row.Editor);
    }

    private static ScenarioMergeViewModel SampleMerge() => Merge(Text(SampleMergeTexts.Base()), SampleMergeTexts.Mine(), SampleMergeTexts.Theirs());

    private static ScenarioMergeViewModel Merge(string? baseText, JsonObject mine, JsonObject theirs) =>
        ScenarioMergeViewModel.Create(
            new ScenarioMergeRequest("ZOA", "01H06NVK7VN8BS7MCDXHKJZ7MQ", new(baseText, Text(mine), Text(theirs)), Captions, NoWrite)
        );

    private static Task NoWrite(string json) => Task.CompletedTask;

    private static ScenarioMergeRowViewModel Row(ScenarioMergeViewModel vm, string path) => Assert.Single(vm.Rows, row => row.Path == path);

    private static JsonObject WithPlan(JsonObject scenario)
    {
        scenario["aircraft"]![1]!["flightplan"] = new JsonObject
        {
            ["rules"] = "IFR",
            ["departure"] = "KBCT",
            ["destination"] = "KOAK",
            ["route"] = "",
        };
        return scenario;
    }

    private static JsonObject WithTrigger(JsonObject scenario, int seconds)
    {
        scenario["initializationTriggers"] = new JsonArray(new JsonObject { ["timeOffset"] = seconds, ["command"] = "SQALL" });
        return scenario;
    }

    private static JsonObject Moved(JsonObject scenario, int from, int to)
    {
        JsonArray aircraft = scenario["aircraft"]!.AsArray();
        JsonNode? item = aircraft[from];
        aircraft.RemoveAt(from);
        aircraft.Insert(to, item);
        return scenario;
    }

    private static JsonObject Sample() => JsonNode.Parse(File.ReadAllText(SamplePath))!.AsObject();

    private static string Text(JsonObject scenario) => scenario.ToJsonString();

    // Name changed in theirs only, auto-delete and one preset command changed on both sides.
    private static class SampleMergeTexts
    {
        public static JsonObject Base() => WithPreset(Sample(), "FH 010");

        public static JsonObject Mine()
        {
            JsonObject mine = WithPreset(Sample(), "FH 020");
            mine["autoDeleteMode"] = "OnLanding";
            return mine;
        }

        public static JsonObject Theirs()
        {
            JsonObject theirs = WithPreset(Sample(), "FH 030");
            theirs["autoDeleteMode"] = "Parked";
            theirs["name"] = "Theirs";
            return theirs;
        }

        private static JsonObject WithPreset(JsonObject scenario, string command)
        {
            scenario["aircraft"]![1]!["presetCommands"] = new JsonArray(new JsonObject { ["id"] = PresetId, ["command"] = command });
            return scenario;
        }
    }
}
