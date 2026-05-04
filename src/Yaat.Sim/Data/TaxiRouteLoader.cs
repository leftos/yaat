using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class TaxiRouteLoadResult
{
    public List<TaxiRouteDefinition> Routes { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class TaxiRouteLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/TaxiRoutes/*.json</c> across every ARTCC
    /// subdirectory and loads route definitions from each.
    /// </summary>
    public static TaxiRouteLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new TaxiRouteLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "TaxiRoutes");
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

    private static void LoadFile(string filePath, TaxiRouteLoadResult result)
    {
        TaxiRoutesFile? file;
        try
        {
            var json = File.ReadAllText(filePath);
            file = JsonSerializer.Deserialize<TaxiRoutesFile>(json, JsonOptions);
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
            result.Warnings.Add($"{filePath}: missing airportId, skipping all routes");
            return;
        }

        string airportId = file.AirportId.Trim().ToUpperInvariant();

        for (int i = 0; i < file.Routes.Count; i++)
        {
            var def = file.Routes[i];
            var location = $"{filePath}[{i}]";

            if (string.IsNullOrWhiteSpace(def.Name))
            {
                result.Warnings.Add($"{location}: missing name, skipping");
                continue;
            }

            if (def.GetPathTokens().Count == 0)
            {
                result.Warnings.Add($"{location} ({def.Name}): empty path, skipping");
                continue;
            }

            int destinationCount =
                (string.IsNullOrWhiteSpace(def.DestinationRunway) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(def.DestinationParking) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(def.DestinationSpot) ? 0 : 1);

            if (destinationCount > 1)
            {
                result.Warnings.Add(
                    $"{location} ({def.Name}): conflicting destinations (set at most one of destinationRunway/destinationParking/destinationSpot), skipping"
                );
                continue;
            }

            def.AirportId = airportId;
            result.Routes.Add(def);
        }
    }
}
