using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioDiff"/> lists the fields that differ between two scenario JSON texts, keyed to the editor's tabs and
/// labels, on a real committed scenario edited by hand.
/// </summary>
public class ScenarioDiffTests
{
    private static readonly string SamplePath = Path.Combine(
        TickRecorder.FindRepoRoot(),
        "docs",
        "atctrainer-scenario-examples",
        "01H06NVK7VN8BS7MCDXHKJZ7MQ.json"
    );

    [Fact]
    public void Compare_HandEditedRealScenario_ListsExactlyTheChangedFields()
    {
        string left = File.ReadAllText(SamplePath);
        JsonObject right = JsonNode.Parse(left)!.AsObject();
        JsonArray aircraft = right["aircraft"]!.AsArray();
        string firstId = aircraft[0]!["id"]!.GetValue<string>();
        string secondId = aircraft[1]!["id"]!.GetValue<string>();
        string removedAtcId = right["atc"]![1]!["id"]!.GetValue<string>();
        right["name"] = "Clearances Intro, revised";
        aircraft[0]!["flightplan"]!["route"] = "OAK6 OAK DIRECT";
        aircraft[1]!["aircraftId"] = "SWA9999";
        aircraft.Add(
            new JsonObject
            {
                ["id"] = "01HZZZZZZZZZZZZZZZZZZZZZZZ",
                ["aircraftId"] = "N123AB",
                ["aircraftType"] = "C172/G",
            }
        );
        right["atc"]!.AsArray().RemoveAt(1);
        right["unmodelledRootField"] = "kept";

        IReadOnlyList<ScenarioDiffEntry> entries = ScenarioDiff.Compare(left, right.ToJsonString());

        ScenarioDiffEntry[] expected =
        [
            new("/name", "General", "Name", null, DiffKind.Changed),
            new("/aircraft/0/flightplan/route", "Aircraft", "Route", firstId, DiffKind.Changed),
            new("/aircraft/1/aircraftId", "Aircraft", "Callsign", secondId, DiffKind.Changed),
            new("/aircraft/18", "Aircraft", "Aircraft", "01HZZZZZZZZZZZZZZZZZZZZZZZ", DiffKind.Added),
            new("/atc/1", "ATC positions", "ATC position", removedAtcId, DiffKind.Removed),
            new("/unmodelledRootField", "General", "unmodelledRootField", null, DiffKind.Added),
        ];
        Assert.Equal(expected, entries);
    }
}
