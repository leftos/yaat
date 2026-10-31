using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class GatePronunciationLoadResult
{
    public List<GatePronunciationFile> Files { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads gate and spot pronunciations and named ground places from <c>{artccsBaseDir}/{ARTCC}/GatePronunciations/*.json</c>,
/// one <see cref="GatePronunciationFile"/> per file, files in the ordinal order of their full path. Names and stand
/// patterns come back trimmed and upper-cased, pronunciations trimmed with blanks dropped. Anything unreadable is skipped
/// with a warning; the loader never throws.
/// </summary>
public static class GatePronunciationLoader
{
    private const string CategoryFolder = "GatePronunciations";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Scans every ARTCC folder's <c>GatePronunciations/*.json</c>.</summary>
    /// <param name="artccsBaseDir">The folder holding one subfolder per ARTCC.</param>
    /// <returns>The cleaned files and every warning raised while reading them.</returns>
    public static GatePronunciationLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new GatePronunciationLoadResult();
        foreach (string file in PronunciationText.CategoryFiles(artccsBaseDir, CategoryFolder, result.Warnings))
        {
            LoadFile(file, result);
        }

        return result;
    }

    private static void LoadFile(string filePath, GatePronunciationLoadResult result)
    {
        GatePronunciationFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GatePronunciationFile>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse {filePath}: {ex.Message}");
            return;
        }

        if (parsed is null)
        {
            result.Warnings.Add($"Null result deserializing {filePath}");
            return;
        }

        if (string.IsNullOrWhiteSpace(parsed.AirportId))
        {
            result.Warnings.Add($"{filePath}: missing airportId, skipping the file");
            return;
        }

        result.Files.Add(
            new GatePronunciationFile
            {
                AirportId = parsed.AirportId.Trim().ToUpperInvariant(),
                Gates = CleanNames(parsed.Gates, $"{filePath} gates", result.Warnings),
                Spots = CleanNames(parsed.Spots, $"{filePath} spots", result.Warnings),
                Places = CleanPlaces(parsed.Places, $"{filePath} places", result.Warnings),
            }
        );
    }

    private static IReadOnlyList<NamePronunciation> CleanNames(IReadOnlyList<NamePronunciation?>? entries, string section, List<string> warnings)
    {
        var cleaned = new List<NamePronunciation>();
        IReadOnlyList<NamePronunciation?> source = entries ?? [];
        for (int i = 0; i < source.Count; i++)
        {
            NamePronunciation? entry = source[i];
            if (CleanEntry(entry?.Name, entry?.Pronunciations, $"{section}[{i}]", warnings) is { } valid)
            {
                cleaned.Add(new NamePronunciation { Name = valid.Name, Pronunciations = valid.Pronunciations });
            }
        }

        return cleaned.AsReadOnly();
    }

    private static IReadOnlyList<GroundPlaceDefinition> CleanPlaces(
        IReadOnlyList<GroundPlaceDefinition?>? entries,
        string section,
        List<string> warnings
    )
    {
        var cleaned = new List<GroundPlaceDefinition>();
        IReadOnlyList<GroundPlaceDefinition?> source = entries ?? [];
        for (int i = 0; i < source.Count; i++)
        {
            GroundPlaceDefinition? entry = source[i];
            string location = $"{section}[{i}]";
            if (entry is null)
            {
                warnings.Add($"{location}: null entry, skipping");
                continue;
            }

            if (CleanEntry(entry.Name, entry.Pronunciations, location, warnings) is { } valid)
            {
                cleaned.Add(
                    new GroundPlaceDefinition
                    {
                        Name = valid.Name,
                        Pronunciations = valid.Pronunciations,
                        Stands = CleanStands(entry.Stands, location, warnings),
                        Notes = entry.Notes,
                    }
                );
            }
        }

        return cleaned.AsReadOnly();
    }

    private static (string Name, IReadOnlyList<string> Pronunciations)? CleanEntry(
        string? name,
        IReadOnlyList<string?>? pronunciations,
        string location,
        List<string> warnings
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            warnings.Add($"{location}: missing name, skipping");
            return null;
        }

        IReadOnlyList<string> spoken = PronunciationText.Clean(pronunciations);
        if (spoken.Count == 0)
        {
            warnings.Add($"{location} ({name.Trim()}): no pronunciations defined, skipping");
            return null;
        }

        return (name.Trim().ToUpperInvariant(), spoken);
    }

    private static IReadOnlyList<string> CleanStands(IReadOnlyList<string?>? stands, string location, List<string> warnings)
    {
        var cleaned = new List<string>();
        foreach (string? stand in stands ?? [])
        {
            string pattern = (stand ?? "").Trim().ToUpperInvariant();
            int star = pattern.IndexOf('*', StringComparison.Ordinal);
            if ((pattern.Length == 0) || ((star >= 0) && (star != pattern.Length - 1)))
            {
                warnings.Add($"{location}: stand pattern '{stand}' is blank or has '*' before its end, skipping the pattern");
                continue;
            }

            cleaned.Add(pattern);
        }

        return cleaned.AsReadOnly();
    }
}
