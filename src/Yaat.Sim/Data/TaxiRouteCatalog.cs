namespace Yaat.Sim.Data;

/// <summary>
/// Airport-keyed lookup over a flat list of <see cref="TaxiRouteDefinition"/>s loaded from
/// per-ARTCC JSON files. Layout-agnostic: callers do their own validation against the airport
/// ground graph (typically via
/// <see cref="Yaat.Sim.Data.Airport.TaxiPathfinderV2.ResolveExplicitPath"/> at menu-build time).
/// </summary>
public sealed class TaxiRouteCatalog
{
    public static TaxiRouteCatalog Empty { get; } = new(routes: []);

    private readonly Dictionary<string, List<TaxiRouteDefinition>> _byAirport = new(StringComparer.OrdinalIgnoreCase);

    public TaxiRouteCatalog(IEnumerable<TaxiRouteDefinition> routes)
    {
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.AirportId))
            {
                continue;
            }

            string key = NavigationDatabase.NormalizeAirport(route.AirportId);
            if (!_byAirport.TryGetValue(key, out var list))
            {
                list = [];
                _byAirport[key] = list;
            }

            list.Add(route);
        }
    }

    /// <summary>
    /// Returns routes registered for the given airport. Accepts either ICAO ("KOAK") or
    /// FAA short form ("OAK"); both normalize to the same key.
    /// </summary>
    public IReadOnlyList<TaxiRouteDefinition> GetRoutesForAirport(string airportIcao)
    {
        if (string.IsNullOrWhiteSpace(airportIcao))
        {
            return [];
        }

        string key = NavigationDatabase.NormalizeAirport(airportIcao);
        return _byAirport.TryGetValue(key, out var list) ? list : [];
    }
}
