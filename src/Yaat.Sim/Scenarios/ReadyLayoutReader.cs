using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Reads ground layouts at a site where they must already be loaded: a scenario load after its prefetch, a restore, a
/// recording or snapshot load, a generator spawn. An airport that is not loaded reads as null and is logged as an error
/// once per airport over this reader's life, keyed by its normalized code so <c>KOAK</c> and <c>OAK</c> log once.
/// </summary>
/// <param name="groundData">The ground data to read.</param>
/// <param name="logger">Where an airport that is not loaded is reported.</param>
public sealed class ReadyLayoutReader(IAirportGroundData groundData, ILogger logger)
{
    private readonly IAirportGroundData _groundData = groundData;
    private readonly ILogger _logger = logger;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// <summary>Reads <paramref name="airportId"/>'s layout, which <paramref name="site"/> expects already loaded.</summary>
    /// <param name="airportId">The airport, by ICAO or FAA id; null or empty reads as no layout.</param>
    /// <param name="site">The site reading, named in the error.</param>
    /// <returns>The layout, or null when the airport has none, is not named or is not loaded.</returns>
    public AirportGroundLayout? Read(string? airportId, string site)
    {
        if (string.IsNullOrEmpty(airportId))
        {
            return null;
        }

        GroundDataRead read = _groundData.Read(airportId);
        if (!read.IsLoaded && _reported.TryAdd(NavigationDatabase.NormalizeAirport(airportId), 0))
        {
            _logger.LogError("Ground layout for {AirportId} is not loaded at {Site} (its prefetch failed or it is still loading)", airportId, site);
        }

        return read.Layout;
    }
}
