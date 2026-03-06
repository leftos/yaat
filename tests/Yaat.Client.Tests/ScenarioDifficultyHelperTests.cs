using Xunit;
using Yaat.Client.Services;

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
}
