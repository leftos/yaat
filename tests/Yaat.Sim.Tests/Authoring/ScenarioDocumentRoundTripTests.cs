using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioDocument"/> reads a vNAS scenario into the typed model and writes it back JSON-equal: every field
/// the source held is kept, modelled or not, and a member the source left out stays out unless code changed it.
/// </summary>
public class ScenarioDocumentRoundTripTests
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    private static readonly string SampleScenarioPath = Path.Combine(ExamplesDir, "01H06NVK7VN8BS7MCDXHKJZ7MQ.json");

    public ScenarioDocumentRoundTripTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>Every committed scenario: the ATCTrainer examples under <c>docs/</c> and the test-data scenarios.</summary>
    public static TheoryData<string> CorpusScenarios()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(ExamplesDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        foreach (string path in Directory.EnumerateFiles(TestDataDir, "*scenario*.json").Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusScenarios))]
    public void Corpus_ReadThenWrite_IsJsonEqualToTheSource(string scenarioPath)
    {
        string original = File.ReadAllText(scenarioPath);

        string written = ScenarioDocument.Read(original).Write();

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(written)),
            $"{Path.GetFileName(scenarioPath)}: the written scenario differs from the source"
        );
    }

    [Fact]
    public void UnmodelledFields_AtRootAircraftAndStartingConditions_SurviveTheRoundTrip()
    {
        JsonNode source = JsonNode.Parse(File.ReadAllText(SampleScenarioPath))!;
        source["unmodelledRootField"] = new JsonObject { ["nested"] = "kept", ["count"] = 3 };
        JsonNode aircraft = source["aircraft"]![0]!;
        aircraft["unmodelledAircraftField"] = new JsonArray(1, "two", true);
        aircraft["startingConditions"]!["unmodelledStartField"] = 42.5;
        string original = source.ToJsonString();

        var document = ScenarioDocument.Read(original);
        string written = document.Write();

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(written)));
        Assert.Contains("unmodelledRootField", document.Scenario.ExtensionData!.Keys);
        Assert.Contains("unmodelledAircraftField", document.Scenario.Aircraft[0].ExtensionData!.Keys);
        Assert.Contains("unmodelledStartField", document.Scenario.Aircraft[0].StartingConditions.ExtensionData!.Keys);
    }

    [Fact]
    public void AbsentOptional_SetByCodeAfterTheRead_IsWritten()
    {
        string original = File.ReadAllText(SampleScenarioPath);
        JsonArray sourceAircraft = JsonNode.Parse(original)!["aircraft"]!.AsArray();
        int index = sourceAircraft.ToList().FindIndex(ac => !ac!.AsObject().ContainsKey("spawnDelay"));
        Assert.True(index >= 0, "the sample scenario has no aircraft without spawnDelay");

        var document = ScenarioDocument.Read(original);
        document.Scenario.Aircraft[index].SpawnDelay = 120;
        JsonNode written = JsonNode.Parse(document.Write())!;

        Assert.Equal(120, written["aircraft"]![index]!["spawnDelay"]!.GetValue<int>());
    }

    [Fact]
    public void AircraftAddedAfterTheRead_WritesEveryMember()
    {
        var document = ScenarioDocument.Read(File.ReadAllText(SampleScenarioPath));
        var added = new ScenarioAircraft { Id = "added", AircraftId = "N123AB" };
        document.Scenario.Aircraft.Add(added);

        JsonNode written = JsonNode.Parse(document.Write())!;

        JsonNode? expected = JsonSerializer.SerializeToNode(added);
        JsonNode? writtenAircraft = written["aircraft"]!.AsArray()[^1];
        Assert.True(JsonNode.DeepEquals(expected, writtenAircraft), writtenAircraft?.ToJsonString());
        Assert.True(writtenAircraft!.AsObject().ContainsKey("flightplan"));
        Assert.True(writtenAircraft["startingConditions"]!.AsObject().ContainsKey("distanceFromRunway"));
    }

    [Fact]
    public void PresentMember_SetBackToTheDefaultByCode_IsStillWritten()
    {
        JsonNode source = JsonNode.Parse(File.ReadAllText(SampleScenarioPath))!;
        int index = source["aircraft"]!.AsArray().ToList().FindIndex(ac => !ac!.AsObject().ContainsKey("spawnDelay"));
        Assert.True(index >= 0, "the sample scenario has no aircraft without spawnDelay");
        source["aircraft"]![index]!["spawnDelay"] = 120;

        var document = ScenarioDocument.Read(source.ToJsonString());
        Assert.Equal(120, document.Scenario.Aircraft[index].SpawnDelay);
        document.Scenario.Aircraft[index].SpawnDelay = 0;
        JsonNode written = JsonNode.Parse(document.Write())!;

        JsonObject writtenAircraft = written["aircraft"]![index]!.AsObject();
        Assert.True(writtenAircraft.ContainsKey("spawnDelay"));
        Assert.Equal(0, writtenAircraft["spawnDelay"]!.GetValue<int>());
    }

    [Fact]
    public void FromScenario_WritesEveryMemberAsThePlainSerializerDoes()
    {
        Scenario scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(SampleScenarioPath), ScenarioLoader.JsonOptions)!;

        string written = ScenarioDocument.FromScenario(scenario).Write();

        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(scenario), JsonNode.Parse(written)));
    }

    [Fact]
    public void Read_MalformedJson_ThrowsJsonException() => Assert.ThrowsAny<JsonException>(() => ScenarioDocument.Read("{\"id\":"));

    [Fact]
    public void Read_JsonNull_ThrowsJsonException() => Assert.Throws<JsonException>(() => ScenarioDocument.Read("null"));

    [Fact]
    public void Read_NullText_ThrowsArgumentNullException() => Assert.Throws<ArgumentNullException>(() => ScenarioDocument.Read(null!));

    [Fact]
    public void FromScenario_Null_ThrowsArgumentNullException() => Assert.Throws<ArgumentNullException>(() => ScenarioDocument.FromScenario(null!));

    /// <summary>
    /// The corpus comparison relies on <see cref="JsonNode.DeepEquals"/> comparing numbers by value, so a source
    /// <c>1.0</c> the model writes back as <c>1</c> still counts as equal.
    /// </summary>
    [Fact]
    public void Probe_DeepEquals_ComparesNumbersByValue() => Assert.True(JsonNode.DeepEquals(JsonNode.Parse("1.0"), JsonNode.Parse("1")));

    [Theory]
    [MemberData(nameof(CorpusScenarios))]
    public void Corpus_WrittenScenario_HasTheSameResourceManifest(string scenarioPath)
    {
        string original = File.ReadAllText(scenarioPath);
        string written = ScenarioDocument.Read(original).Write();

        var before = ScenarioResourceManifest.FromJson(original);
        var after = ScenarioResourceManifest.FromJson(written);

        Assert.Null(before.ReadError);
        Assert.Equal(before.ReadError, after.ReadError);
        Assert.Equal(before.ScenarioName, after.ScenarioName);
        Assert.Equal(before.ArtccId, after.ArtccId);
        Assert.Equal(before.NeighbourArtccIds, after.NeighbourArtccIds);
        Assert.Equal(before.AirportIds, after.AirportIds);
        Assert.Equal(before.MapRequiredAirportIds, after.MapRequiredAirportIds);
    }
}
