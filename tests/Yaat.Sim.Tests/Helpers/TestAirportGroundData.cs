using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// IAirportGroundData backed by GeoJSON files in TestData/.
/// Accepts both ICAO (e.g. "KOAK") and short-code (e.g. "OAK") airport identifiers.
/// Silently returns null for unknown airports (same convention as AirportE2ETests).
///
/// Layouts are immutable after construction (no runtime mutations from phases or
/// commands), so a single shared instance is safe. Parse+fillet runs once per airport
/// per test process (~500ms OAK, ~2700ms SFO), then all callers share the result.
/// </summary>
internal sealed class TestAirportGroundData : IAirportGroundData
{
    private const string TestDataDir = "TestData";

    private static readonly Dictionary<string, AirportGroundLayout?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public AirportGroundLayout? GetLayout(string airportId)
    {
        string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(shortId, out var cached))
            {
                return cached;
            }

            string path = Path.Combine(TestDataDir, $"{shortId.ToLowerInvariant()}.geojson");
            AirportGroundLayout? layout = null;

            if (File.Exists(path))
            {
                layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null);
            }

            Cache[shortId] = layout;
            return layout;
        }
    }
}
