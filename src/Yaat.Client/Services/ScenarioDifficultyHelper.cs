using System.Text.Json;
using System.Text.Json.Nodes;
using Yaat.Sim;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Services;

/// <summary>
/// Extracts difficulty metadata from scenario JSON and filters
/// aircraft by a cumulative difficulty ceiling.
/// </summary>
public static class ScenarioDifficultyHelper
{
    private static readonly string[] DifficultyOrder = ["Easy", "Medium", "Hard"];

    /// <summary>
    /// Returns the ordered list of named difficulty levels present
    /// in the scenario's aircraft array (e.g. ["Easy", "Hard"]).
    /// Unknown/null values are excluded from the list.
    /// </summary>
    public static List<string> GetAvailableDifficulties(string json)
    {
        var root = JsonNode.Parse(json);
        JsonArray? aircraft = root?["aircraft"]?.AsArray();
        if (aircraft is null)
        {
            return [];
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? ac in aircraft)
        {
            string? diff = ac?["difficulty"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(diff))
            {
                present.Add(diff);
            }
        }

        var result = new List<string>();
        foreach (string level in DifficultyOrder)
        {
            if (present.Remove(level))
            {
                result.Add(level);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the number of aircraft that would be included for
    /// each possible difficulty ceiling. Only includes ceilings
    /// that are in <paramref name="availableDifficulties"/>.
    /// </summary>
    public static Dictionary<string, int> GetCountsPerCeiling(string json, List<string> availableDifficulties)
    {
        var root = JsonNode.Parse(json);
        JsonArray? aircraft = root?["aircraft"]?.AsArray();
        if (aircraft is null)
        {
            return availableDifficulties.ToDictionary(d => d, _ => 0);
        }

        var counts = new Dictionary<string, int>();
        foreach (string ceiling in availableDifficulties)
        {
            int maxRank = GetRank(ceiling);
            int count = 0;
            foreach (JsonNode? ac in aircraft)
            {
                string? diff = ac?["difficulty"]?.GetValue<string>();
                int rank = GetRank(diff);
                if (rank <= maxRank)
                {
                    count++;
                }
            }
            counts[ceiling] = count;
        }

        return counts;
    }

    /// <summary>
    /// Filters aircraft in the JSON to only include those at or below
    /// the given difficulty ceiling. Returns the filtered JSON and any
    /// warnings about unrecognized difficulty values.
    /// </summary>
    public static (string Json, List<string> Warnings) FilterByDifficulty(string json, string maxDifficulty)
    {
        JsonNode root = JsonNode.Parse(json)!;
        JsonArray? aircraft = root["aircraft"]?.AsArray();
        if (aircraft is null)
        {
            return (json, []);
        }

        int maxRank = GetRank(maxDifficulty);
        var warnings = new List<string>();
        var unknownValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = aircraft.Count - 1; i >= 0; i--)
        {
            JsonNode? ac = aircraft[i];
            string? diff = ac?["difficulty"]?.GetValue<string>();
            int rank = GetRank(diff);

            if (rank == -1 && !string.IsNullOrEmpty(diff) && unknownValues.Add(diff))
            {
                warnings.Add($"Unknown difficulty \"{diff}\" — aircraft included anyway");
            }

            if (rank > maxRank)
            {
                aircraft.RemoveAt(i);
            }
        }

        return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), warnings);
    }

    /// <summary>
    /// Pre-load hint for the pacing slider: whether any aircraft may make an initial call. Counts a <c>Parking</c> spawn,
    /// or a <c>Coordinates</c> / <c>FixOrFrd</c> spawn with no altitude and no speed, whose presets
    /// <see cref="InitialCallupClassifier.ClassifyPresets"/> does not classify as <see cref="InitialCallupPlan.None"/>. The
    /// server's post-load <c>HasParkingSpawns</c> stays authoritative.
    /// </summary>
    public static bool HasParkingSpawns(string json)
    {
        var root = JsonNode.Parse(json);
        JsonArray? aircraft = root?["aircraft"]?.AsArray();
        if (aircraft is null)
        {
            return false;
        }

        foreach (JsonNode? ac in aircraft)
        {
            if ((ac is null) || !IsGroundSpawn(ac["startingConditions"]))
            {
                continue;
            }

            string callsign = ac["aircraftId"]?.GetValue<string>() ?? "";
            if (InitialCallupClassifier.ClassifyPresets(callsign, PresetCommandsOf(ac)) != InitialCallupPlan.None)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A <c>Parking</c> spawn, or a <c>Coordinates</c> / <c>FixOrFrd</c> spawn with no altitude and no speed.</summary>
    private static bool IsGroundSpawn(JsonNode? startingConditions)
    {
        string? startingType = startingConditions?["type"]?.GetValue<string>();
        bool isParkingSpawn = string.Equals(startingType, "Parking", StringComparison.OrdinalIgnoreCase);
        bool isCoordinateSpawn =
            string.Equals(startingType, "Coordinates", StringComparison.OrdinalIgnoreCase)
            || string.Equals(startingType, "FixOrFrd", StringComparison.OrdinalIgnoreCase);
        bool hasNoAltitudeOrSpeed = (startingConditions?["altitude"] is null) && (startingConditions?["speed"] is null);
        return isParkingSpawn || (isCoordinateSpawn && hasNoAltitudeOrSpeed);
    }

    /// <summary>The aircraft's timed presets, each read from its <c>command</c> text alone; a preset with no command is skipped.</summary>
    private static List<PresetCommand> PresetCommandsOf(JsonNode aircraft)
    {
        var presets = new List<PresetCommand>();
        foreach (JsonNode? preset in aircraft["presetCommands"]?.AsArray() ?? [])
        {
            if (preset?["command"]?.GetValue<string>() is { } command)
            {
                presets.Add(new PresetCommand { Command = command });
            }
        }

        return presets;
    }

    public static bool HasArrivalGenerators(string json)
    {
        var root = JsonNode.Parse(json);
        JsonArray? generators = root?["aircraftGenerators"]?.AsArray();
        return generators is { Count: > 0 };
    }

    /// <summary>
    /// Returns the rank of a difficulty level (0=Easy, 1=Medium, 2=Hard).
    /// Null/empty returns -1 (always included).
    /// Unknown values return -1 (always included, with warning).
    /// </summary>
    private static int GetRank(string? difficulty)
    {
        if (string.IsNullOrEmpty(difficulty))
        {
            return -1;
        }

        for (int i = 0; i < DifficultyOrder.Length; i++)
        {
            if (string.Equals(difficulty, DifficultyOrder[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
