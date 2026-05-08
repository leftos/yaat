using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Verifies that parking aircraft with preset TAXI commands are excluded from the
/// scenario's parking-call-up source set — the autonomous solo-training ready-to-taxi
/// call-up must not fire on top of a scenario-scripted ground sequence.
/// </summary>
[Collection("NavDbMutator")]
public class ScenarioLoaderTaxiPresetTests
{
    public ScenarioLoaderTaxiPresetTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string ParkingWithTaxiPreset = """
        {
          "id": "test",
          "name": "Test",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N123",
              "aircraftType": "C172",
              "startingConditions": { "type": "Parking", "parking": "A1" },
              "presetCommands": [ { "id": "p1", "command": "TAXI VIA A B", "timeOffset": 0 } ]
            }
          ]
        }
        """;

    private const string ParkingWithoutPreset = """
        {
          "id": "test",
          "name": "Test",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N123",
              "aircraftType": "C172",
              "startingConditions": { "type": "Parking", "parking": "A1" }
            }
          ]
        }
        """;

    private const string MixedScriptedAndUnscripted = """
        {
          "id": "test",
          "name": "Test",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N111",
              "aircraftType": "C172",
              "startingConditions": { "type": "Parking", "parking": "A1" },
              "presetCommands": [ { "id": "p1", "command": "TAXI VIA A", "timeOffset": 0 } ]
            },
            {
              "id": "ac2",
              "aircraftId": "N222",
              "aircraftType": "C172",
              "startingConditions": { "type": "Parking", "parking": "A2" }
            }
          ]
        }
        """;

    [Fact]
    public void HasParkingSpawns_AllScripted_IsFalse()
    {
        var result = ScenarioLoader.Load(ParkingWithTaxiPreset, groundData: null, new Random(0));

        Assert.False(result.HasParkingSpawns);
    }

    [Fact]
    public void HasParkingSpawns_NoPresets_IsTrue()
    {
        var result = ScenarioLoader.Load(ParkingWithoutPreset, groundData: null, new Random(0));

        Assert.True(result.HasParkingSpawns);
    }

    [Fact]
    public void HasParkingSpawns_MixedScriptedAndUnscripted_IsTrue()
    {
        // A single unscripted parking aircraft is enough for the slider to remain available.
        var result = ScenarioLoader.Load(MixedScriptedAndUnscripted, groundData: null, new Random(0));

        Assert.True(result.HasParkingSpawns);
    }

    [Fact]
    public void HasTaxiPreset_TaxiAlias_ReturnsTrue()
    {
        var presets = new List<PresetCommand> { new() { Command = "TAXI VIA A B" } };

        Assert.True(ScenarioLoader.HasTaxiPreset(presets));
    }

    [Fact]
    public void HasTaxiPreset_NonTaxiCommand_ReturnsFalse()
    {
        var presets = new List<PresetCommand>
        {
            new() { Command = "FH 270" },
            new() { Command = "CM 5000" },
        };

        Assert.False(ScenarioLoader.HasTaxiPreset(presets));
    }

    [Fact]
    public void HasTaxiPreset_EmptyList_ReturnsFalse()
    {
        Assert.False(ScenarioLoader.HasTaxiPreset([]));
    }
}
