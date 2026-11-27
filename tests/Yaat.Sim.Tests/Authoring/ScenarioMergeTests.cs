using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioMerge"/> plans a three-way merge of scenario JSON (two-way without a base): a change on one side merges
/// by itself, only a real clash asks, and <see cref="ScenarioMergePlan.Apply"/> writes the chosen values keeping every field.
/// </summary>
public class ScenarioMergeTests
{
    private const string AddedId = "01HZZZZZZZZZZZZZZZZZZZZZZZ";

    private static readonly (string Id, string Command)[] Presets =
    [
        ("01HPRESET00000000000000001", "FH 010"),
        ("01HPRESET00000000000000002", "CM 3000"),
        ("01HPRESET00000000000000003", "SPD 210"),
    ];

    private static readonly string SamplePath = Path.Combine(
        TickRecorder.FindRepoRoot(),
        "docs",
        "atctrainer-scenario-examples",
        "01H06NVK7VN8BS7MCDXHKJZ7MQ.json"
    );

    private static readonly Dictionary<string, MergeChoice> NoChoices = [];

    public ScenarioMergeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Plan_MineOnlyChange_AutoMergesMine()
    {
        JsonObject mine = Sample();
        mine["aircraft"]![0]!["flightplan"]!["cruiseAltitude"] = 31000;

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(Sample()));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(MergeRowKind.AutoMine, row.Kind);
        Assert.Equal("/aircraft/0/flightplan/cruiseAltitude", row.Path);
        Assert.Equal("Cruise altitude (ft)", row.Label);
        Assert.Equal(1, plan.AutoMergedCount);
        Assert.Equal(31000, Merged(plan)["aircraft"]![0]!["flightplan"]!["cruiseAltitude"]!.GetValue<int>());
    }

    [Fact]
    public void Plan_TheirsOnlyChange_AutoMergesTheirs()
    {
        JsonObject theirs = Sample();
        theirs["primaryAirportId"] = "SFO";

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(Sample()), Text(theirs));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(MergeRowKind.AutoTheirs, row.Kind);
        Assert.Equal(("/primaryAirportId", "General", "Primary airport"), (row.Path, row.Section, row.Label));
        Assert.Equal("SFO", Merged(plan)["primaryAirportId"]!.GetValue<string>());
    }

    [Fact]
    public void Plan_SameFieldChangedBothSides_IsAClash()
    {
        JsonObject mine = Sample();
        mine["name"] = "Mine";
        JsonObject theirs = Sample();
        theirs["name"] = "Theirs";

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(MergeRowKind.Clash, row.Kind);
        Assert.False(plan.IsTwoWay);
        Assert.Equal(Sample()["name"]!.GetValue<string>(), row.Base!.GetValue<string>());
        Assert.Equal("Mine", row.Mine!.GetValue<string>());
        Assert.Equal("Theirs", row.Theirs!.GetValue<string>());
        Assert.Equal(0, plan.AutoMergedCount);
    }

    [Fact]
    public void Plan_SameChangeBothSides_HasNoRow()
    {
        JsonObject mine = Sample();
        mine["aircraft"]![2]!["aircraftId"] = "UAL1";
        JsonObject theirs = Sample();
        theirs["aircraft"]![2]!["aircraftId"] = "UAL1";

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        Assert.Empty(plan.Rows);
        Assert.Equal("UAL1", Merged(plan)["aircraft"]![2]!["aircraftId"]!.GetValue<string>());
    }

    // Also folds the case table's removals: theirs removing an aircraft mine left alone merges as theirs; theirs removing one
    // mine changed is a clash on the whole aircraft.
    [Fact]
    public void Plan_AircraftAddedOnOneSide_IsOneItemRow()
    {
        JsonObject mine = Sample();
        mine["aircraft"]![5]!["aircraftId"] = "DAL55";
        JsonObject theirs = Sample();
        JsonArray theirAircraft = theirs["aircraft"]!.AsArray();
        string untouchedRemovedId = theirAircraft[4]!["id"]!.GetValue<string>();
        string changedRemovedId = theirAircraft[5]!["id"]!.GetValue<string>();
        JsonObject added = theirAircraft[0]!.DeepClone().AsObject();
        added["id"] = AddedId;
        added["aircraftId"] = "N123AB";
        theirAircraft.Add(added);
        theirAircraft.RemoveAt(5);
        theirAircraft.RemoveAt(4);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        Assert.Equal(
            [
                ("/aircraft/4", untouchedRemovedId, MergeRowKind.AutoTheirs),
                ("/aircraft/5", changedRemovedId, MergeRowKind.Clash),
                ("/aircraft/18", AddedId, MergeRowKind.AutoTheirs),
            ],
            plan.Rows.Select(r => (r.Path, r.ItemKey!, r.Kind))
        );
        MergeRow addedRow = plan.Rows[2];
        Assert.Equal(("Aircraft", "Aircraft"), (addedRow.Section, addedRow.Label));
        Assert.Null(addedRow.Mine);
        Assert.Equal("N123AB", addedRow.Theirs!["aircraftId"]!.GetValue<string>());
        Assert.Equal((1, 2), (plan.ClashCount, plan.AutoMergedCount));

        JsonArray merged = JsonNode.Parse(plan.Apply(plan.ChooseItem("Aircraft", changedRemovedId, MergeChoiceKind.KeepMine)))![
            "aircraft"
        ]!.AsArray();
        List<string> ids = [.. merged.Select(a => a!["id"]!.GetValue<string>())];
        Assert.Equal(18, ids.Count);
        Assert.DoesNotContain(untouchedRemovedId, ids);
        Assert.Equal(AddedId, ids[^1]);
        Assert.Equal("DAL55", merged.Single(a => a!["id"]!.GetValue<string>() == changedRemovedId)!["aircraftId"]!.GetValue<string>());
    }

    // Also the case table's two-way identical change: no row.
    [Fact]
    public void Plan_NoBase_EveryDifferenceIsAClash()
    {
        JsonObject mine = Sample();
        mine["name"] = "Mine";
        mine["primaryApproach"] = "ILS 30";
        JsonObject theirs = Sample();
        theirs["aircraft"]![0]!["flightplan"]!["route"] = "OAK6 OAK DIRECT";
        theirs["primaryApproach"] = "ILS 30";
        theirs["unmodelledRootField"] = "theirs only";

        ScenarioMergePlan plan = ScenarioMerge.Plan(null, Text(mine), Text(theirs));

        Assert.True(plan.IsTwoWay);
        Assert.Equal(["/name", "/aircraft/0/flightplan/route", "/unmodelledRootField"], plan.Rows.Select(r => r.Path));
        Assert.Null(plan.Rows[2].Mine);
        Assert.All(plan.Rows, r => Assert.Equal(MergeRowKind.Clash, r.Kind));
        Assert.All(plan.Rows, r => Assert.Null(r.Base));
        Assert.Equal(0, plan.AutoMergedCount);
    }

    [Fact]
    public void Plan_ListItemWithoutId_MatchesByPosition()
    {
        JsonObject baseScenario = WithTriggers(Sample());
        JsonObject mine = WithTriggers(Sample());
        mine["initializationTriggers"]![1]!["command"] = "PAUSE";
        JsonObject theirs = WithTriggers(Sample());
        theirs["initializationTriggers"]![0]!["timeOffset"] = 30;

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(baseScenario), Text(mine), Text(theirs));

        Assert.Equal(
            [
                ("/initializationTriggers/0/timeOffset", "Initialization triggers", "Time", "#0", MergeRowKind.AutoTheirs),
                ("/initializationTriggers/1/command", "Initialization triggers", "Command", "#1", MergeRowKind.AutoMine),
            ],
            plan.Rows.Select(r => (r.Path, r.Section, r.Label, r.ItemKey!, r.Kind))
        );
        JsonArray triggers = Merged(plan)["initializationTriggers"]!.AsArray();
        Assert.Equal(30, triggers[0]!["timeOffset"]!.GetValue<int>());
        Assert.Equal("PAUSE", triggers[1]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Plan_ListWithAnItemWithoutId_MatchesByPosition() => AssertTriggersMatchByPosition("01HTRIGGER0000000000000001", null);

    [Fact]
    public void Plan_ListWithADuplicateId_MatchesByPosition() =>
        AssertTriggersMatchByPosition("01HTRIGGER0000000000000001", "01HTRIGGER0000000000000001");

    [Fact]
    public void Plan_MineOnlyReorderOfIdList_IsOneAutoRow_ResolvedToMine()
    {
        JsonObject mine = ReorderAircraft(Sample(), Reverse);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(Sample()));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(("/aircraft#order", "Aircraft", MergeRowKind.AutoMine), (row.Path, row.Section, row.Kind));
        Assert.Equal((0, 1), (plan.ClashCount, plan.AutoMergedCount));
        Assert.Equal(AircraftIds(mine), Ids(row.Mine!.AsArray()));
        Assert.Equal(AircraftIds(mine), AircraftIds(Merged(plan)));
    }

    [Fact]
    public void Plan_TheirsOnlyReorderOfIdList_IsOneAutoRow_ResolvedToTheirs()
    {
        JsonObject theirs = ReorderAircraft(Sample(), Reverse);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(Sample()), Text(theirs));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(("/aircraft#order", "Aircraft", MergeRowKind.AutoTheirs), (row.Path, row.Section, row.Kind));
        Assert.Equal(AircraftIds(Sample()), Ids(row.Base!.AsArray()));
        Assert.Equal(AircraftIds(Sample()), Ids(row.Mine!.AsArray()));
        Assert.Equal(AircraftIds(theirs), Ids(row.Theirs!.AsArray()));
        Assert.Equal(AircraftIds(theirs), AircraftIds(Merged(plan)));
    }

    [Fact]
    public void Plan_BothSidesReorderDifferently_IsOneClash_AndChoosingTheirsWritesTheirsOrder()
    {
        JsonObject mine = ReorderAircraft(Sample(), Reverse);
        JsonObject theirs = ReorderAircraft(Sample(), FirstToLast);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(("/aircraft#order", MergeRowKind.Clash), (row.Path, row.Kind));
        Assert.Equal(1, plan.ClashCount);
        JsonNode merged = JsonNode.Parse(plan.Apply(plan.ChooseAll(MergeChoiceKind.KeepTheirs)))!;
        Assert.Equal(AircraftIds(theirs), AircraftIds(merged));
    }

    [Fact]
    public void Plan_BothSidesSameReorder_HasNoRow()
    {
        JsonObject mine = ReorderAircraft(Sample(), Reverse);
        JsonObject theirs = ReorderAircraft(Sample(), Reverse);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        Assert.Empty(plan.Rows);
        Assert.Equal(AircraftIds(mine), AircraftIds(Merged(plan)));
    }

    [Fact]
    public void Apply_EditChoiceOnTheOrderRow_Throws()
    {
        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(Sample()), Text(ReorderAircraft(Sample(), Reverse)));
        var choices = new Dictionary<string, MergeChoice> { ["/aircraft#order"] = MergeChoice.Edit(JsonValue.Create("reversed")) };
        Assert.Equal("/aircraft#order", Assert.Single(plan.Rows).Path);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => plan.Apply(choices));

        Assert.Contains("/aircraft#order", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TheirsReorderPlusMineAddedAircraft_KeepsTheAddedOneLast()
    {
        JsonObject theirs = ReorderAircraft(Sample(), Reverse);
        JsonObject mine = Sample();
        JsonArray mineAircraft = mine["aircraft"]!.AsArray();
        JsonObject added = mineAircraft[0]!.DeepClone().AsObject();
        added["id"] = AddedId;
        added["aircraftId"] = "N123AB";
        mineAircraft.Add(added);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        Assert.Equal(
            [("/aircraft/18", MergeRowKind.AutoMine), ("/aircraft#order", MergeRowKind.AutoTheirs)],
            plan.Rows.Select(r => (r.Path, r.Kind))
        );
        List<string> expected = [.. AircraftIds(theirs), AddedId];
        Assert.Equal(expected, AircraftIds(Merged(plan)));
    }

    // A nested id-matched list keeps its item's key and the editor's label, so ChooseItem can resolve it for its aircraft.
    [Fact]
    public void Plan_TheirsReordersOneAircraftsPresetCommands_RowCarriesTheAircraftKeyAndLabel()
    {
        string aircraftId = Sample()["aircraft"]![0]!["id"]!.GetValue<string>();
        JsonObject theirs = WithPresetCommands(Sample(), Enumerable.Reverse(Presets));

        ScenarioMergePlan plan = ScenarioMerge.Plan(
            Text(WithPresetCommands(Sample(), Presets)),
            Text(WithPresetCommands(Sample(), Presets)),
            Text(theirs)
        );

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(
            ("/aircraft/0/presetCommands#order", aircraftId, "Preset commands", MergeRowKind.AutoTheirs),
            (row.Path, row.ItemKey!, row.Label, row.Kind)
        );
        List<string> expected = [.. Presets.Reverse().Select(p => p.Id)];
        Assert.Equal(expected, ItemIds(Merged(plan)["aircraft"]![0]!["presetCommands"]!.AsArray()));
    }

    [Fact]
    public void Plan_BothSidesReorderOneAircraftsPresetCommands_ChooseItemResolvesIt()
    {
        string aircraftId = Sample()["aircraft"]![0]!["id"]!.GetValue<string>();
        JsonObject mine = WithPresetCommands(Sample(), Enumerable.Reverse(Presets));
        JsonObject theirs = WithPresetCommands(Sample(), [Presets[1], Presets[2], Presets[0]]);

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(WithPresetCommands(Sample(), Presets)), Text(mine), Text(theirs));

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(("/aircraft/0/presetCommands#order", MergeRowKind.Clash), (row.Path, row.Kind));
        IReadOnlyDictionary<string, MergeChoice> choices = plan.ChooseItem("Aircraft", aircraftId, MergeChoiceKind.KeepTheirs);
        Assert.Contains("/aircraft/0/presetCommands#order", choices.Keys);
        List<string> expected = [Presets[1].Id, Presets[2].Id, Presets[0].Id];
        Assert.Equal(expected, ItemIds(JsonNode.Parse(plan.Apply(choices))!["aircraft"]![0]!["presetCommands"]!.AsArray()));
    }

    // An id both sides added is no reorder: the order rows cover the ids every side holds, so mine's one-sided reorder still
    // merges by itself.
    [Fact]
    public void Plan_OneSidedReorderPlusTheSameIdAddedOnBothSides_IsAnAutoRow()
    {
        ScenarioMergePlan plan = ScenarioMerge.Plan(
            Text(WithAircraft("A", "B")),
            Text(WithAircraft("B", "A", "X")),
            Text(WithAircraft("A", "B", "X"))
        );

        MergeRow row = Assert.Single(plan.Rows);
        Assert.Equal(("/aircraft#order", MergeRowKind.AutoMine), (row.Path, row.Kind));
        List<string> expected = ["B", "A", "X"];
        Assert.Equal(expected, AircraftIds(Merged(plan)));
    }

    [Fact]
    public void Apply_ChoiceForUnknownPath_Throws()
    {
        ScenarioMergePlan plan = NameClash();
        var choices = new Dictionary<string, MergeChoice> { ["/name"] = MergeChoice.KeepMine, ["/nowhere"] = MergeChoice.KeepTheirs };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => plan.Apply(choices));

        Assert.Contains("/nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_UnresolvedClash_Throws()
    {
        ScenarioMergePlan plan = NameClash();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => plan.Apply(NoChoices));

        Assert.Contains("/name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_EditChoice_WritesTheEditedValue()
    {
        ScenarioMergePlan plan = NameClash();

        string merged = plan.Apply(new Dictionary<string, MergeChoice> { ["/name"] = MergeChoice.Edit(JsonValue.Create("Edited")) });

        Assert.Equal("Edited", JsonNode.Parse(merged)!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_KeepsAnUnmodeledField()
    {
        JsonObject baseScenario = WithUnmodelled(Sample());
        JsonObject mine = WithUnmodelled(Sample());
        mine["name"] = "Mine";
        JsonObject theirs = WithUnmodelled(Sample());
        theirs["aircraft"]![0]!["startingConditions"]!["unmodelledStartField"] = 42.5;

        JsonNode merged = Merged(ScenarioMerge.Plan(Text(baseScenario), Text(mine), Text(theirs)));

        Assert.Equal("kept", merged["unmodelledRootField"]!["nested"]!.GetValue<string>());
        Assert.Equal(3, merged["aircraft"]![0]!["unmodelledAircraftField"]!.AsArray().Count);
        Assert.Equal(42.5, merged["aircraft"]![0]!["startingConditions"]!["unmodelledStartField"]!.GetValue<double>());
        Assert.Equal("Mine", merged["name"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_Result_ReadsBackWithoutWarnings()
    {
        JsonObject mine = Sample();
        mine["name"] = "Mine";
        mine["aircraft"]![1]!["aircraftId"] = "SWA1111";
        JsonObject theirs = Sample();
        theirs["name"] = "Theirs";
        theirs["aircraft"]![3]!["flightplan"]!["route"] = "OAK6 OAK DIRECT";
        theirs["aircraft"]!.AsArray().RemoveAt(7);
        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));

        string merged = plan.Apply(plan.ChooseAll(MergeChoiceKind.KeepTheirs));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(merged), JsonNode.Parse(ScenarioDocument.Read(merged).Write())));
        ScenarioLoadResult loaded = ScenarioLoader.Load(merged, new TestAirportGroundData(), new Random(0), MagneticDeclination.EvaluationDateUtc);
        Assert.Empty(loaded.Warnings);
        Assert.Equal("SWA1111", JsonNode.Parse(merged)!["aircraft"]![1]!["aircraftId"]!.GetValue<string>());
        Assert.Equal(17, loaded.AllAircraftStates.Count());
    }

    [Fact]
    public void ChooseSection_ResolvesOnlyThatSectionsClashes()
    {
        (ScenarioMergePlan plan, _) = AircraftAndNameClashes();

        IReadOnlyDictionary<string, MergeChoice> aircraftChoices = plan.ChooseSection("Aircraft", MergeChoiceKind.KeepTheirs);

        Assert.Equal(
            ["/aircraft/0/aircraftId", "/aircraft/0/aircraftType", "/aircraft/1/aircraftId"],
            aircraftChoices.Keys.Order(StringComparer.Ordinal)
        );
        Assert.All(aircraftChoices.Values, c => Assert.Equal(MergeChoiceKind.KeepTheirs, c.Kind));
        IReadOnlyDictionary<string, MergeChoice> all = plan.ChooseAll(MergeChoiceKind.KeepMine);
        Assert.Equal(4, all.Count);
        Assert.Contains(plan.Rows, r => (r.Path == AutoMinePath) && (r.Kind == MergeRowKind.AutoMine));
        Assert.DoesNotContain(AutoMinePath, all.Keys);
        Assert.DoesNotContain(AutoMinePath, aircraftChoices.Keys);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => plan.Apply(aircraftChoices));
        Assert.Contains("/name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChooseItem_ResolvesOnlyThatAircraftsClashes()
    {
        (ScenarioMergePlan plan, string firstId) = AircraftAndNameClashes();

        IReadOnlyDictionary<string, MergeChoice> choices = plan.ChooseItem("Aircraft", firstId, MergeChoiceKind.KeepMine);

        Assert.Equal(["/aircraft/0/aircraftId", "/aircraft/0/aircraftType"], choices.Keys.Order(StringComparer.Ordinal));
        Assert.All(choices.Values, c => Assert.Equal(MergeChoiceKind.KeepMine, c.Kind));
    }

    [Fact]
    public void ChooseItem_PositionKey_TouchesOnlyItsSection()
    {
        JsonObject mine = WithStripBay(WithTriggers(Sample()));
        mine["initializationTriggers"]![0]!["command"] = "MINE";
        mine["flightStripConfigurations"]![0]!["rack"] = 2;
        JsonObject theirs = WithStripBay(WithTriggers(Sample()));
        theirs["initializationTriggers"]![0]!["command"] = "THEIRS";
        theirs["flightStripConfigurations"]![0]!["rack"] = 3;
        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(WithStripBay(WithTriggers(Sample()))), Text(mine), Text(theirs));

        IReadOnlyDictionary<string, MergeChoice> triggers = plan.ChooseItem("Initialization triggers", "#0", MergeChoiceKind.KeepMine);
        IReadOnlyDictionary<string, MergeChoice> bays = plan.ChooseItem("Strip bays", "#0", MergeChoiceKind.KeepTheirs);

        Assert.Equal(2, plan.ClashCount);
        Assert.Equal(["/initializationTriggers/0/command"], triggers.Keys);
        Assert.Equal(["/flightStripConfigurations/0/rack"], bays.Keys);
    }

    private const string AutoMinePath = "/aircraft/2/aircraftId";

    private static (ScenarioMergePlan Plan, string FirstId) AircraftAndNameClashes()
    {
        JsonObject mine = Sample();
        JsonObject theirs = Sample();
        mine["aircraft"]![2]!["aircraftId"] = "AUTO2";
        mine["name"] = "Mine";
        theirs["name"] = "Theirs";
        foreach ((JsonObject side, string suffix) in new[] { (mine, "M"), (theirs, "T") })
        {
            side["aircraft"]![0]!["aircraftId"] = "AAL1" + suffix;
            side["aircraft"]![0]!["aircraftType"] = "A321/L" + suffix;
            side["aircraft"]![1]!["aircraftId"] = "AAL2" + suffix;
        }

        return (ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs)), Sample()["aircraft"]![0]!["id"]!.GetValue<string>());
    }

    private static ScenarioMergePlan NameClash()
    {
        JsonObject mine = Sample();
        mine["name"] = "Mine";
        JsonObject theirs = Sample();
        theirs["name"] = "Theirs";
        return ScenarioMerge.Plan(Text(Sample()), Text(mine), Text(theirs));
    }

    // Mine changes the second trigger's command, theirs the first's time: matched by position, each merges by itself.
    private static void AssertTriggersMatchByPosition(string? firstId, string? secondId)
    {
        JsonObject mine = WithTriggers(Sample(), firstId, secondId);
        mine["initializationTriggers"]![1]!["command"] = "PAUSE";
        JsonObject theirs = WithTriggers(Sample(), firstId, secondId);
        theirs["initializationTriggers"]![0]!["timeOffset"] = 30;

        ScenarioMergePlan plan = ScenarioMerge.Plan(Text(WithTriggers(Sample(), firstId, secondId)), Text(mine), Text(theirs));

        Assert.Equal(
            [
                ("/initializationTriggers/0/timeOffset", "#0", MergeRowKind.AutoTheirs),
                ("/initializationTriggers/1/command", "#1", MergeRowKind.AutoMine),
            ],
            plan.Rows.Select(r => (r.Path, r.ItemKey!, r.Kind))
        );
    }

    private static JsonObject WithTriggers(JsonObject scenario, string? firstId = null, string? secondId = null)
    {
        scenario["initializationTriggers"] = new JsonArray(Trigger(firstId, "SQALL", 0), Trigger(secondId, "WX", 60));
        return scenario;
    }

    private static JsonObject Trigger(string? id, string command, int timeOffset)
    {
        var trigger = new JsonObject();
        if (id is not null)
        {
            trigger["id"] = id;
        }

        trigger["command"] = command;
        trigger["timeOffset"] = timeOffset;
        return trigger;
    }

    private static JsonObject WithStripBay(JsonObject scenario)
    {
        scenario["flightStripConfigurations"] = new JsonArray(
            new JsonObject
            {
                ["bayId"] = "B1",
                ["rack"] = 1,
                ["aircraftIds"] = new JsonArray(),
            }
        );
        return scenario;
    }

    private static JsonObject WithUnmodelled(JsonObject scenario)
    {
        scenario["unmodelledRootField"] = new JsonObject { ["nested"] = "kept", ["count"] = 3 };
        scenario["aircraft"]![0]!["unmodelledAircraftField"] = new JsonArray(1, "two", true);
        return scenario;
    }

    // Preset commands on the first aircraft, in the given order; the list carries ids, so the walk matches it by id.
    private static JsonObject WithPresetCommands(JsonObject scenario, IEnumerable<(string Id, string Command)> presets)
    {
        scenario["aircraft"]![0]!["presetCommands"] = new JsonArray([.. presets.Select(p => (JsonNode?)Preset(p))]);
        return scenario;
    }

    private static JsonObject Preset((string Id, string Command) preset) =>
        new()
        {
            ["id"] = preset.Id,
            ["command"] = preset.Command,
            ["timeOffset"] = 0,
        };

    // A scenario whose aircraft are these ids and nothing else: enough for the list's order and a same-id addition.
    private static JsonObject WithAircraft(params string[] ids)
    {
        JsonObject scenario = Sample();
        scenario["aircraft"] = new JsonArray([.. ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["aircraftId"] = id })]);
        return scenario;
    }

    private static List<JsonNode?> Reverse(List<JsonNode?> aircraft) => [.. Enumerable.Reverse(aircraft)];

    private static List<JsonNode?> FirstToLast(List<JsonNode?> aircraft) => [.. aircraft.Skip(1), aircraft[0]];

    // A fresh copy of the scenario's aircraft in the given order, so each call fills a scenario of its own.
    private static JsonObject ReorderAircraft(JsonObject scenario, Func<List<JsonNode?>, List<JsonNode?>> order)
    {
        List<JsonNode?> aircraft = [.. scenario["aircraft"]!.AsArray().Select(a => a!.DeepClone())];
        scenario["aircraft"] = new JsonArray([.. order(aircraft)]);
        return scenario;
    }

    private static List<string> Ids(JsonArray ids) => [.. ids.Select(id => id!.GetValue<string>())];

    private static List<string> ItemIds(JsonArray items) => [.. items.Select(item => item!["id"]!.GetValue<string>())];

    private static List<string> AircraftIds(JsonNode? scenario) => ItemIds(scenario!["aircraft"]!.AsArray());

    private static JsonNode Merged(ScenarioMergePlan plan) => JsonNode.Parse(plan.Apply(NoChoices))!;

    private static JsonObject Sample() => JsonNode.Parse(File.ReadAllText(SamplePath))!.AsObject();

    private static string Text(JsonNode node) => node.ToJsonString();
}
