using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class AvoidTaxiwayLoadResult
{
    public List<AvoidTaxiwayAirport> Airports { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class AvoidTaxiwayLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/AvoidTaxiways/*.json</c> across every ARTCC
    /// subdirectory and loads each airport's avoided-taxiway list.
    /// </summary>
    public static AvoidTaxiwayLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new AvoidTaxiwayLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "AvoidTaxiways");
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

    private static void LoadFile(string filePath, AvoidTaxiwayLoadResult result)
    {
        AvoidTaxiwaysFile? file;
        try
        {
            var json = File.ReadAllText(filePath);
            file = JsonSerializer.Deserialize<AvoidTaxiwaysFile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse {filePath}: {ex.Message}");
            return;
        }

        if (file is null)
        {
            result.Warnings.Add($"Null result deserializing {filePath}");
            return;
        }

        if (string.IsNullOrWhiteSpace(file.AirportId))
        {
            result.Warnings.Add($"{filePath}: missing airportId, skipping");
            return;
        }

        string airportId = file.AirportId.Trim().ToUpperInvariant();

        var entries = new List<AvoidTaxiwayEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.Taxiways.Count; i++)
        {
            var entry = file.Taxiways[i];
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                result.Warnings.Add($"{filePath}[{i}]: missing taxiway name, skipping");
                continue;
            }

            string name = entry.Name.Trim().ToUpperInvariant();
            if (!seen.Add(name))
            {
                continue;
            }

            entries.Add(new AvoidTaxiwayEntry { Name = name, Notes = entry.Notes });
        }

        if (entries.Count == 0)
        {
            result.Warnings.Add($"{filePath}: no valid taxiways, skipping");
            return;
        }

        result.Airports.Add(new AvoidTaxiwayAirport(airportId, entries));
    }
}
