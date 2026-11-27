using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The merge window's grouping over real scenario JSON: the General-tab sections with their field, item and order lines,
/// the aircraft lines aligned by id, the section and line buttons, Only differences, and the whole-scenario commands.
/// </summary>
public class ScenarioMergeBulkTests
{
    private const string ExtraId = "01HZZZZZZZZZZZZZZZZZZZZZZZ";

    private const string TheirsOnlyId = "01HYYYYYYYYYYYYYYYYYYYYYYY";

    private const string PresetId = "01HPRESET00000000000000001";

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

    [Fact]
    public void LineButton_PositionalTriggerKey_LeavesStripBayWithTheSameKeyOpen()
    {
        ScenarioMergeViewModel vm = Merge(Text(WithPositional(Small(), "A", 1)), WithPositional(Small(), "B", 2), WithPositional(Small(), "C", 3));
        ScenarioMergeLineViewModel trigger = Line(vm, "Initialization triggers", "#0");
        ScenarioMergeLineViewModel bay = Line(vm, "Strip bays", "#0");
        Assert.Equal(("0:00 B", "0:00 C"), (trigger.MineText, trigger.TheirsText));

        trigger.KeepMineCommand.Execute(null);

        Assert.Equal((true, false), (trigger.IsMinePressed, bay.IsMinePressed));
        Assert.All(bay.Rows, row => Assert.True(row.IsOpen));
        Assert.All(bay.Rows, row => Assert.False(vm.Choices.ContainsKey(row.Path)));
    }

    [Fact]
    public void KeepTheirsForEveryClash_LeavesAnAutoMineRowOnMine()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeRowViewModel cruise = Row(vm, "/aircraft/0/flightplan/cruiseAltitude");

        vm.KeepTheirsForEveryClashCommand.Execute(null);

        Assert.Equal((MergeRowKind.AutoMine, true, false), (cruise.Kind, cruise.IsMinePressed, vm.Choices.ContainsKey(cruise.Path)));
        Assert.True(Row(vm, "/autoDeleteMode").IsTheirsPressed);
        Assert.True(Row(vm, "/aircraft/1/presetCommands/0/command").IsTheirsPressed);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void TakeTheirsForEverything_TurnsEveryRowToTheirsIncludingTheOrderRow()
    {
        JsonObject mine = Moved(Oak(), "FDX895", 0);
        mine["autoDeleteMode"] = "OnLanding";
        Craft(mine, "NKS2904")["spawnDelay"] = 60;
        JsonObject theirs = Oak();
        theirs["autoDeleteMode"] = "Parked";
        theirs["name"] = "Theirs";
        ScenarioMergeViewModel vm = Merge(Text(Oak()), mine, theirs);
        Assert.Contains(vm.Rows, row => row.IsOrderRow);
        Row(vm, "/name").KeepMineCommand.Execute(null);

        vm.TakeTheirsForEverythingCommand.Execute(null);

        Assert.Equal(vm.Rows.Count, vm.Choices.Count);
        Assert.All(vm.Rows, row => Assert.Same(MergeChoice.KeepTheirs, vm.Choices[row.Path]));
        Assert.All(vm.Rows, row => Assert.True(row.IsTheirsPressed));
        Assert.True(vm.AircraftSection.IsTheirsPressed);
    }

    [Fact]
    public void AircraftKeepMine_RemovedInTheirs_KeepsItInTheMergedText()
    {
        JsonObject theirs = Oak();
        theirs["aircraft"]!.AsArray().Remove(Craft(theirs, "FDX895"));
        ScenarioMergeViewModel vm = Merge(Text(Oak()), Oak(), theirs);
        ScenarioMergeAircraftLineViewModel line = Aircraft(vm, "FDX895");
        Assert.Equal(("removed in theirs", true, false), (line.StatusText, line.HasMine, line.HasTheirs));
        Assert.Null(line.Theirs);

        line.KeepMineCommand.Execute(null);

        Assert.True(line.IsMinePressed);
        JsonArray merged = JsonNode.Parse(vm.Plan.Apply(vm.Choices))!["aircraft"]!.AsArray();
        Assert.Contains(merged, aircraft => aircraft!["aircraftId"]!.GetValue<string>() == "FDX895");
    }

    [Fact]
    public void TwoWay_AircraftOnOneSide_ReadsOnlyInMine()
    {
        ScenarioMergeViewModel vm = Merge(null, WithExtra(Oak(), 5), Oak());

        ScenarioMergeAircraftLineViewModel extra = Assert.Single(vm.AircraftLines, line => line.ItemKey == ExtraId);

        Assert.Equal(("only in mine", true, false), (extra.StatusText, extra.HasMine, extra.HasTheirs));
        Assert.All(vm.AircraftLines.Where(line => line != extra), line => Assert.Equal("same", line.StatusText));
    }

