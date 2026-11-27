using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioMergeViewModel"/> over real scenario JSON: rows with their texts and pressed sides, the choices, the
/// counts and texts that gate Apply, Previous/Next in the editor's order, and the base strips.
/// </summary>
public class ScenarioMergeViewModelTests
{
    private const string PresetId = "01HPRESET00000000000000001";

    private static readonly string SamplePath = Path.Combine(
        FindRepoRoot(),
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

    [Fact]
    public void Create_TwoClashesAndTwoAutoRows_SummaryCountsAndApplyOff()
    {
        ScenarioMergeViewModel vm = SampleMerge();

        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(2, vm.OpenClashCount);
        Assert.Equal("2 auto-merged · 2 clashes to resolve", vm.SummaryText);
        Assert.Equal("Resolve 2 clashes to apply. Nothing is saved until you apply.", vm.FooterText);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Choose_BothClashes_ApplyOnAndResolvedSummary()
    {
        ScenarioMergeViewModel vm = SampleMerge();

        Row(vm, "/autoDeleteMode").KeepMineCommand.Execute(null);
        Assert.Equal("2 auto-merged · 1 clash to resolve", vm.SummaryText);
        Assert.Equal("Resolve 1 clash to apply. Nothing is saved until you apply.", vm.FooterText);
        Row(vm, "/aircraft/1/presetCommands/0/command").KeepTheirsCommand.Execute(null);

        Assert.Equal(0, vm.OpenClashCount);
        Assert.True(vm.CanApply);
        Assert.Equal("All differences resolved: 2 merged by themselves, 2 chosen.", vm.SummaryText);
    }

    [Fact]
    public void KeepMine_OnAutoTheirsRow_StoresKeepMineAndPressesMine()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel name = Row(vm, "/name");
        Assert.Equal((MergeRowKind.AutoTheirs, false, true), (name.Kind, name.IsMinePressed, name.IsTheirsPressed));

        name.KeepMineCommand.Execute(null);

        Assert.Same(MergeChoice.KeepMine, vm.Choices["/name"]);
        Assert.Equal((true, false), (name.IsMinePressed, name.IsTheirsPressed));
    }

    [Fact]
    public void Clash_WithNoChoice_PressesNothing()
    {
        ScenarioMergeRowViewModel clash = Row(SampleMerge(), "/autoDeleteMode");

        Assert.Equal((MergeRowKind.Clash, true), (clash.Kind, clash.IsOpen));
        Assert.Equal((false, false), (clash.IsMinePressed, clash.IsTheirsPressed));
    }

    [Fact]
    public void Next_RowsAircraftFirstInJson_WalksGeneralSectionsThenAircraftWithoutWrapping()
    {
        JsonObject mine = AircraftFirst(Sample());
        mine["aircraft"]![2]!["aircraftId"] = "UAL1";
        mine["atc"]![1]!["autoConnect"] = false;
        mine["name"] = "Mine";
        JsonObject theirs = AircraftFirst(Sample());
        theirs["aircraft"]![2]!["aircraftId"] = "UAL2";
        theirs["atc"]![1]!["autoConnect"] = JsonValue.Create("yes");
        theirs["name"] = "Theirs";
        ScenarioMergeViewModel vm = Merge(Text(AircraftFirst(Sample())), mine, theirs);
        Assert.Equal("/aircraft/2/aircraftId", vm.Rows[0].Path);

        List<(string?, string, ScenarioMergeTab)> walked = [];
        for (int i = 0; i < 4; i++)
        {
            vm.NextClashCommand.Execute(null);
            walked.Add((vm.CurrentClash?.Path, vm.PositionText, vm.SelectedTab));
        }

        Assert.Equal(
            [
                ("/name", "clash 1 of 3", ScenarioMergeTab.General),
                ("/atc/1/autoConnect", "clash 2 of 3", ScenarioMergeTab.General),
                ("/aircraft/2/aircraftId", "clash 3 of 3", ScenarioMergeTab.Aircraft),
                ("/aircraft/2/aircraftId", "clash 3 of 3", ScenarioMergeTab.Aircraft),
            ],
            walked
        );
        Assert.Equal(Sample()["aircraft"]![2]!["id"]!.GetValue<string>(), vm.SelectedAircraftId);
        Assert.False(vm.NextClashCommand.CanExecute(null));

        for (int i = 0; i < 3; i++)
        {
            vm.PreviousClashCommand.Execute(null);
        }

        Assert.Equal(("/name", "clash 1 of 3"), (vm.CurrentClash?.Path, vm.PositionText));
        Assert.False(vm.PreviousClashCommand.CanExecute(null));
    }

    [Fact]
    public void TabCounts_DropAsClashesAreChosen()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        Assert.Equal((1, 1), (vm.GeneralOpenClashCount, vm.AircraftOpenClashCount));

        Row(vm, "/autoDeleteMode").KeepTheirsCommand.Execute(null);
        Assert.Equal((0, 1), (vm.GeneralOpenClashCount, vm.AircraftOpenClashCount));

        Row(vm, "/aircraft/1/presetCommands/0/command").KeepMineCommand.Execute(null);
        Assert.Equal((0, 0), (vm.GeneralOpenClashCount, vm.AircraftOpenClashCount));
    }

