namespace Yaat.Sim.Data;

/// <summary>
/// Airport-keyed lookup of taxiways the AUTO taxi pathfinder should avoid, loaded from per-ARTCC
/// JSON files under <c>Data/ARTCCs/{ARTCC}/AvoidTaxiways/</c>. Returns a case-insensitive set of
/// taxiway names per airport; the pathfinder consults it via
/// <see cref="Yaat.Sim.Data.Airport.Pathfinding.SearchContext"/> and avoids the named taxiways
/// unless the destination is only reachable through them.
/// </summary>
public sealed class AvoidTaxiwayCatalog
{
    public static AvoidTaxiwayCatalog Empty { get; } = new([]);

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private readonly Dictionary<string, HashSet<string>> _byAirport = new(StringComparer.OrdinalIgnoreCase);

    public AvoidTaxiwayCatalog(IEnumerable<AvoidTaxiwayAirport> airports)
    {
        foreach (var airport in airports)
        {
            if (string.IsNullOrWhiteSpace(airport.AirportId))
            {
                continue;
            }

            string key = NavigationDatabase.NormalizeAirport(airport.AirportId);
            if (!_byAirport.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _byAirport[key] = set;
            }

            foreach (var entry in airport.Taxiways)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name))
                {
                    set.Add(entry.Name);
                }
            }
        }
    }

    /// <summary>
    /// Returns the set of taxiway names to avoid for the given airport. Accepts either ICAO
    /// ("KOAK") or FAA short form ("OAK"); both normalize to the same key. Never null — returns an
    /// empty set when the airport has no configured avoided taxiways.
    /// </summary>
    public IReadOnlySet<string> GetAvoidedTaxiways(string airportId)
    {
        if (string.IsNullOrWhiteSpace(airportId))
        {
            return EmptySet;
        }

        string key = NavigationDatabase.NormalizeAirport(airportId);
        return _byAirport.TryGetValue(key, out var set) ? set : EmptySet;
    }
}