    [Fact]
    public void AircraftLines_Mine31Theirs30_AlignedByIdWithPlaceholders()
    {
        JsonObject mine = WithExtra(Oak(), 5);
        JsonObject theirs = Oak();
        theirs["aircraft"]!.AsArray().Remove(Craft(theirs, "FDX440"));
        JsonObject added = Craft(Oak(), "N3796G").DeepClone().AsObject();
        added["id"] = TheirsOnlyId;
        added["aircraftId"] = "N4471K";
        theirs["aircraft"]!.AsArray().Add(added);
        Assert.Equal((31, 30), (mine["aircraft"]!.AsArray().Count, theirs["aircraft"]!.AsArray().Count));

        ScenarioMergeViewModel vm = Merge(Text(Oak()), mine, theirs);

        List<string> mineIds = [.. mine["aircraft"]!.AsArray().Select(aircraft => aircraft!["id"]!.GetValue<string>())];
        Assert.Equal([.. mineIds, TheirsOnlyId], vm.AircraftLines.Select(line => line.ItemKey));
        Assert.All(vm.AircraftLines.Where(line => line.HasMine && line.HasTheirs), line => Assert.Equal(line.Mine!.Id, line.Theirs!.Id));
        Assert.Equal((true, false, "added in mine"), Placeholder(vm.AircraftLines[5]));
        Assert.Equal((true, false, "removed in theirs"), Placeholder(Aircraft(vm, "FDX440")));
        Assert.Equal((false, true, "added in theirs"), Placeholder(vm.AircraftLines[^1]));
        Assert.Equal("N4471K", vm.AircraftLines[^1].Theirs!.Callsign);
    }

    [Fact]
    public void OnlyDifferences_HidesTheIdenticalAtcLines()
    {
        JsonObject mine = Small();
        mine["atc"]![1]!["autoConnect"] = false;
        ScenarioMergeViewModel vm = Merge(Text(Small()), mine, Small());
        ScenarioMergeSectionViewModel atc = Section(vm, "ATC positions");
        Assert.Equal([true, true], atc.Lines.Select(line => line.IsVisible));

        vm.OnlyDifferences = true;

        Assert.Equal([false, true], atc.Lines.Select(line => line.IsVisible));
        Assert.All(vm.AircraftLines, line => Assert.False(line.IsVisible));
        Assert.Equal(["ATC positions"], vm.Sections.Where(section => section.IsVisible).Select(section => section.Name));
    }

    [Fact]
    public void SectionButton_SetsEveryDifferingRowAutoIncluded_PressedOnlyWhileAllResolveToThatSide()
    {
        ScenarioMergeViewModel vm = SampleMerge();
        ScenarioMergeSectionViewModel general = Section(vm, "General");
        Assert.Equal(["/name", "/autoDeleteMode"], general.Rows.Select(row => row.Path));
        Assert.Equal((false, false), (general.IsMinePressed, general.IsTheirsPressed));

        general.KeepMineCommand.Execute(null);

        Assert.All(general.Rows, row => Assert.Same(MergeChoice.KeepMine, vm.Choices[row.Path]));
        Assert.Equal((true, false), (general.IsMinePressed, general.IsTheirsPressed));
        Assert.Equal(1, vm.OpenClashCount);

        Row(vm, "/name").KeepTheirsCommand.Execute(null);

        Assert.Equal((false, false), (general.IsMinePressed, general.IsTheirsPressed));
    }

    [Fact]
    public void OrderLine_ListsEachSidesItemsInItsOrderWithMovedItemsMarked()
    {
        ScenarioMergeViewModel vm = Merge(Text(Oak()), Moved(Oak(), "FDX895", 0), Oak());

        ScenarioMergeLineViewModel order = Assert.Single(vm.AircraftSection.Lines, line => line.Order is not null);

        Assert.Equal("Aircraft order", order.Label);
        List<string> callsigns = [.. Oak()["aircraft"]!.AsArray().Select(aircraft => aircraft!["aircraftId"]!.GetValue<string>())];
        Assert.Equal(["FDX895", .. callsigns.Take(29)], order.Order!.Mine.Select(item => item.Text));
        Assert.Equal(callsigns, order.Order.Theirs.Select(item => item.Text));
        Assert.Equal(new ScenarioMergeOrderItem(1, "FDX895", 30), order.Order.Mine[0]);
        Assert.Equal(new ScenarioMergeOrderItem(2, "SWA5456", null), order.Order.Mine[1]);
        Assert.Single(order.Order.Mine, item => item.MovedFrom is not null);
        Assert.All(order.Order.Theirs, item => Assert.Null(item.MovedFrom));
    }

