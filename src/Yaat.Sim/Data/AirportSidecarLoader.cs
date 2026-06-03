using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class AirportSidecarLoadResult
{
    public List<AirportSidecar> Airports { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads the unified per-airport ground sidecars from <c>Data/ARTCCs/{ARTCC}/Airports/*.json</c>.
/// Warn-don't-throw: a malformed file or section adds a warning and is skipped; the rest still load.
/// </summary>
public static class AirportSidecarLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/Airports/*.json</c> across every ARTCC subdirectory and parses
    /// each into an <see cref="AirportSidecar"/>.
    /// </summary>
    public static AirportSidecarLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new AirportSidecarLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "Airports");
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

    private static void LoadFile(string filePath, AirportSidecarLoadResult result)
    {
        AirportSidecarFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AirportSidecarFile>(File.ReadAllText(filePath), JsonOptions);
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

        result.Airports.Add(
            new AirportSidecar(airportId)
            {
                AvoidTaxiways = ParseAvoidTaxiways(file, filePath, result),
                TaxiRoutes = ParseTaxiRoutes(file, filePath, airportId, result),
            }
        );
    }

    private static List<AvoidTaxiwayEntry> ParseAvoidTaxiways(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var entries = new List<AvoidTaxiwayEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.AvoidTaxiways.Count; i++)
        {
            var entry = file.AvoidTaxiways[i];
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                result.Warnings.Add($"{filePath}: avoidTaxiways[{i}] missing name, skipping");
                continue;
            }

            string name = entry.Name.Trim().ToUpperInvariant();
            if (!seen.Add(name))
            {
                continue;
            }

            entries.Add(new AvoidTaxiwayEntry { Name = name, Notes = entry.Notes });
        }

        return entries;
    }

    private static List<TaxiRouteDefinition> ParseTaxiRoutes(
        AirportSidecarFile file,
        string filePath,
        string airportId,
        AirportSidecarLoadResult result
    )
    {
        var routes = new List<TaxiRouteDefinition>();
        for (int i = 0; i < file.TaxiRoutes.Count; i++)
        {
            var def = file.TaxiRoutes[i];
            string location = $"{filePath}: taxiRoutes[{i}]";

            if (string.IsNullOrWhiteSpace(def.Name))
            {
                result.Warnings.Add($"{location} missing name, skipping");
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
            routes.Add(def);
        }

        return routes;
    }
}
