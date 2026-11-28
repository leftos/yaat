using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioNullNormalizer"/> repairs the empty numbers an older YAATEdit file holds as null: a preset's or an
/// initialization trigger's <c>timeOffset</c> becomes 0, a null optional generator number is dropped, and every other
/// value is left as it is.
/// </summary>
public class ScenarioNullNormalizerTests
{
    private static JsonObject Fixture() => JsonNode.Parse(TrainingFixtures.Read(TrainingFixtures.IdB))!.AsObject();

    // A preset list holding one preset whose timeOffset is the given JSON text: null, a number.
    private static JsonArray Presets(string timeOffsetJson) =>
        JsonNode.Parse("[{\"id\": \"preset-1\", \"command\": \"FH 010\", \"timeOffset\": " + timeOffsetJson + "}]")!.AsArray();

    // A one-generator array whose numberKey holds the given JSON text.
    private static JsonArray Generators(string numberKey, string valueJson) =>
        JsonNode.Parse("[{\"id\": \"generator-1\", \"runway\": \"30\", \"" + numberKey + "\": " + valueJson + "}]")!.AsArray();

    [Fact]
    public void Fix_PresetTimeOffsetNull_SetsZero_CountsOne()
    {
        JsonObject scenario = Fixture();
        scenario["aircraft"]![0]!["presetCommands"] = Presets("null");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(1, count);
        Assert.Equal(0, scenario["aircraft"]![0]!["presetCommands"]![0]!["timeOffset"]!.GetValue<int>());
    }

    [Fact]
    public void Fix_PresetTimeOffsetNumber_IsLeftAsItIs_CountsZero()
    {
        JsonObject scenario = Fixture();
        scenario["aircraft"]![0]!["presetCommands"] = Presets("5");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(0, count);
        Assert.Equal(5, scenario["aircraft"]![0]!["presetCommands"]![0]!["timeOffset"]!.GetValue<int>());
    }

    [Fact]
    public void Fix_TimeOffsetNullInAnotherCasing_SetsZeroUnderThatKey_CountsOne()
    {
        JsonObject scenario = Fixture();
        scenario["aircraft"]![0]!["presetCommands"] = JsonNode.Parse("""[{"id": "preset-1", "command": "FH 010", "TimeOffset": null}]""");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(1, count);
        JsonObject preset = scenario["aircraft"]![0]!["presetCommands"]![0]!.AsObject();
        Assert.Equal(0, preset["TimeOffset"]!.GetValue<int>());
        Assert.False(preset.ContainsKey("timeOffset"));
    }

    [Fact]
    public void Fix_InitializationTriggerTimeOffsetNull_SetsZero_CountsOne()
    {
        JsonObject scenario = Fixture();
        scenario["initializationTriggers"] = JsonNode.Parse("""[{"command": "SQALL", "timeOffset": null}]""");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(1, count);
        Assert.Equal(0, scenario["initializationTriggers"]![0]!["timeOffset"]!.GetValue<int>());
    }

    [Fact]
    public void Fix_GeneratorMaxTimeNull_IsRemoved_CountsOne()
    {
        JsonObject scenario = Fixture();
        scenario["aircraftGenerators"] = Generators("maxTime", "null");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(1, count);
        Assert.False(scenario["aircraftGenerators"]![0]!.AsObject().ContainsKey("maxTime"));
    }

