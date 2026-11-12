namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The airport ground layouts a simulation reads, keyed by airport id.
/// </summary>
public interface IAirportGroundData
{
    /// <summary>
    /// Reads <paramref name="airportId"/>'s ground layout without blocking.
    /// </summary>
    /// <param name="airportId">The airport, by ICAO or FAA id.</param>
    /// <returns>
    /// Loaded: the layout (null when the airport has no map) and its source GeoJSON, both from one cache entry.
    /// Loading: not loaded yet; its fetch is running, started or joined by this call, or a failed fetch waits for its
    /// retry.
    /// </returns>
    GroundDataRead Read(string airportId);
}