    [Fact]
    public void Next_AircraftFieldRowSelectsItsLine_OrderRowKeepsTheSelection()
    {
        JsonObject mine = Moved(Oak(), "FDX895", 0);
        Craft(mine, "NKS2904")["spawnDelay"] = 60;
        JsonObject theirs = Moved(Oak(), "SWA5456", 29);
        Craft(theirs, "NKS2904")["spawnDelay"] = 120;
        ScenarioMergeViewModel vm = Merge(Text(Oak()), mine, theirs);

        vm.NextClashCommand.Execute(null);

        Assert.Equal("NKS2904", vm.SelectedAircraftLine?.Mine?.Callsign);
        Assert.Same(vm.SelectedAircraftLine, vm.CurrentLine);

        vm.NextClashCommand.Execute(null);

        Assert.True(vm.CurrentClash?.IsOrderRow);
        Assert.Equal(("NKS2904", ScenarioMergeTab.Aircraft), (vm.SelectedAircraftLine?.Mine?.Callsign, vm.SelectedTab));
    }

    [Fact]
    public void Next_AnotherClashOfTheSameAircraft_SelectsItsLineAgainAfterTheAuthorPickedAnother()
    {
        JsonObject mine = Oak();
        Craft(mine, "NKS2904")["spawnDelay"] = 60;
        Craft(mine, "NKS2904")["aircraftType"] = "A321/L";
        JsonObject theirs = Oak();
        Craft(theirs, "NKS2904")["spawnDelay"] = 120;
        Craft(theirs, "NKS2904")["aircraftType"] = "A319/L";
        ScenarioMergeViewModel vm = Merge(Text(Oak()), mine, theirs);
        vm.NextClashCommand.Execute(null);
        ScenarioMergeAircraftLineViewModel nks = Aircraft(vm, "NKS2904");
        Assert.Same(nks, vm.SelectedAircraftLine);

        vm.SelectedAircraftLine = Aircraft(vm, "FDX895");
        Assert.Equal(Aircraft(vm, "FDX895").ItemKey, vm.SelectedAircraftId);
        vm.NextClashCommand.Execute(null);

        Assert.Same(nks, vm.SelectedAircraftLine);
        Assert.Equal(nks.ItemKey, vm.SelectedAircraftId);
    }

    [Fact]
    public void OrderLine_TwoWaySwap_MarksTheSameItemMovedOnBothPanes()
    {
        ScenarioMergeViewModel vm = Merge(null, Moved(Small(), "SWA919", 0), Small());

        ScenarioMergeLineViewModel order = Assert.Single(vm.AircraftSection.Lines, line => line.Order is not null);

        ScenarioMergeOrderItem mineMoved = Assert.Single(order.Order!.Mine, item => item.MovedFrom is not null);
        ScenarioMergeOrderItem theirsMoved = Assert.Single(order.Order.Theirs, item => item.MovedFrom is not null);
        Assert.Equal(mineMoved.Text, theirsMoved.Text);
    }

