namespace Yaat.Sim.Data;

/// <summary>
/// Airport-keyed lookup over the unified per-airport ground sidecars loaded from
/// <c>Data/ARTCCs/{ARTCC}/Airports/*.json</c>. Replaces the former separate AvoidTaxiway and TaxiRoute
/// catalogs. Keys accept either ICAO ("KOAK") or FAA short form ("OAK"); both normalize to the same
/// key via <see cref="NavigationDatabase.NormalizeAirport"/>. Multiple files for the same airport merge.
/// All accessors are never-null.
/// </summary>
public sealed class AirportSidecarCatalog
{
    public static AirportSidecarCatalog Empty { get; } = new([]);

    private static readonly IReadOnlySet<string> EmptyTaxiwaySet = new HashSet<string>();

    private readonly Dictionary<string, HashSet<string>> _avoidByAirport = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TaxiRouteDefinition>> _routesByAirport = new(StringComparer.OrdinalIgnoreCase);

    public AirportSidecarCatalog(IEnumerable<AirportSidecar> airports)
    {
        foreach (var airport in airports)
        {
            if (string.IsNullOrWhiteSpace(airport.AirportId))
            {
                continue;
            }

            string key = NavigationDatabase.NormalizeAirport(airport.AirportId);
            MergeAvoidedTaxiways(key, airport.AvoidTaxiways);
            MergeTaxiRoutes(key, airport.TaxiRoutes);
        }
    }

    private void MergeAvoidedTaxiways(string key, IReadOnlyList<AvoidTaxiwayEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (!_avoidByAirport.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _avoidByAirport[key] = set;
        }

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                set.Add(entry.Name);
            }
        }
    }

    private void MergeTaxiRoutes(string key, IReadOnlyList<TaxiRouteDefinition> routes)
    {
        if (routes.Count == 0)
        {
            return;
        }

        if (!_routesByAirport.TryGetValue(key, out var list))
        {
            list = [];
            _routesByAirport[key] = list;
        }

        list.AddRange(routes);
    }

    /// <summary>
    /// Set of taxiway names the AUTO router should avoid at the given airport. Never null — returns an
    /// empty set when the airport has no configured avoided taxiways.
    /// </summary>
    public IReadOnlySet<string> GetAvoidedTaxiways(string airportId)
    {
        if (string.IsNullOrWhiteSpace(airportId))
        {
            return EmptyTaxiwaySet;
        }

        string key = NavigationDatabase.NormalizeAirport(airportId);
        return _avoidByAirport.TryGetValue(key, out var set) ? set : EmptyTaxiwaySet;
    }

    /// <summary>
    /// Preset taxi routes registered for the given airport. Never null — returns an empty list when the
    /// airport has no configured routes.
    /// </summary>
    public IReadOnlyList<TaxiRouteDefinition> GetTaxiRoutes(string airportId)
    {
        if (string.IsNullOrWhiteSpace(airportId))
        {
            return [];
        }

        string key = NavigationDatabase.NormalizeAirport(airportId);
        return _routesByAirport.TryGetValue(key, out var list) ? list : [];
    }
}
