using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioAircraftFactory"/> builds the aircraft the Scenario Editor's grid adds: a new one with the editor's
/// defaults, and a duplicate that keeps every member of its source, unmodelled ones included, under new ids and a free callsign.
/// </summary>
public class ScenarioAircraftFactoryTests
{
    private const string ProbeKey = "yaatProbe";

    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 15, 30, 250, TimeSpan.Zero);

    private static readonly string SampleScenarioPath = Path.Combine(
        TickRecorder.FindRepoRoot(),
        "docs",
        "atctrainer-scenario-examples",
        "01HCE8FW7P81CGQQFZP0HRM9F7.json"
    );

    [Fact]
    public void NewAircraft_HasTheEditorDefaults()
    {
        var scenario = new Scenario { PrimaryAirportId = "KOAK" };

        ScenarioAircraft ac = ScenarioAircraftFactory.NewAircraft(scenario, Now, new Random(7));

        Assert.Equal(26, ac.Id.Length);
        Assert.True(LocalScenarioStore.IsValidId(ac.Id));
        Assert.Equal("", ac.AircraftId);
        Assert.Equal("", ac.AircraftType);
        Assert.Equal("C", ac.TransponderMode);
        Assert.Equal(0, ac.SpawnDelay);
        Assert.Null(ac.FlightPlan);
        Assert.Null(ac.AirportId);
        Assert.Equal("Parking", ac.StartingConditions.Type);
        Assert.Equal("", ac.StartingConditions.Parking);
        Assert.Empty(ac.PresetCommands);
        Assert.Null(ac.AutoTrackConditions);
        Assert.False(ac.OnAltitudeProfile);
        Assert.Null(ac.Difficulty);
        Assert.Null(ac.ExpectedApproach);
        Assert.Null(ac.ExtensionData);
        Assert.Empty(scenario.Aircraft);
    }

    [Fact]
    public void NewAircraft_SameClockAndSeed_GivesTheSameId()
    {
        var scenario = new Scenario();

        Assert.Equal(
            ScenarioAircraftFactory.NewAircraft(scenario, Now, new Random(42)).Id,
            ScenarioAircraftFactory.NewAircraft(scenario, Now, new Random(42)).Id
        );
    }

    [Fact]
    public void NewAircraft_WrittenAfterSave_HoldsNoNullMember()
    {
        ScenarioAircraft ac = ScenarioAircraftFactory.NewAircraft(new Scenario { PrimaryAirportId = "KOAK" }, Now, new Random(7));

        JsonObject written = WrittenAircraft(ac);

        Assert.Empty(NullMembers(written, "aircraft"));
        Assert.Equal(["id", "aircraftId", "transponderMode", "startingConditions", "spawnDelay"], written.Select(p => p.Key));
    }

    /// <summary>The aircraft as a scenario document writes it, inside a scenario built in code.</summary>
    internal static JsonObject WrittenAircraft(ScenarioAircraft ac)
    {
        string json = ScenarioDocument.FromScenario(new Scenario { Aircraft = [ac] }).Write();
        JsonNode root = JsonNode.Parse(json) ?? throw new InvalidOperationException("Scenario wrote null");
        return root["aircraft"]![0]!.AsObject();
    }

    /// <summary>The paths of every property under <paramref name="node"/> whose value is <c>null</c>.</summary>
    internal static IEnumerable<string> NullMembers(JsonNode? node, string path)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (value is null)
                {
                    yield return $"{path}.{key}";
                }

                foreach (string inner in NullMembers(value, $"{path}.{key}"))
                {
                    yield return inner;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                foreach (string inner in NullMembers(array[i], $"{path}[{i}]"))
                {
                    yield return inner;
                }
            }
        }
    }

    [Fact]
    public void Duplicate_IdsComeFromTheClockAndSeed_AircraftFirstThenEachPreset()
    {
        (Scenario scenario, ScenarioAircraft source) = SampleWithProbe();
        var random = new Random(11);
        List<string> expected = [.. Enumerable.Range(0, 1 + source.PresetCommands.Count).Select(_ => ScenarioIdGenerator.Create(Now, random))];

        ScenarioAircraft first = ScenarioAircraftFactory.Duplicate(scenario, source, Now, new Random(11));
        ScenarioAircraft second = ScenarioAircraftFactory.Duplicate(scenario, source, Now, new Random(11));

        Assert.Equal(expected, [first.Id, .. first.PresetCommands.Select(p => p.Id)]);
        Assert.Equal(expected, [second.Id, .. second.PresetCommands.Select(p => p.Id)]);
    }

    [Fact]
    public void Duplicate_KeepsEveryMember_UnderNewIds()
    {
        (Scenario scenario, ScenarioAircraft source) = SampleWithProbe();
        JsonObject sourceBefore = ToNode(source);
        int count = scenario.Aircraft.Count;

        ScenarioAircraft copy = ScenarioAircraftFactory.Duplicate(scenario, source, Now, new Random(3));

        Assert.NotSame(source, copy);
        Assert.Equal(count, scenario.Aircraft.Count);
        Assert.True(JsonNode.DeepEquals(sourceBefore, ToNode(source)), "the source changed");
        Assert.NotNull(copy.ExtensionData);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse("""{"kept":true}""").RootElement, copy.ExtensionData[ProbeKey]));
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(26, copy.Id.Length);
        Assert.Equal(source.PresetCommands.Count, copy.PresetCommands.Count);
        Assert.All(source.PresetCommands.Zip(copy.PresetCommands), p => Assert.NotEqual(p.First.Id, p.Second.Id));
        Assert.Equal(copy.PresetCommands.Count, copy.PresetCommands.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());

        JsonObject expected = ToNode(source);
        expected["id"] = copy.Id;
        expected["aircraftId"] = copy.AircraftId;
        JsonArray presets = expected["presetCommands"]!.AsArray();
        for (int i = 0; i < presets.Count; i++)
        {
            presets[i]!["id"] = copy.PresetCommands[i].Id;
        }

        Assert.True(JsonNode.DeepEquals(expected, ToNode(copy)), "a modelled member differs");
    }

    [Theory]
    [InlineData("AAL123", "", "AAL124")]
    [InlineData("AAL123", "AAL124", "AAL125")]
    [InlineData("aal123", "AAL124", "aal125")]
    [InlineData("N05", "", "N06")]
    [InlineData("SWA", "", "SWA2")]
    [InlineData("", "", "")]
    [InlineData("N9", "N10", "N11")]
    public void Duplicate_MakesTheCallsignUnique(string callsign, string taken, string expected)
    {
        var source = new ScenarioAircraft { Id = "01H06NVK7VN8BS7MCDXHKJZ7MQ", AircraftId = callsign };
        var scenario = new Scenario { Aircraft = [source] };
        foreach (string other in taken.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            scenario.Aircraft.Add(new ScenarioAircraft { AircraftId = other });
        }

        ScenarioAircraft copy = ScenarioAircraftFactory.Duplicate(scenario, source, Now, new Random(5));

        Assert.Equal(expected, copy.AircraftId);
    }

    // The sample scenario with an unmodelled member added to the source JSON of its first aircraft with presets.
    private static (Scenario Scenario, ScenarioAircraft Source) SampleWithProbe()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(SampleScenarioPath)) ?? throw new InvalidOperationException("Empty sample");
        JsonArray aircraft = root["aircraft"]!.AsArray();
        int index = aircraft.ToList().FindIndex(a => a?["presetCommands"] is JsonArray { Count: > 1 });
        Assert.True(index >= 0, "the sample has no aircraft with presets");
        aircraft[index]![ProbeKey] = new JsonObject { ["kept"] = true };

        Scenario scenario = ScenarioDocument.Read(root.ToJsonString()).Scenario;
        return (scenario, scenario.Aircraft[index]);
    }

    private static JsonObject ToNode(ScenarioAircraft ac) =>
        JsonSerializer.SerializeToNode(ac)?.AsObject() ?? throw new InvalidOperationException("Aircraft serialised to null");
}
