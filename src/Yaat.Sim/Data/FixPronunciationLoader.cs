using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class FixPronunciationLoadResult
{
    public List<FixPronunciationDefinition> Definitions { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads fix pronunciation hint JSON from <c>{artccsBaseDir}/{ARTCC}/FixPronunciations/*.json</c>.
/// Each file is a JSON array of <see cref="FixPronunciationDefinition"/> records. Malformed files
/// are skipped with a warning — the loader never throws.
/// </summary>
public static class FixPronunciationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/FixPronunciations/*.json</c> across every ARTCC
    /// subdirectory and loads pronunciation definitions from each.
    /// </summary>
    public static FixPronunciationLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new FixPronunciationLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "FixPronunciations");
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(categoryDir, "*.json"))
            {
                LoadFile(file, result);
            }
        }

        return result;
    }

    private static void LoadFile(string filePath, FixPronunciationLoadResult result)
    {
        List<FixPronunciationDefinition>? definitions;
        try
        {
            var json = File.ReadAllText(filePath);
            definitions = JsonSerializer.Deserialize<List<FixPronunciationDefinition>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse {filePath}: {ex.Message}");
            return;
        }

        if (definitions is null)
        {
            result.Warnings.Add($"Null result deserializing {filePath}");
            return;
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            var def = definitions[i];
            var location = $"{filePath}[{i}]";

            if (string.IsNullOrWhiteSpace(def.Fix))
            {
                result.Warnings.Add($"{location}: missing fix name, skipping");
                continue;
            }

            if (def.Pronunciations.Count == 0 && string.IsNullOrWhiteSpace(def.DisplayName))
            {
                result.Warnings.Add($"{location} ({def.Fix}): no pronunciations or displayName defined, skipping");
                continue;
            }

            result.Definitions.Add(def);
        }
    }
}
