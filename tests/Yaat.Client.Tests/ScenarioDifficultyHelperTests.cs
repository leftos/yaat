using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

public class ScenarioDifficultyHelperTests
{
    private const string AllDifficulties = """
        {
          "aircraft": [
            { "callsign": "A1", "difficulty": "Easy" },
            { "callsign": "A2", "difficulty": "Medium" },
            { "callsign": "A3", "difficulty": "Hard" },
            { "callsign": "A4", "difficulty": "Easy" }
          ]
        }
        """;

    private const string EasyOnly = """
        {
          "aircraft": [
            { "callsign": "A1", "difficulty": "Easy" },
            { "callsign": "A2", "difficulty": "Easy" }
          ]
        }
        """;

    private const string EasyOnlyWithParking = """
        {
          "aircraft": [
            {
              "callsign": "A1",
              "difficulty": "Easy",
              "startingConditions": { "type": "Parking", "parking": "A1" }
            }
          ]
        }
        """;

    private const string EasyOnlyWithArrivalGenerators = """
        {
          "aircraftGenerators": [
            { "id": "G1", "runway": "30", "intervalTime": 300 }
          ],
          "aircraft": [
            {
              "callsign": "A1",
              "difficulty": "Easy",
              "startingConditions": { "type": "OnFinal", "runway": "30" }
            }
          ]
        }
        """;

    private const string AllDifficultiesWithParkingAndArrivalGenerators = """
        {
          "aircraftGenerators": [
            { "id": "G1", "runway": "30", "intervalTime": 300 }
          ],
          "aircraft": [
            {
              "callsign": "A1",
              "difficulty": "Easy",
              "startingConditions": { "type": "Parking", "parking": "A1" }
            },
            {
              "callsign": "A2",
              "difficulty": "Medium",
              "startingConditions": { "type": "OnFinal", "runway": "30" }
            },
            {
              "callsign": "A3",
              "difficulty": "Hard",
              "startingConditions": { "type": "OnFinal", "runway": "30" }
            }
          ]
        }
        """;

    private const string NoDifficulty = """
        {
          "aircraft": [
            { "callsign": "A1" },
            { "callsign": "A2", "difficulty": null }
          ]
        }
        """;

    private const string NoAircraft = """{ "name": "test" }""";

    // -------------------------------------------------------------------------
    // GetAvailableDifficulties
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAvailableDifficulties_AllPresent_ReturnsOrdered()
    {
        var result = ScenarioDifficultyHelper.GetAvailableDifficulties(AllDifficulties);

        Assert.Equal(["Easy", "Medium", "Hard"], result);
    }

    [Fact]
    public void GetAvailableDifficulties_EasyOnly_ReturnsSingle()
    {
        var result = ScenarioDifficultyHelper.GetAvailableDifficulties(EasyOnly);

        Assert.Equal(["Easy"], result);
    }

    [Fact]
    public void GetAvailableDifficulties_NoDifficultyField_ReturnsEmpty()
    {
        var result = ScenarioDifficultyHelper.GetAvailableDifficulties(NoDifficulty);

        Assert.Empty(result);
    }

