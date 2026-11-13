using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Ground data over the committed test layouts (<see cref="TestAirportGroundData"/>) in which the named airports are not
/// loaded yet: a read of one answers loading, as the server's service does while that airport's fetch runs, until
/// <see cref="Release"/> lands it. Every read is counted per normalized airport.
/// </summary>
internal sealed class PartlyLoadingGroundData(params string[] loadingAirports) : IAirportGroundData
{
    private readonly TestAirportGroundData _loaded = new();
    private readonly HashSet<string> _loading = new(loadingAirports.Select(NavigationDatabase.NormalizeAirport), StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

    /// <summary>Loading for a named airport not yet released, else the committed layout and its GeoJSON.</summary>
    public GroundDataRead Read(string airportId)
    {
        string key = NavigationDatabase.NormalizeAirport(airportId);
        _reads[key] = ReadCount(key) + 1;
        return _loading.Contains(key) ? new GroundDataRead(false, null, null) : _loaded.Read(airportId);
    }

    /// <summary>Lands <paramref name="airportId"/>'s fetch: every later read of it answers loaded.</summary>
    public void Release(string airportId) => _loading.Remove(NavigationDatabase.NormalizeAirport(airportId));

    /// <summary>How many times <paramref name="airportId"/> has been read, in either form of its code.</summary>
    public int ReadCount(string airportId) => _reads.GetValueOrDefault(NavigationDatabase.NormalizeAirport(airportId));
}
