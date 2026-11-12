using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// IAirportGroundData that serves SFO from a caller-specified GeoJSON file instead of the
/// shared <c>TestData/sfo.geojson</c>, delegating every other airport to a standard
/// <see cref="TestAirportGroundData"/>.
///
/// Recording-replay tests reproduce a controller's exact taxi commands against the airport
/// layout that was live when the recording was made. The shared <c>sfo.geojson</c> is
/// periodically re-downloaded; a refresh that only truncates coordinate precision can still
/// shift fillet/spot nodes by a sub-meter margin and break a borderline route that the
/// recorded command relied on (e.g. reaching a spot from the end of a taxiway). Pinning the
/// test to a committed full-precision snapshot keeps the replay deterministic across those
/// refreshes.
/// </summary>
internal sealed class PinnedSfoGroundData(string sfoGeoJsonPath) : IAirportGroundData
{
    private readonly TestAirportGroundData _fallback = new();
    private readonly string _sfoGeoJsonPath = sfoGeoJsonPath;
    private readonly Lock _lock = new();
    private AirportGroundLayout? _sfo;
    private bool _loaded;
    private string? _sfoSource;
    private bool _sourceLoaded;

    public AirportGroundLayout? GetLayout(string airportId)
    {
        string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
        if (!string.Equals(shortId, "SFO", StringComparison.OrdinalIgnoreCase))
        {
            return _fallback.GetLayout(airportId);
        }

        lock (_lock)
        {
            if (!_loaded)
            {
                _sfo = File.Exists(_sfoGeoJsonPath)
                    ? GeoJsonParser.Parse(
                        "SFO",
                        File.ReadAllText(_sfoGeoJsonPath),
                        null,
                        TestLayoutInputs.For("SFO", videoMap: null),
                        FilletMode.Standard
                    )
                    : null;
                _loaded = true;
            }

            return _sfo;
        }
    }

    public string? GetSourceGeoJson(string airportId)
    {
        string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
        if (!string.Equals(shortId, "SFO", StringComparison.OrdinalIgnoreCase))
        {
            return _fallback.GetSourceGeoJson(airportId);
        }

        // Read once: an engine reads its ground data every tick.
        lock (_lock)
        {
            if (!_sourceLoaded)
            {
                _sfoSource = File.Exists(_sfoGeoJsonPath) ? File.ReadAllText(_sfoGeoJsonPath) : null;
                _sourceLoaded = true;
            }

            return _sfoSource;
        }
    }

    /// <summary>The layout and its GeoJSON, always loaded: the files are read on the caller.</summary>
    public GroundDataRead Read(string airportId) => new(true, GetLayout(airportId), GetSourceGeoJson(airportId));
}
