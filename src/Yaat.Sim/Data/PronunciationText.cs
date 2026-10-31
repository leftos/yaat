namespace Yaat.Sim.Data;

/// <summary>Helpers the gate and procedure pronunciation loaders share.</summary>
internal static class PronunciationText
{
    /// <summary>Trims each pronunciation and drops the blank ones.</summary>
    /// <param name="pronunciations">The authored forms; null reads as none.</param>
    /// <returns>The non-blank forms, trimmed, in authored order.</returns>
    public static IReadOnlyList<string> Clean(IEnumerable<string?>? pronunciations) =>
        [.. (pronunciations ?? []).Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!.Trim())];

    /// <summary>
    /// Every <c>{artccsBaseDir}/{ARTCC}/{category}/*.json</c>, in the ordinal order of the full path. A missing base folder or
    /// a folder that cannot be listed adds a warning instead of throwing.
    /// </summary>
    /// <param name="artccsBaseDir">The folder holding one subfolder per ARTCC.</param>
    /// <param name="category">The category folder name ("GatePronunciations").</param>
    /// <param name="warnings">Receives a warning for each folder that could not be listed.</param>
    /// <returns>The files to load.</returns>
    public static IReadOnlyList<string> CategoryFiles(string artccsBaseDir, string category, List<string> warnings)
    {
        if (!Directory.Exists(artccsBaseDir))
        {
            warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return [];
        }

        try
        {
            return
            [
                .. Directory
                    .EnumerateDirectories(artccsBaseDir)
                    .Select(artccDir => Path.Combine(artccDir, category))
                    .Where(Directory.Exists)
                    .SelectMany(categoryDir => Directory.GetFiles(categoryDir, "*.json"))
                    .Order(StringComparer.Ordinal),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Failed to list {category} files under {artccsBaseDir}: {ex.Message}");
            return [];
        }
    }
}
