using System.Text.Json;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

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

    /// <summary>Sanity bound on an authored ADW range — real windows sit within a few miles of the threshold.</summary>
    private const double MaxAdwRangeNm = 15.0;

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
                ImplicitConnectors = ParseImplicitConnectors(file, filePath, result),
                OneWayEdges = ParseOneWayEdges(file, filePath, result),
                BlockedTurns = ParseBlockedTurns(file, filePath, result),
                Adw = ParseAdw(file, filePath, result),
                ExitDirections = ParseExitDirections(file, filePath, result),
            }
        );
    }

    private static List<ExitDirectionOverride> ParseExitDirections(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var overrides = new List<ExitDirectionOverride>();
        var indexByRunway = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.ExitDirections.Count; i++)
        {
            var entry = file.ExitDirections[i];
            string location = $"{filePath}: exitDirections[{i}]";

            if (string.IsNullOrWhiteSpace(entry.Runway))
            {
                result.Warnings.Add($"{location} missing runway, skipping");
                continue;
            }

            ExitSide? side = entry.Side.Trim().ToLowerInvariant() switch
            {
                "left" => ExitSide.Left,
                "right" => ExitSide.Right,
                _ => null,
            };
            if (side is null)
            {
                result.Warnings.Add($"{location} ({entry.Runway}): side must be 'left' or 'right', got '{entry.Side}', skipping");
                continue;
            }

            string runway = RunwayIdentifier.NormalizeDesignator(entry.Runway.Trim().ToUpperInvariant());
            var parsed = new ExitDirectionOverride(runway, side.Value, entry.Notes);
            if (indexByRunway.TryGetValue(runway, out int existing))
            {
                result.Warnings.Add($"{location} ({runway}): duplicate runway in this file, last entry wins");
                overrides[existing] = parsed;
                continue;
            }

            indexByRunway[runway] = overrides.Count;
            overrides.Add(parsed);
        }

        return overrides;
    }

    private static List<AdwWindow> ParseAdw(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var windows = new List<AdwWindow>();
        for (int i = 0; i < file.Adw.Count; i++)
        {
            var entry = file.Adw[i];
            string location = $"{filePath}: adw[{i}]";

            if (string.IsNullOrWhiteSpace(entry.ArrivalRunway) || string.IsNullOrWhiteSpace(entry.DepartureRunway))
            {
                result.Warnings.Add($"{location} requires both arrivalRunway and departureRunway, skipping");
                continue;
            }

            string arrival = RunwayIdentifier.NormalizeDesignator(entry.ArrivalRunway.Trim().ToUpperInvariant());
            string departure = RunwayIdentifier.NormalizeDesignator(entry.DepartureRunway.Trim().ToUpperInvariant());

            // A window is always between two converging runways; naming the same one twice is a typo.
            if (string.Equals(arrival, departure, StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"{location} ({arrival}): arrivalRunway and departureRunway must differ, skipping");
                continue;
            }

            // The window runs from the inner range outbound to the outer range; an inverted or
            // zero-width pair would draw two marks with no window between them.
            if (entry.OuterNm <= entry.InnerNm)
            {
                result.Warnings.Add($"{location} ({arrival}/{departure}): outerNm must exceed innerNm, skipping");
                continue;
            }

            // P/CG: the outer range is the point "on the final approach course" — it is always ahead of
            // the threshold, so a non-positive value would put both ends of the window on the runway.
            if (entry.OuterNm <= 0)
            {
                result.Warnings.Add($"{location} ({arrival}/{departure}): outerNm must be positive (out on final), skipping");
                continue;
            }

            // Anything beyond a normal final is authored data gone wrong, not a real window.
            if (entry.OuterNm > MaxAdwRangeNm || entry.InnerNm < -MaxAdwRangeNm)
            {
                result.Warnings.Add($"{location} ({arrival}/{departure}): ranges must be within ±{MaxAdwRangeNm} nm, skipping");
                continue;
            }

            windows.Add(new AdwWindow(arrival, departure, entry.OuterNm, entry.InnerNm, entry.Notes));
        }

        return windows;
    }

    private static List<BlockedTurn> ParseBlockedTurns(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var turns = new List<BlockedTurn>();
        for (int i = 0; i < file.BlockedTurns.Count; i++)
        {
            var entry = file.BlockedTurns[i];
            if (entry.Path.Count < 3)
            {
                result.Warnings.Add($"{filePath}: blockedTurns[{i}] needs at least 3 path points (an L-shape through the apex), skipping");
                continue;
            }

            var points = ParseWaypointPath(entry.Path, $"blockedTurns[{i}]", filePath, result);
            if (points is null)
            {
                continue;
            }

            turns.Add(new BlockedTurn(points, entry.Notes));
        }

        return turns;
    }

    private static List<OneWayConstraint> ParseOneWayEdges(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var constraints = new List<OneWayConstraint>();
        for (int i = 0; i < file.OneWayEdges.Count; i++)
        {
            var entry = file.OneWayEdges[i];
            if (entry.Path.Count < 2)
            {
                result.Warnings.Add($"{filePath}: oneWayEdges[{i}] needs at least 2 path points, skipping");
                continue;
            }

            var points = ParseWaypointPath(entry.Path, $"oneWayEdges[{i}]", filePath, result);
            if (points is null)
            {
                continue;
            }

            bool blockBoth = ParseBlockMode(entry.Block, filePath, i, result);
            constraints.Add(new OneWayConstraint(points, blockBoth, entry.Notes));
        }

        return constraints;
    }

    private static List<OneWayPoint>? ParseWaypointPath(List<OneWayWaypoint> path, string location, string filePath, AirportSidecarLoadResult result)
    {
        var points = new List<OneWayPoint>(path.Count);
        foreach (var wp in path)
        {
            if (wp.Point.Length != 2)
            {
                result.Warnings.Add($"{filePath}: {location} point must be [lon, lat], skipping");
                return null;
            }

            string? taxiway = string.IsNullOrWhiteSpace(wp.Taxiway) ? null : wp.Taxiway.Trim().ToUpperInvariant();
            points.Add(new OneWayPoint(Lat: wp.Point[1], Lon: wp.Point[0], Taxiway: taxiway));
        }

        return points;
    }

    private static bool ParseBlockMode(string? block, string filePath, int i, AirportSidecarLoadResult result)
    {
        if (string.IsNullOrWhiteSpace(block) || block.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (block.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        result.Warnings.Add($"{filePath}: oneWayEdges[{i}] unknown block '{block}' (expected 'reverse' or 'both'), defaulting to reverse");
        return false;
    }

    private static List<ImplicitConnectorEntry> ParseImplicitConnectors(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var connectors = new List<ImplicitConnectorEntry>();
        for (int i = 0; i < file.ImplicitConnectors.Count; i++)
        {
            var entry = file.ImplicitConnectors[i];
            if (string.IsNullOrWhiteSpace(entry.Connector))
            {
                result.Warnings.Add($"{filePath}: implicitConnectors[{i}] missing connector, skipping");
                continue;
            }

            if (entry.Between.Count != 2 || entry.Between.Any(string.IsNullOrWhiteSpace))
            {
                result.Warnings.Add(
                    $"{filePath}: implicitConnectors[{i}] ({entry.Connector}) requires exactly 2 non-blank 'between' taxiways, skipping"
                );
                continue;
            }

            connectors.Add(
                new ImplicitConnectorEntry
                {
                    Connector = entry.Connector.Trim().ToUpperInvariant(),
                    Between = [.. entry.Between.Select(b => b.Trim().ToUpperInvariant())],
                    Notes = entry.Notes,
                }
            );
        }

        return connectors;
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
