using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class ProcedurePronunciationLoadResult
{
    public List<ProcedurePronunciationDefinition> Definitions { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads procedure base-name pronunciations from <c>{artccsBaseDir}/{ARTCC}/ProcedurePronunciations/*.json</c>, each file a
/// JSON array of <see cref="ProcedurePronunciationDefinition"/>, files in the ordinal order of their full path. Base names
/// come back trimmed and upper-cased, pronunciations trimmed with blanks dropped. Anything unreadable is skipped with a
/// warning; the loader never throws.
/// </summary>
public static class ProcedurePronunciationLoader
{
    private const string CategoryFolder = "ProcedurePronunciations";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Scans every ARTCC folder's <c>ProcedurePronunciations/*.json</c>.</summary>
    /// <param name="artccsBaseDir">The folder holding one subfolder per ARTCC.</param>
    /// <returns>The cleaned definitions and every warning raised while reading them.</returns>
    public static ProcedurePronunciationLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new ProcedurePronunciationLoadResult();
        foreach (string file in PronunciationText.CategoryFiles(artccsBaseDir, CategoryFolder, result.Warnings))
        {
            LoadFile(file, result);
        }

        return result;
    }

    private static void LoadFile(string filePath, ProcedurePronunciationLoadResult result)
    {
        List<ProcedurePronunciationDefinition?>? definitions;
        try
        {
            definitions = JsonSerializer.Deserialize<List<ProcedurePronunciationDefinition?>>(File.ReadAllText(filePath), JsonOptions);
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
            if (Clean(definitions[i], $"{filePath}[{i}]", result.Warnings) is { } cleaned)
            {
                result.Definitions.Add(cleaned);
            }
        }
    }

    private static ProcedurePronunciationDefinition? Clean(ProcedurePronunciationDefinition? def, string location, List<string> warnings)
    {
        string procedure = (def?.Procedure ?? "").Trim().ToUpperInvariant();
        if ((procedure.Length == 0) || procedure.Any(char.IsDigit))
        {
            warnings.Add($"{location}: procedure '{def?.Procedure}' is blank or carries version digits (give the base name, e.g. NIMI), skipping");
            return null;
        }

        IReadOnlyList<string> pronunciations = PronunciationText.Clean(def?.Pronunciations);
        if (pronunciations.Count == 0)
        {
            warnings.Add($"{location} ({procedure}): no pronunciations defined, skipping");
            return null;
        }

        return new ProcedurePronunciationDefinition { Procedure = procedure, Pronunciations = pronunciations };
    }
}
