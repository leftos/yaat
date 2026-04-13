using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class FixPronunciationLoadResult
{
    public List<FixPronunciationDefinition> Definitions { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads fix pronunciation hint JSON from a directory tree (typically
/// <c>Data/FixPronunciations/{ARTCC}/*.json</c>). Each file is a JSON array of
/// <see cref="FixPronunciationDefinition"/> records. Malformed files are skipped with a warning —
/// the loader never throws.
/// </summary>
public static class FixPronunciationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static FixPronunciationLoadResult LoadAll(string baseDir)
    {
        var result = new FixPronunciationLoadResult();

        if (!Directory.Exists(baseDir))
        {
            result.Warnings.Add($"Fix pronunciations directory not found: {baseDir}");
            return result;
        }

        var files = Directory.GetFiles(baseDir, "*.json", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            LoadFile(file, result);
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

            if (def.Pronunciations.Count == 0)
            {
                result.Warnings.Add($"{location} ({def.Fix}): no pronunciations defined, skipping");
                continue;
            }

            result.Definitions.Add(def);
        }
    }
}
