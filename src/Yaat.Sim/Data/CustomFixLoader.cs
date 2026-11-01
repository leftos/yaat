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

        foreach (string artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "CustomFixes");
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(categoryDir, "*.json"))
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
            string json = File.ReadAllText(filePath);
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
            CustomFixDefinition def = definitions[i];
            string location = $"{filePath}[{i}]";

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

            def.SourceFile = filePath;
            result.Fixes.Add(def);
        }
    }

    /// <summary>
    /// The FAA id of the airport a landmark file serves, from its name <c>&lt;faa&gt;-landmarks.json</c>, uppercased
    /// ("OAK" from <c>oak-landmarks.json</c>); null for any other custom-fix file.
    /// </summary>
    /// <param name="sourceFile">The path or name of the custom-fix file.</param>
    public static string? LandmarkAirport(string sourceFile)
    {
        const string Suffix = "-landmarks.json";
        string name = Path.GetFileName(sourceFile);
        if (!name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) || (name.Length == Suffix.Length))
        {
            return null;
        }

        return name[..^Suffix.Length].ToUpperInvariant();
    }
}

/// <summary>A landmark custom fix, with the airport whose landmark file declares it.</summary>
/// <param name="Airport">The FAA id of the airport from the file name ("OAK").</param>
/// <param name="Alias">The fix's first alias ("OAK30NUM").</param>
/// <param name="SpokenPatterns">The raw non-blank spoken patterns, trimmed and lowercased.</param>
/// <param name="Lat">The landmark's latitude.</param>
/// <param name="Lon">The landmark's longitude.</param>
public sealed record CustomFixLandmark(string Airport, string Alias, IReadOnlyList<string> SpokenPatterns, double Lat, double Lon);