    [Fact]
    public void KeepMineForEveryClash_LeavesAnAutoTheirsRowOnTheirs()
    {
        ScenarioMergeViewModel vm = SampleMerge();

        vm.KeepMineForEveryClashCommand.Execute(null);

        ScenarioMergeRowViewModel name = Row(vm, "/name");
        Assert.Equal((MergeRowKind.AutoTheirs, true, false), (name.Kind, name.IsTheirsPressed, vm.Choices.ContainsKey(name.Path)));
        Assert.True(Row(vm, "/autoDeleteMode").IsMinePressed);
        Assert.True(Row(vm, "/aircraft/1/presetCommands/0/command").IsMinePressed);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void TakeMineForEverything_TurnsEveryRowToMineIncludingTheOrderRow()
    {
        JsonObject mine = Oak();
        mine["autoDeleteMode"] = "OnLanding";
        JsonObject theirs = Moved(Oak(), "FDX895", 0);
        theirs["autoDeleteMode"] = "Parked";
        theirs["name"] = "Theirs";
        ScenarioMergeViewModel vm = Merge(Text(Oak()), mine, theirs);
        Assert.Contains(vm.Rows, row => row.IsOrderRow);
        Row(vm, "/name").KeepTheirsCommand.Execute(null);

        vm.TakeMineForEverythingCommand.Execute(null);

        Assert.Equal(vm.Rows.Count, vm.Choices.Count);
        Assert.All(vm.Rows, row => Assert.Same(MergeChoice.KeepMine, vm.Choices[row.Path]));
        Assert.All(vm.Rows, row => Assert.True(row.IsMinePressed));
    }

    [Fact]
    public void SectionButton_AllRowsAutoMine_ShowsMinePressedWithNoChoice()
    {
        JsonObject mine = Small();
        mine["name"] = "Mine";
        mine["autoDeleteMode"] = "OnLanding";
        ScenarioMergeViewModel vm = Merge(Text(Small()), mine, Small());

        ScenarioMergeSectionViewModel general = Section(vm, "General");

        Assert.Equal(2, general.Rows.Count);
        Assert.Empty(vm.Choices);
        Assert.Equal((true, false), (general.IsMinePressed, general.IsTheirsPressed));
    }

    // Name changed in theirs only, auto-delete and one preset command changed on both sides, a cruise altitude in mine only.
    private static ScenarioMergeViewModel SampleMerge()
    {
        JsonObject mine = WithPreset(Small(), "FH 020");
        mine["autoDeleteMode"] = "OnLanding";
        mine["aircraft"]![0]!["flightplan"]!["cruiseAltitude"] = 31000;
        JsonObject theirs = WithPreset(Small(), "FH 030");
        theirs["autoDeleteMode"] = "Parked";
        theirs["name"] = "Theirs";
        return Merge(Text(WithPreset(Small(), "FH 010")), mine, theirs);
    }

    private static ScenarioMergeViewModel Merge(string? baseText, JsonObject mine, JsonObject theirs) =>
        ScenarioMergeViewModel.Create(
            new ScenarioMergeRequest("ZOA", "01H08FSWF5NCDXTQMQ3BWB0BND", new(baseText, Text(mine), Text(theirs)), Captions, NoWrite)
        );

    private static Task NoWrite(string json) => Task.CompletedTask;

    private static ScenarioMergeRowViewModel Row(ScenarioMergeViewModel vm, string path) => Assert.Single(vm.Rows, row => row.Path == path);

    private static ScenarioMergeSectionViewModel Section(ScenarioMergeViewModel vm, string name) =>
        Assert.Single(vm.Sections, section => section.Name == name);

    private static ScenarioMergeLineViewModel Line(ScenarioMergeViewModel vm, string section, string itemKey) =>
        Assert.Single(Section(vm, section).Lines, line => line.ItemKey == itemKey);

    private static ScenarioMergeAircraftLineViewModel Aircraft(ScenarioMergeViewModel vm, string callsign) =>
        Assert.Single(vm.AircraftLines, line => (line.Mine ?? line.Theirs)?.Callsign == callsign);

    private static (bool, bool, string) Placeholder(ScenarioMergeAircraftLineViewModel line) =>
        (line.Mine is not null, line.Theirs is not null, line.StatusText);

    private static JsonObject Craft(JsonObject scenario, string callsign) =>
        scenario["aircraft"]!.AsArray().Single(aircraft => aircraft!["aircraftId"]!.GetValue<string>() == callsign)!.AsObject();

    private static JsonObject Moved(JsonObject scenario, string callsign, int index)
    {
        JsonArray aircraft = scenario["aircraft"]!.AsArray();
        JsonObject moved = Craft(scenario, callsign);
        aircraft.Remove(moved);
        aircraft.Insert(index, moved);
        return scenario;
    }

    private static JsonObject WithExtra(JsonObject scenario, int index)
    {
        JsonObject added = scenario["aircraft"]![0]!.DeepClone().AsObject();
        added["id"] = ExtraId;
        added["aircraftId"] = "N172SP";
        scenario["aircraft"]!.AsArray().Insert(index, added);
        return scenario;
    }

    private static JsonObject WithPreset(JsonObject scenario, string command)
    {
        scenario["aircraft"]![1]!["presetCommands"] = new JsonArray(new JsonObject { ["id"] = PresetId, ["command"] = command });
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

    // S1-OAK-1: 18 aircraft, two ATC positions, no triggers, generators or strip bays.
    private static JsonObject Small() => Example("01H06NVK7VN8BS7MCDXHKJZ7MQ.json");

    // The OAK scenario holding FDX895: 30 aircraft, four ATC positions and 13 initialization triggers.
    private static JsonObject Oak() => Example("01H08FSWF5NCDXTQMQ3BWB0BND.json");

    private static JsonObject Example(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, file)))!.AsObject();

    private static string Text(JsonObject scenario) => scenario.ToJsonString();
}
