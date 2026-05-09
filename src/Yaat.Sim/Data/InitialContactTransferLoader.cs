using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class InitialContactTransferLoadResult
{
    public List<InitialContactTransferRule> Rules { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class InitialContactTransferLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/InitialContactTransfers/*.json</c> across every
    /// ARTCC subdirectory and loads facility-specific radio-transfer exceptions.
    /// </summary>
    public static InitialContactTransferLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new InitialContactTransferLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "InitialContactTransfers");
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var artccId = Path.GetFileName(artccDir).Trim().ToUpperInvariant();
            foreach (var file in Directory.GetFiles(categoryDir, "*.json"))
            {
                LoadFile(file, artccId, result);
            }
        }

        return result;
    }

    private static void LoadFile(string filePath, string artccId, InitialContactTransferLoadResult result)
    {
        List<InitialContactTransferRule>? rules;
        try
        {
            var json = File.ReadAllText(filePath);
            rules = JsonSerializer.Deserialize<List<InitialContactTransferRule>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse {filePath}: {ex.Message}");
            return;
        }

        if (rules is null)
        {
            result.Warnings.Add($"Null result deserializing {filePath}");
            return;
        }

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var location = $"{filePath}[{i}]";

            if (string.IsNullOrWhiteSpace(rule.AirportId))
            {
                result.Warnings.Add($"{location}: missing airportId, skipping");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.FromPositionType))
            {
                result.Warnings.Add($"{location} ({rule.AirportId}): missing fromPositionType, skipping");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.ToPositionType))
            {
                result.Warnings.Add($"{location} ({rule.AirportId}): missing toPositionType, skipping");
                continue;
            }

            rule.ArtccId = artccId;
            rule.AirportId = NavigationDatabase.NormalizeAirport(rule.AirportId);
            rule.FromPositionType = NormalizePositionType(rule.FromPositionType);
            rule.ToPositionType = NormalizePositionType(rule.ToPositionType);
            result.Rules.Add(rule);
        }
    }

    private static string NormalizePositionType(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "LC" => "TWR",
            "GC" => "GND",
            "DEL" => "GND",
            "DEP" => "APP",
            var normalized => normalized,
        };
}
