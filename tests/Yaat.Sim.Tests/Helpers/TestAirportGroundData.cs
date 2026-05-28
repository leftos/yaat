using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// IAirportGroundData backed by GeoJSON files in TestData/.
/// Accepts both ICAO (e.g. "KOAK") and short-code (e.g. "OAK") airport identifiers.
/// Silently returns null for unknown airports (same convention as AirportE2ETests).
///
/// Layouts are immutable after construction (no runtime mutations from phases or
/// commands), so a single shared instance is safe. Parse+fillet runs once per
/// (fillet mode, airport) per test process (~500ms OAK, ~2700ms SFO), then all
/// callers share the result.
///
/// The parameterless constructor uses <see cref="FilletMode.Legacy"/> — the
/// production fillet generator — so the ~150 existing call sites are unaffected.
/// Pass <see cref="FilletMode.V2"/> to exercise the V2 arc generator in sim-level
/// validation tests while production stays on Legacy.
/// </summary>
internal sealed class TestAirportGroundData : IAirportGroundData
{
    private const string TestDataDir = "TestData";

    private static readonly Dictionary<(FilletMode Mode, string ShortId), AirportGroundLayout?> Cache = new();
    private static readonly object CacheLock = new();

    private readonly FilletMode _filletMode;

    public TestAirportGroundData()
        : this(FilletMode.Legacy) { }

    public TestAirportGroundData(FilletMode filletMode)
    {
        _filletMode = filletMode;
    }

    public AirportGroundLayout? GetLayout(string airportId)
    {
        string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
        var key = (_filletMode, shortId);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            string path = Path.Combine(TestDataDir, $"{shortId.ToLowerInvariant()}.geojson");
            AirportGroundLayout? layout = null;

            if (File.Exists(path))
            {
                layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, _filletMode);
            }

            Cache[key] = layout;
            return layout;
        }
    }
}