    [Fact]
    public void GetAvailableDifficulties_NoAircraftArray_ReturnsEmpty()
    {
        var result = ScenarioDifficultyHelper.GetAvailableDifficulties(NoAircraft);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // GetCountsPerCeiling
    // -------------------------------------------------------------------------

    [Fact]
    public void GetCountsPerCeiling_AllDifficulties_ProgressiveCounts()
    {
        var available = ScenarioDifficultyHelper.GetAvailableDifficulties(AllDifficulties);
        var counts = ScenarioDifficultyHelper.GetCountsPerCeiling(AllDifficulties, available);

        // Easy ceiling: 2 Easy + 0 null-difficulty = 2
        Assert.Equal(2, counts["Easy"]);
        // Medium ceiling: 2 Easy + 1 Medium = 3
        Assert.Equal(3, counts["Medium"]);
        // Hard ceiling: 2 Easy + 1 Medium + 1 Hard = 4
        Assert.Equal(4, counts["Hard"]);
    }

    [Fact]
    public void GetCountsPerCeiling_NoAircraft_ReturnsZeros()
    {
        var available = new List<string> { "Easy", "Hard" };
        var counts = ScenarioDifficultyHelper.GetCountsPerCeiling(NoAircraft, available);

        Assert.Equal(0, counts["Easy"]);
        Assert.Equal(0, counts["Hard"]);
    }

    // -------------------------------------------------------------------------
    // FilterByDifficulty
    // -------------------------------------------------------------------------

    [Fact]
    public void FilterByDifficulty_EasyCeiling_RemovesMediumAndHard()
    {
        var (json, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(AllDifficulties, "Easy");

        Assert.Empty(warnings);
        // Re-parse to check aircraft count
        var available = ScenarioDifficultyHelper.GetAvailableDifficulties(json);
        Assert.Equal(["Easy"], available);
    }

    [Fact]
    public void FilterByDifficulty_HardCeiling_KeepsAll()
    {
        var (json, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(AllDifficulties, "Hard");

        Assert.Empty(warnings);
        var available = ScenarioDifficultyHelper.GetAvailableDifficulties(json);
        Assert.Equal(["Easy", "Medium", "Hard"], available);
    }

    [Fact]
    public void FilterByDifficulty_NullDifficulty_AlwaysIncluded()
    {
        var json = """
            {
              "aircraft": [
                { "callsign": "A1" },
                { "callsign": "A2", "difficulty": "Hard" }
              ]
            }
            """;

        var (filtered, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(json, "Easy");

        Assert.Empty(warnings);
        // null-difficulty aircraft should be included (rank -1 ≤ any ceiling)
        Assert.Contains("A1", filtered);
        Assert.DoesNotContain("A2", filtered);
    }

    [Fact]
    public void FilterByDifficulty_UnknownDifficulty_IncludedWithWarning()
    {
        var json = """
            {
              "aircraft": [
                { "callsign": "A1", "difficulty": "Insane" }
              ]
            }
            """;

        var (filtered, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(json, "Easy");

        Assert.Single(warnings);
        Assert.Contains("Insane", warnings[0]);
        // Unknown rank is -1 → always included
        Assert.Contains("A1", filtered);
    }

    [Fact]
    public void FilterByDifficulty_NoAircraftKey_ReturnsOriginal()
    {
        var (json, warnings) = ScenarioDifficultyHelper.FilterByDifficulty(NoAircraft, "Easy");

        Assert.Empty(warnings);
        Assert.Contains("test", json);
    }

    [Fact]
    public void ScenarioSetupPlan_DifficultyAndSoloMode_ShowsBothControls()
    {
        var plan = ScenarioSetupPlan.Create(
            AllDifficultiesWithParkingAndArrivalGenerators,
            soloTrainingMode: true,
            parkingInitialCallupRatePercent: 55,
            arrivalGeneratorRatePercent: 75,
            goAroundProbabilityPercent: 0
        );

        Assert.True(plan.RequiresSetup);
        Assert.Equal(3, plan.DifficultyOptions.Count);
        Assert.Equal(2, plan.SelectedDifficultyIndex);
        Assert.True(plan.ShowPacingControls);
        Assert.True(plan.ShowParkingInitialCallupRate);
        Assert.True(plan.ShowArrivalGeneratorRate);
        Assert.Equal(55, plan.ParkingInitialCallupRatePercent);
        Assert.Equal(75, plan.ArrivalGeneratorRatePercent);
    }

    [Fact]
    public void ScenarioSetupPlan_DifficultyOnly_ShowsDifficulty()
    {
        var plan = ScenarioSetupPlan.Create(
            AllDifficultiesWithParkingAndArrivalGenerators,
            soloTrainingMode: false,
            parkingInitialCallupRatePercent: 55,
            arrivalGeneratorRatePercent: 75,
            goAroundProbabilityPercent: 0
        );

        Assert.True(plan.RequiresSetup);
        Assert.Equal(3, plan.DifficultyOptions.Count);
        Assert.False(plan.ShowPacingControls);
        Assert.False(plan.ShowParkingInitialCallupRate);
        Assert.False(plan.ShowArrivalGeneratorRate);
    }

    [Fact]
    public void ScenarioSetupPlan_SoloModeWithParkingOnly_ShowsParkingPacing()
    {
        var plan = ScenarioSetupPlan.Create(
            EasyOnlyWithParking,
            soloTrainingMode: true,
            parkingInitialCallupRatePercent: -20,
            arrivalGeneratorRatePercent: 125,
            goAroundProbabilityPercent: 0
        );

        Assert.True(plan.RequiresSetup);
        Assert.Empty(plan.DifficultyOptions);
        Assert.True(plan.ShowPacingControls);
        Assert.True(plan.ShowParkingInitialCallupRate);
        Assert.False(plan.ShowArrivalGeneratorRate);
        Assert.Equal(0, plan.ParkingInitialCallupRatePercent);
        Assert.Equal(100, plan.ArrivalGeneratorRatePercent);
    }

    [Fact]
    public void ScenarioSetupPlan_SoloModeWithArrivalGeneratorsOnly_ShowsArrivalGeneratorPacing()
    {
        var plan = ScenarioSetupPlan.Create(
            EasyOnlyWithArrivalGenerators,
            soloTrainingMode: true,
            parkingInitialCallupRatePercent: 55,
            arrivalGeneratorRatePercent: 75,
            goAroundProbabilityPercent: 0
        );

        Assert.True(plan.RequiresSetup);
        Assert.Empty(plan.DifficultyOptions);
        Assert.True(plan.ShowPacingControls);
        Assert.False(plan.ShowParkingInitialCallupRate);
        Assert.True(plan.ShowArrivalGeneratorRate);
        Assert.Equal(55, plan.ParkingInitialCallupRatePercent);
        Assert.Equal(75, plan.ArrivalGeneratorRatePercent);
    }

    [Fact]
    public void ScenarioSetupPlan_SoloModeWithoutParkingOrArrivalGenerators_SkipsSetup()
    {
        var plan = ScenarioSetupPlan.Create(
            EasyOnly,
            soloTrainingMode: true,
            parkingInitialCallupRatePercent: 55,
            arrivalGeneratorRatePercent: 75,
            goAroundProbabilityPercent: 0
        );

        Assert.False(plan.RequiresSetup);
        Assert.Empty(plan.DifficultyOptions);
        Assert.False(plan.ShowPacingControls);
        Assert.False(plan.ShowParkingInitialCallupRate);
        Assert.False(plan.ShowArrivalGeneratorRate);
    }

    [Fact]
    public void ScenarioSetupPlan_NoDifficultyAndInstructorMode_SkipsSetup()
    {
        var plan = ScenarioSetupPlan.Create(
            EasyOnly,
            soloTrainingMode: false,
            parkingInitialCallupRatePercent: 55,
            arrivalGeneratorRatePercent: 75,
            goAroundProbabilityPercent: 0
        );

        Assert.False(plan.RequiresSetup);
        Assert.Empty(plan.DifficultyOptions);
        Assert.False(plan.ShowPacingControls);
        Assert.False(plan.ShowParkingInitialCallupRate);
        Assert.False(plan.ShowArrivalGeneratorRate);
    }

    // -------------------------------------------------------------------------
    // HasParkingSpawns — preset TAXI filter
    // -------------------------------------------------------------------------

    private const string ParkingWithTaxiPreset = """
        {
          "aircraft": [
            {
              "callsign": "A1",
              "startingConditions": { "type": "Parking", "parking": "A1" },
              "presetCommands": [ { "id": "p1", "command": "TAXI VIA A B", "timeOffset": 0 } ]
            }
          ]
        }
        """;

    private const string ParkingMixedScriptedAndUnscripted = """
        {
          "aircraft": [
            {
              "callsign": "A1",
              "startingConditions": { "type": "Parking", "parking": "A1" },
              "presetCommands": [ { "id": "p1", "command": "TAXI VIA A", "timeOffset": 0 } ]
            },
            {
              "callsign": "A2",
              "startingConditions": { "type": "Parking", "parking": "A2" }
            }
          ]
        }
        """;

    [Fact]
    public void HasParkingSpawns_AllParkingAircraftScripted_ReturnsFalse()
    {
        Assert.False(ScenarioDifficultyHelper.HasParkingSpawns(ParkingWithTaxiPreset));
    }

    [Fact]
    public void HasParkingSpawns_MixedScriptedAndUnscripted_ReturnsTrue()
    {
        Assert.True(ScenarioDifficultyHelper.HasParkingSpawns(ParkingMixedScriptedAndUnscripted));
    }

    [Fact]
    public void HasParkingSpawns_NoPresets_ReturnsTrue()
    {
        Assert.True(ScenarioDifficultyHelper.HasParkingSpawns(EasyOnlyWithParking));
    }

    [Fact]
    public void ScenarioSetupPlan_AllParkingScripted_HidesParkingSlider()
    {
        var plan = ScenarioSetupPlan.Create(
            ParkingWithTaxiPreset,
            soloTrainingMode: true,
            parkingInitialCallupRatePercent: 55,
            arrivalGeneratorRatePercent: 75,
            goAroundProbabilityPercent: 0
        );

        Assert.False(plan.ShowParkingInitialCallupRate);
    }
}