    [Fact]
    public void Fix_GeneratorMaxTimeZero_IsKept_CountsZero()
    {
        JsonObject scenario = Fixture();
        scenario["aircraftGenerators"] = Generators("maxTime", "0");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(0, count);
        Assert.Equal(0, scenario["aircraftGenerators"]![0]!["maxTime"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("aircraftGenerators", "startTimeOffset")]
    [InlineData("aircraftGenerators", "maxDistance")]
    [InlineData("aircraftGenerators", "intervalDistance")]
    [InlineData("aircraftGenerators", "maxTime")]
    [InlineData("aircraftGenerators", "intervalTime")]
    [InlineData("vfrArrivalGenerators", "startTimeOffset")]
    [InlineData("vfrArrivalGenerators", "maxDistance")]
    [InlineData("vfrArrivalGenerators", "intervalDistance")]
    [InlineData("vfrArrivalGenerators", "maxTime")]
    [InlineData("vfrArrivalGenerators", "intervalTime")]
    [InlineData("overflightGenerators", "startTimeOffset")]
    [InlineData("overflightGenerators", "maxDistance")]
    [InlineData("overflightGenerators", "intervalDistance")]
    [InlineData("overflightGenerators", "maxTime")]
    [InlineData("overflightGenerators", "intervalTime")]
    public void Fix_NullOptionalGeneratorNumber_IsRemovedInEveryGeneratorArray(string arrayKey, string numberKey)
    {
        JsonObject scenario = Fixture();
        scenario[arrayKey] = Generators(numberKey, "null");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(1, count);
        Assert.False(scenario[arrayKey]![0]!.AsObject().ContainsKey(numberKey));
    }

    [Fact]
    public void Fix_NullUnderAnUnmodelledKey_IsLeft_CountsZero()
    {
        JsonObject scenario = Fixture();
        scenario["aircraft"]![0]!["presetCommands"] = JsonNode.Parse(
            """[{"id": "preset-1", "command": "FH 010", "timeOffset": 5, "unmodelledNumber": null}]"""
        );
        scenario["aircraftGenerators"] = JsonNode.Parse("""[{"id": "generator-1", "maxTime": 3600, "unmodelledNumber": null}]""");

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(0, count);
        Assert.True(scenario["aircraft"]![0]!["presetCommands"]![0]!.AsObject().ContainsKey("unmodelledNumber"));
        Assert.True(scenario["aircraftGenerators"]![0]!.AsObject().ContainsKey("unmodelledNumber"));
    }

    [Fact]
    public void Fix_ScenarioWithoutNulls_ChangesNothing_CountsZero()
    {
        JsonObject scenario = Fixture();
        JsonNode before = scenario.DeepClone()!;

        int count = ScenarioNullNormalizer.Fix(scenario);

        Assert.Equal(0, count);
        Assert.True(JsonNode.DeepEquals(before, scenario));
    }

    [Fact]
    public void Repair_TextWithoutNulls_IsReturnedUnchanged_CountsZero()
    {
        string json = TrainingFixtures.Read(TrainingFixtures.IdB);

        string repaired = ScenarioNullNormalizer.Repair(json, out int count);

        Assert.Equal(0, count);
        Assert.Same(json, repaired);
    }

    [Fact]
    public void Repair_MalformedText_IsReturnedUnchanged_CountsZero()
    {
        string json = "{ not json";

        string repaired = ScenarioNullNormalizer.Repair(json, out int count);

        Assert.Equal(0, count);
        Assert.Same(json, repaired);
    }

    [Fact]
    public void Repair_NonObjectRoot_IsReturnedUnchanged_CountsZero()
    {
        string json = "[1, 2]";

        string repaired = ScenarioNullNormalizer.Repair(json, out int count);

        Assert.Equal(0, count);
        Assert.Same(json, repaired);
    }

    [Fact]
    public void Repair_TextThatRepeatsAKey_IsReturnedUnchanged_CountsZero()
    {
        string json = """
            {
              "id": "dup",
              "name": "Dup",
              "aircraft": [
                { "id": "ac1", "presetCommands": [{ "id": "p1", "command": "FH 010", "timeOffset": null, "command": "FH 020" }] }
              ]
            }
            """;

        string repaired = ScenarioNullNormalizer.Repair(json, out int count);

        Assert.Equal(0, count);
        Assert.Same(json, repaired);
    }

    [Fact]
    public void Repair_NonAsciiText_IsWrittenIndentedAndUnescaped()
    {
        string json = """
            {
              "id": "r1",
              "name": "Café + Zôa",
              "artccId": "ZOA",
              "aircraft": [
                { "id": "ac1", "presetCommands": [{ "id": "p1", "command": "FH 010", "timeOffset": null }] }
              ]
            }
            """;

        string repaired = ScenarioNullNormalizer.Repair(json, out int count);

        Assert.Equal(1, count);
        Assert.Contains("Café + Zôa", repaired, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", repaired, StringComparison.Ordinal);
        Assert.Contains("\n", repaired, StringComparison.Ordinal);
    }
}
