using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Ground data over the committed test layouts (<see cref="TestAirportGroundData"/>) in which the named airports are not
/// loaded yet: a read of one answers loading, as the server's service does while that airport's fetch runs.
/// </summary>
internal sealed class PartlyLoadingGroundData(params string[] loadingAirports) : IAirportGroundData
{
    private readonly TestAirportGroundData _loaded = new();
    private readonly HashSet<string> _loading = new(loadingAirports.Select(NavigationDatabase.NormalizeAirport), StringComparer.Ordinal);

    /// <summary>Loading for a named airport, else the committed layout and its GeoJSON.</summary>
    public GroundDataRead Read(string airportId) =>
        _loading.Contains(NavigationDatabase.NormalizeAirport(airportId)) ? new GroundDataRead(false, null, null) : _loaded.Read(airportId);
}
