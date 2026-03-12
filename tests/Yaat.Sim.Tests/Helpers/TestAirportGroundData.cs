using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// IAirportGroundData backed by GeoJSON files in TestData/.
/// Accepts both ICAO (e.g. "KOAK") and short-code (e.g. "OAK") airport identifiers.
/// Silently returns null for unknown airports (same convention as AirportE2ETests).
/// </summary>
internal sealed class TestAirportGroundData : IAirportGroundData
{
    private const string TestDataDir = "TestData";

    private readonly Dictionary<string, AirportGroundLayout?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AirportGroundLayout? GetLayout(string airportId)
    {
        // Normalise "KOAK" → "OAK" (strip leading K for standard US 4-letter ICAO codes)
        string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;

        if (_cache.TryGetValue(shortId, out var cached))
        {
            return cached;
        }

        string path = Path.Combine(TestDataDir, $"{shortId.ToLowerInvariant()}.geojson");
        AirportGroundLayout? layout = null;

        if (File.Exists(path))
        {
            layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, null);
        }

        _cache[shortId] = layout;
        return layout;
    }
}