    [Fact]
    public void TwoWay_DifferencesToChoose_ShowBaseDisabledAndNoAutoTags()
    {
        JsonObject mine = Sample();
        mine["name"] = "Mine";
        JsonObject theirs = Sample();
        theirs["primaryApproach"] = "ILS 30";

        ScenarioMergeViewModel vm = Merge(null, mine, theirs);
        vm.ShowBase = true;
        vm.NextClashCommand.Execute(null);

        Assert.True(vm.IsTwoWay);
        Assert.Equal("2 differences to choose", vm.SummaryText);
        Assert.Equal("Choose a side for 2 differences to apply.", vm.FooterText);
        Assert.Equal("difference 1 of 2", vm.PositionText);
        Assert.False(vm.CanShowBase);
        Assert.Equal("Show base (no base version)", vm.ShowBaseLabel);
        Assert.Equal(Captions.TwoWayNote, vm.TwoWayNote);
        Assert.All(vm.Rows, row => Assert.False(row.ShowsAutoTag));
        Assert.All(vm.Rows, row => Assert.False(row.ShowsBase));
    }

    [Fact]
    public void ShowsBase_ClashAlwaysAutoRowOnlyWithShowBase()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel clash = Row(vm, "/autoDeleteMode");
        ScenarioMergeRowViewModel auto = Row(vm, "/name");
        Assert.Equal(("Show base", "base: server version of 2 Oct 2026", true), (vm.ShowBaseLabel, vm.ShowBaseSourceText, vm.CanShowBase));
        Assert.Equal((true, false, true), (clash.ShowsBase, auto.ShowsBase, auto.ShowsAutoTag));
        Assert.Equal("None", clash.BaseText);
        Assert.Equal("mine changed it to OnLanding; theirs changed it to Parked", clash.BaseReason);

        vm.ShowBase = true;

