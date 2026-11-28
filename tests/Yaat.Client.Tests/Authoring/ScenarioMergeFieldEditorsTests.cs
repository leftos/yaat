using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The merge window's field-to-editor table: each mapped kind turns a document value into its text and back to the same
/// JSON, and a field the table does not map gets the JSON box with its own error.
/// </summary>
public class ScenarioMergeFieldEditorsTests
{
    public ScenarioMergeFieldEditorsTests() => TestVnasData.EnsureInitialized();

    [Theory]
    [InlineData("General", "name", "\"Lima\"", ScenarioMergeEditorKind.Text)]
    [InlineData("General", "primaryApproach", "null", ScenarioMergeEditorKind.Text)]
    [InlineData("Initialization triggers", "timeOffset", "480", ScenarioMergeEditorKind.Time)]
    [InlineData("Aircraft", "spawnDelay", "90", ScenarioMergeEditorKind.Time)]
    [InlineData("Generators", "maxTime", "3600", ScenarioMergeEditorKind.OptionalTime)]
    [InlineData("Generators", "maxTime", "null", ScenarioMergeEditorKind.OptionalTime)]
    [InlineData("Aircraft", "flightplan/cruiseAltitude", "31000", ScenarioMergeEditorKind.Number)]
    [InlineData("Aircraft", "startingConditions/coordinates/lat", "37.7213", ScenarioMergeEditorKind.Number)]
    [InlineData("Aircraft", "startingConditions/altitude", "null", ScenarioMergeEditorKind.Number)]
    [InlineData("General", "autoDeleteMode", "\"OnLanding\"", ScenarioMergeEditorKind.Choice)]
    [InlineData("General", "autoDeleteMode", "null", ScenarioMergeEditorKind.Choice)]
    [InlineData("General", "studentPositionId", "\"01GEAT1S60WNVZ3VQXP7TSQF84\"", ScenarioMergeEditorKind.Choice)]
    [InlineData("ATC positions", "autoConnect", "true", ScenarioMergeEditorKind.Bool)]
    [InlineData("ATC positions", "autoTrackAirportIds", "[\"OAK\",\"SFO\"]", ScenarioMergeEditorKind.CommaList)]
    [InlineData("Initialization triggers", "command", "\"SQALL\"", ScenarioMergeEditorKind.Command)]
    [InlineData("Aircraft", "presetCommands[]/command", "\"FH 020\"", ScenarioMergeEditorKind.Command)]
    [InlineData("Aircraft", "startingConditions/altitude", "3000", ScenarioMergeEditorKind.Number)]
    [InlineData("Aircraft", "startingConditions/heading", "270", ScenarioMergeEditorKind.Number)]
    [InlineData("Aircraft", "autoTrackConditions/handoffDelay", "30", ScenarioMergeEditorKind.Number)]
    [InlineData("General", "minimumRating", "null", ScenarioMergeEditorKind.Choice)]
    [InlineData("Generators", "enabled", "null", ScenarioMergeEditorKind.OptionalBool)]
    [InlineData("VFR arrival generators", "enabled", "true", ScenarioMergeEditorKind.OptionalBool)]
    [InlineData("Overflight generators", "enabled", "false", ScenarioMergeEditorKind.OptionalBool)]
    [InlineData("Overflight generators", "snapHemisphericAltitude", "false", ScenarioMergeEditorKind.Bool)]
    [InlineData("General", "customThing", "{\"a\":[1,2.5,\"x\"]}", ScenarioMergeEditorKind.Json)]
    public void EachKind_JsonToTextAndBack_GivesTheSameJson(string section, string fieldKey, string json, ScenarioMergeEditorKind kind)
    {
        ScenarioMergeFieldEditor editor = ScenarioMergeFieldEditors.For(section, fieldKey);

        ScenarioMergeParsed parsed = editor.Parse(editor.Format(JsonNode.Parse(json)));

        Assert.Equal(kind, editor.Kind);
        Assert.Null(parsed.Error);
        Assert.Equal(json, parsed.Value?.ToJsonString() ?? "null");
    }

    [Fact]
    public void UnmappedTopLevelField_GetsTheJsonBox_AndBareTextIsNotValidJson()
    {
        ScenarioMergeFieldEditor editor = ScenarioMergeFieldEditors.For("General", "weightClass");

        ScenarioMergeParsed parsed = editor.Parse("SNALL");

        Assert.Equal(ScenarioMergeEditorKind.Json, editor.Kind);
        Assert.StartsWith("Not valid JSON: ", parsed.Error, StringComparison.Ordinal);
        Assert.True(parsed.BlocksKeep);
        Assert.Equal("\"SNALL\"", editor.Parse("\"SNALL\"").Value?.ToJsonString());
    }

    [Fact]
    public void WholeListItem_GetsTheJsonBox() => Assert.Equal(ScenarioMergeEditorKind.Json, ScenarioMergeFieldEditors.For("Aircraft", "[]").Kind);

    [Fact]
    public void TriggerCommand_ThatDoesNotParse_HasNoErrorLine()
    {
        ScenarioMergeParsed parsed = ScenarioMergeFieldEditors.For("Initialization triggers", "command").Parse("XYZZY 42");

        Assert.Null(parsed.Error);
        Assert.Equal("\"XYZZY 42\"", parsed.Value?.ToJsonString());
    }

