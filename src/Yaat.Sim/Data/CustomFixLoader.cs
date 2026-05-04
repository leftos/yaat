using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class CustomFixLoadResult
{
    public List<CustomFixDefinition> Fixes { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class CustomFixLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/CustomFixes/*.json</c> across every ARTCC
    /// subdirectory and loads custom fix definitions from each.
    /// </summary>
    public static CustomFixLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new CustomFixLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "CustomFixes");
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

    private static void LoadFile(string filePath, CustomFixLoadResult result)
    {
        List<CustomFixDefinition>? definitions;
        try
        {
            var json = File.ReadAllText(filePath);
            definitions = JsonSerializer.Deserialize<List<CustomFixDefinition>>(json, JsonOptions);
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

            if (def.Aliases.Count == 0)
            {
                result.Warnings.Add($"{location}: no aliases defined, skipping");
                continue;
            }

            bool hasLatLon = def.Lat.HasValue && def.Lon.HasValue;
            bool hasFrd = !string.IsNullOrWhiteSpace(def.Frd);

            if (!hasLatLon && !hasFrd)
            {
                result.Warnings.Add($"{location} ({def.Aliases[0]}): " + "must specify either lat/lon or frd");
                continue;
            }

            if (hasLatLon && hasFrd)
            {
                result.Warnings.Add($"{location} ({def.Aliases[0]}): " + "has both lat/lon and frd, using lat/lon");
                def.Frd = null;
            }

            result.Fixes.Add(def);
        }
    }
}