        Assert.Equal((true, true), (clash.ShowsBase, auto.ShowsBase));
    }

    [Fact]
    public void ValueText_AbsentNullAndTime()
    {
        JsonObject mine = Sample();
        mine["primaryApproach"] = "ILS 30";
        mine["aircraft"]![0]!["spawnDelay"] = 480;
        JsonObject theirs = Sample();
        theirs["studentPositionId"] = null;
        JsonObject added = theirs["aircraft"]![0]!.DeepClone().AsObject();
        added["id"] = "01HZZZZZZZZZZZZZZZZZZZZZZZ";
        theirs["aircraft"]!.AsArray().Add(added);

        ScenarioMergeViewModel vm = Merge(Text(Sample()), mine, theirs);

        ScenarioMergeRowViewModel approach = Row(vm, "/primaryApproach");
        Assert.Equal(("(not set)", "ILS 30", "(not set)"), (approach.BaseText, approach.MineText, approach.TheirsText));
        Assert.Equal((ScenarioMergePresence.Added, ScenarioMergePresence.None), (approach.MinePresence, approach.TheirsPresence));
        Assert.Equal("(none)", Row(vm, "/studentPositionId").TheirsText);
        Assert.Equal("8:00", Row(vm, "/aircraft/0/spawnDelay").MineText);
        ScenarioMergeRowViewModel item = Row(vm, "/aircraft/18");
        Assert.Equal((null, "added in theirs"), (item.MineNote, item.TheirsNote));
    }

    [Fact]
    public void OrderRow_LabelledListOrderWithNoEdit()
    {
        JsonObject mine = Sample();
        JsonArray aircraft = mine["aircraft"]!.AsArray();
        JsonNode first = aircraft[0]!;
        aircraft.RemoveAt(0);
        aircraft.Insert(1, first);

        ScenarioMergeViewModel vm = Merge(Text(Sample()), mine, Sample());

        ScenarioMergeRowViewModel order = Row(vm, "/aircraft" + ScenarioMerge.OrderPathSuffix);
        Assert.Equal(("Aircraft order", false), (order.Label, order.CanEdit));
    }

    [Fact]
    public void Choice_PositionalKeyInOneSection_LeavesTheSameKeyInAnotherOpen()
    {
        ScenarioMergeViewModel vm = Merge(Text(WithPositional(Sample(), "A", 1)), WithPositional(Sample(), "B", 2), WithPositional(Sample(), "C", 3));
        ScenarioMergeRowViewModel trigger = Row(vm, "/initializationTriggers/0/command");
        ScenarioMergeRowViewModel bay = Row(vm, "/flightStripConfigurations/0/rack");
        Assert.Equal(("#0", "#0"), (trigger.ItemKey, bay.ItemKey));

        trigger.KeepMineCommand.Execute(null);

        Assert.Equal((false, true), (trigger.IsOpen, bay.IsOpen));
        Assert.False(vm.Choices.ContainsKey(bay.Path));
        Assert.Equal(1, vm.OpenClashCount);
    }

    [Fact]
    public void Create_MalformedMine_ThrowsJsonException()
    {
        var request = new ScenarioMergeRequest(
            "ZOA",
            "01H06NVK7VN8BS7MCDXHKJZ7MQ",
            new(Text(Sample()), "{ not json", Text(Sample())),
            Captions,
            NoWrite
        );

        Assert.ThrowsAny<JsonException>(() => ScenarioMergeViewModel.Create(request));
    }

    [Fact]
    public void Next_GeneralSections_WalkInViewRankNotJsonOrder()
    {
        JsonObject mine = WithPositional(Sample(), "B", 1);
        mine["atc"]![1]!["autoConnect"] = false;
        mine["autoDeleteMode"] = "OnLanding";
        JsonObject theirs = WithPositional(Sample(), "C", 1);
        theirs["atc"]![1]!["autoConnect"] = JsonValue.Create("yes");
        theirs["autoDeleteMode"] = "Parked";
        ScenarioMergeViewModel vm = Merge(Text(WithPositional(Sample(), "A", 1)), mine, theirs);
        Assert.Equal(["/initializationTriggers/0/command", "/atc/1/autoConnect", "/autoDeleteMode"], vm.Rows.Select(row => row.Path));

        List<string?> walked = [];
        for (int i = 0; i < 3; i++)
        {
            vm.NextClashCommand.Execute(null);
            walked.Add(vm.CurrentClash?.Path);
        }

        Assert.Equal(["/autoDeleteMode", "/atc/1/autoConnect", "/initializationTriggers/0/command"], walked);
    }

    [Fact]
    public void Next_EverySectionTheEngineNames_HasARankInViewOrder()
    {
        string[] lists =
        [
            "flightStripConfigurations",
            "overflightGenerators",
            "vfrArrivalGenerators",
            "aircraftGenerators",
            "initializationTriggers",
            "atc",
        ];
        ScenarioMergeViewModel vm = Merge(Text(EverySection(lists, "base")), EverySection(lists, "mine"), EverySection(lists, "theirs"));
        Assert.Equal(8, vm.Rows.Select(row => row.Section).Distinct().Count());

        List<string?> walked = [];
        for (int i = 0; i < 8; i++)
        {
            vm.NextClashCommand.Execute(null);
            walked.Add(vm.CurrentClash?.Section);
        }

        Assert.Equal(
            [
                "General",
                "ATC positions",
                "Initialization triggers",
                "Generators",
                "VFR arrival generators",
                "Overflight generators",
                "Strip bays",
                "Aircraft",
            ],
            walked
        );
    }

    [Fact]
    public void ItemNotes_TwoWay_OnlyInOneSideAndNotInTheOther()
    {
        ScenarioMergeRowViewModel mineOnly = Row(Merge(null, WithExtraAircraft(Sample()), Sample()), "/aircraft/18");
        ScenarioMergeRowViewModel theirsOnly = Row(Merge(null, Sample(), WithExtraAircraft(Sample())), "/aircraft/18");

        Assert.Equal(("only in mine", "not in theirs"), (mineOnly.MineNote, mineOnly.TheirsNote));
        Assert.Equal(("not in mine", "only in theirs"), (theirsOnly.MineNote, theirsOnly.TheirsNote));
    }

    [Fact]
    public void ItemNotes_ThreeWay_RemovedOnOneSide()
    {
        JsonObject dropped = Sample();
        dropped["aircraft"]!.AsArray().RemoveAt(0);

        ScenarioMergeRowViewModel theirsDropped = Row(Merge(Text(Sample()), Sample(), dropped), "/aircraft/0");
        ScenarioMergeRowViewModel mineDropped = Row(Merge(Text(Sample()), dropped.DeepClone().AsObject(), Sample()), "/aircraft/17");

        Assert.Equal((null, "removed in theirs"), (theirsDropped.MineNote, theirsDropped.TheirsNote));
        Assert.Equal(("removed in mine", null), (mineDropped.MineNote, mineDropped.TheirsNote));
    }

    [Fact]
    public void TwoWay_EveryDifferenceChosen_ResolvedTextsAndApplyOn()
    {
        JsonObject mine = Sample();
        mine["name"] = "Mine";
        JsonObject theirs = Sample();
        theirs["primaryApproach"] = "ILS 30";
        ScenarioMergeViewModel vm = Merge(null, mine, theirs);

        Row(vm, "/name").KeepMineCommand.Execute(null);
        Row(vm, "/primaryApproach").KeepTheirsCommand.Execute(null);

        Assert.Equal("All differences resolved: 2 chosen.", vm.SummaryText);
        Assert.Equal("Nothing is saved until you apply.", vm.FooterText);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Choose_AnEdit_Throws()
    {
        ScenarioMergeViewModel vm = SampleMerge();

        Assert.Throws<ArgumentOutOfRangeException>(() => vm.Choose(Row(vm, "/autoDeleteMode"), MergeChoice.Edit(JsonValue.Create("Never"))));
        Assert.False(vm.Choices.ContainsKey("/autoDeleteMode"));
    }

    // Name changed in theirs only, auto-delete and one preset command changed on both sides, a cruise altitude in mine only.
    private static ScenarioMergeViewModel SampleMerge()
    {
        JsonObject mine = WithPreset(Sample(), "FH 020");
        mine["autoDeleteMode"] = "OnLanding";
        mine["aircraft"]![0]!["flightplan"]!["cruiseAltitude"] = 31000;
        JsonObject theirs = WithPreset(Sample(), "FH 030");
        theirs["autoDeleteMode"] = "Parked";
        theirs["name"] = "Theirs";
        return Merge(Text(WithPreset(Sample(), "FH 010")), mine, theirs);
    }

    private static ScenarioMergeViewModel Merge(string? baseText, JsonObject mine, JsonObject theirs) =>
        ScenarioMergeViewModel.Create(
            new ScenarioMergeRequest("ZOA", "01H06NVK7VN8BS7MCDXHKJZ7MQ", new(baseText, Text(mine), Text(theirs)), Captions, NoWrite)
        );

    private static Task NoWrite(string json) => Task.CompletedTask;

    private static ScenarioMergeRowViewModel Row(ScenarioMergeViewModel vm, string path) => Assert.Single(vm.Rows, row => row.Path == path);

    private static JsonObject WithPreset(JsonObject scenario, string command)
    {
        scenario["aircraft"]![1]!["presetCommands"] = new JsonArray(new JsonObject { ["id"] = PresetId, ["command"] = command });
        return scenario;
    }

    private static JsonObject WithExtraAircraft(JsonObject scenario)
    {
        JsonObject added = scenario["aircraft"]![0]!.DeepClone().AsObject();
        added["id"] = "01HZZZZZZZZZZZZZZZZZZZZZZZ";
        scenario["aircraft"]!.AsArray().Add(added);
        return scenario;
    }

    // One differing value in every section, the lists written in reverse view order and the General field last.
    private static JsonObject EverySection(string[] lists, string value)
    {
        JsonObject sample = Sample();
        JsonArray aircraft = sample["aircraft"]!.DeepClone().AsArray();
        aircraft[0]!["probe"] = value;
        var scenario = new JsonObject { ["aircraft"] = aircraft };
        foreach (string list in lists)
        {
            scenario[list] = new JsonArray(new JsonObject { ["probe"] = value });
        }

        scenario["name"] = value;
        return scenario;
    }

    private static JsonObject WithPositional(JsonObject scenario, string command, int rack)
    {
        scenario["initializationTriggers"] = new JsonArray(new JsonObject { ["timeOffset"] = 0, ["command"] = command });
        scenario["flightStripConfigurations"] = new JsonArray(
            new JsonObject
            {
                ["facilityId"] = "OAK",
                ["bayId"] = "1",
                ["rack"] = rack,
                ["aircraftIds"] = new JsonArray(),
            }
        );
        return scenario;
    }

    private static JsonObject AircraftFirst(JsonObject scenario)
    {
        var reordered = new JsonObject { ["aircraft"] = scenario["aircraft"]!.DeepClone() };
        foreach ((string key, JsonNode? value) in scenario.Where(member => member.Key != "aircraft"))
        {
            reordered[key] = value?.DeepClone();
        }

        return reordered;
    }

    private static JsonObject Sample() => JsonNode.Parse(File.ReadAllText(SamplePath))!.AsObject();

    private static string Text(JsonObject scenario) => scenario.ToJsonString();

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No yaat.slnx above {AppContext.BaseDirectory}: the scenario examples are read from the checkout.");
    }
}