    [Fact]
    public void MinimumRating_NoneOption_WritesNull()
    {
        ScenarioMergeFieldEditor editor = ScenarioMergeFieldEditors.For("General", "minimumRating");
        ScenarioPickOption none = editor.OptionsFor("", ScenarioMergePickers.FreeText)![0];

        Assert.Equal(new ScenarioPickOption("", "None"), none);
        Assert.Null(editor.Parse(none.Value).Value);
    }

    [Fact]
    public void BlankName_IsRefusedWithTheEditorsError()
    {
        ScenarioMergeParsed parsed = ScenarioMergeFieldEditors.For("General", "name").Parse("   ");

        Assert.Equal(("A scenario needs a name.", true), (parsed.Error, parsed.BlocksKeep));
    }

    [Fact]
    public void AbsentBool_ReadsAsTheFieldsDefault()
    {
        Assert.Equal("true", ScenarioMergeFieldEditors.For("Overflight generators", "snapHemisphericAltitude").Format(null));
        Assert.Equal("false", ScenarioMergeFieldEditors.For("Generators", "randomizeInterval").Format(null));
        Assert.Equal("", ScenarioMergeFieldEditors.For("Generators", "enabled").Format(null));
    }

    [Fact]
    public void PresetCommand_ResolvingThroughTheFlightPlansAirports_ParsesOnlyWithThem()
    {
        ScenarioMergeFieldEditor editor = ScenarioMergeFieldEditors.For("Aircraft", "presetCommands[]/command");

        ScenarioMergeParsed bare = editor.Parse("CFIX CFBMS 1400");
        ScenarioMergeParsed planned = editor.Parse("CFIX CFBMS 1400", new ScenarioMergeCommandContext("", "KBCT", "KOAK"));

        Assert.StartsWith("The command does not parse: ", bare.Error, StringComparison.Ordinal);
        Assert.Null(planned.Error);
    }

    [Fact]
    public void TextKind_HoldingANonString_FallsBackToTheJsonBox()
    {
        ScenarioMergeFieldEditor name = ScenarioMergeFieldEditors.For("General", "name");

        Assert.Equal(ScenarioMergeEditorKind.Json, name.ForValue(JsonValue.Create(5)).Kind);
        Assert.Equal(ScenarioMergeEditorKind.Text, name.ForValue(JsonValue.Create("Lima")).Kind);
        Assert.Equal(ScenarioMergeEditorKind.Text, name.ForValue(null).Kind);
    }

    [Fact]
    public void EveryMappedField_ChangedInARealScenario_PlansARowTheTableMaps()
    {
        JsonObject mine = Sample();
        JsonObject theirs = Sample();
        foreach ((string section, string fieldKey) in ScenarioMergeFieldEditors.MappedFields)
        {
            Probe(mine, section, fieldKey, "mine");
            Probe(theirs, section, fieldKey, "theirs");
        }

        ScenarioMergePlan plan = ScenarioMerge.Plan(null, mine.ToJsonString(), theirs.ToJsonString());

        HashSet<(string, string)> planned = [.. plan.Rows.Select(row => (row.Section, row.FieldKey))];
        List<(string, string)> unplanned = [.. ScenarioMergeFieldEditors.MappedFields.Where(field => !planned.Contains(field))];
        Assert.Empty(unplanned);
        Assert.All(
            plan.Rows.Where(row => ScenarioMergeFieldEditors.MappedFields.Contains((row.Section, row.FieldKey))),
            row => Assert.NotEqual(ScenarioMergeEditorKind.Json, ScenarioMergeFieldEditors.For(row.Section, row.FieldKey).ForValue(row.Mine).Kind)
        );
    }

    private static readonly Dictionary<string, string> ListKeys = new(StringComparer.Ordinal)
    {
        ["Aircraft"] = "aircraft",
        ["ATC positions"] = "atc",
        ["Initialization triggers"] = "initializationTriggers",
        ["Generators"] = "aircraftGenerators",
        ["VFR arrival generators"] = "vfrArrivalGenerators",
        ["Overflight generators"] = "overflightGenerators",
        ["Strip bays"] = "flightStripConfigurations",
    };

    // Writes a value at the field's label path under the section's first item, making the items and objects it needs.
    private static void Probe(JsonObject scenario, string section, string fieldKey, string value)
    {
        JsonObject target = ListKeys.TryGetValue(section, out string? list) ? FirstItem(scenario, list) : scenario;
        string[] members = fieldKey.Split('/');
        foreach (string member in members[..^1])
        {
            target = member.EndsWith("[]", StringComparison.Ordinal) ? FirstItem(target, member[..^2]) : Child(target, member);
        }

        target[members[^1]] = value;
    }

    private static JsonObject FirstItem(JsonObject parent, string key)
    {
        if ((parent[key] is not JsonArray items) || (items.Count == 0))
        {
            items = [new JsonObject { ["id"] = "01HPROBE000000000000000001" }];
            parent[key] = items;
        }

        return items[0]!.AsObject();
    }

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is not JsonObject child)
        {
            child = [];
            parent[key] = child;
        }

        return child;
    }

    private static JsonObject Sample() =>
        JsonNode
            .Parse(
                File.ReadAllText(
                    Path.Combine(
                        TestRepo.Root("the scenario examples are read from the checkout."),
                        "docs",
                        "atctrainer-scenario-examples",
                        "01H06NVK7VN8BS7MCDXHKJZ7MQ.json"
                    )
                )
            )!
            .AsObject();
}
