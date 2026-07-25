using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

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
/// The parameterless constructor uses <see cref="FilletMode.Standard"/> — the production
/// fillet generator — so the ~150 existing call sites build the shipping graph.
/// Pass <see cref="FilletMode.None"/> explicitly for raw-graph (unfilleted) tests.
/// </summary>
internal sealed class TestAirportGroundData : IAirportGroundData
{
    private const string TestDataDir = "TestData";

    private static readonly Dictionary<(FilletMode Mode, string ShortId), AirportGroundLayout?> Cache = new();
    private static readonly object CacheLock = new();

    private readonly FilletMode _filletMode;

    public TestAirportGroundData()
        : this(FilletMode.Standard) { }

    public TestAirportGroundData(FilletMode filletMode)
    {
        _filletMode = filletMode;
    }

    public AirportGroundLayout? GetLayout(string airportId)
    {
        string shortId = NormalizeShortId(airportId);
        var key = (_filletMode, shortId);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            string path = GetGeoJsonPath(shortId);
            AirportGroundLayout? layout = null;

            if (File.Exists(path))
            {
                // Both production call sites (AirportLayoutDownloader, the server's AirportGroundDataService) parse
                // with the real airport code so runway widths come from navdata. Passing null instead fell back to a
                // 150 ft default that no real runway uses, which moves the on-runway rectangle and therefore where
                // hold-short nodes seat — the harness was building a different graph than the shipping one.
                //
                // EnsureInitialized is called unconditionally so the width does not depend on whether some other test
                // class happened to load nav data first: the layout is cached per process, so a race there would bake
                // one arbitrary graph in for the whole run. Codes unknown to navdata (the non-airport fixtures such as
                // issue172-sfo) simply keep the default width, and a fresh/offline checkout with no nav data falls
                // back to null so the harness's silent-skip convention still holds.
                TestVnasData.EnsureInitialized();
                string? runwayAirportCode = NavigationDatabase.InstanceOrNull is not null ? shortId.ToUpperInvariant() : null;

                layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), runwayAirportCode, _filletMode);
            }

            Cache[key] = layout;
            return layout;
        }
    }

    public string? GetSourceGeoJson(string airportId)
    {
        string path = GetGeoJsonPath(NormalizeShortId(airportId));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string NormalizeShortId(string airportId)
    {
        return airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
    }

    /// <summary>
    /// Resolves the GeoJSON fixture for <paramref name="shortId"/>, matching the filename without regard to case.
    /// Windows resolves paths case-insensitively but the Linux CI runner does not, so a fixture committed under a
    /// different case than the lowercase convention (e.g. <c>SMF.geojson</c>) resolves locally and silently vanishes
    /// on CI — <see cref="GetLayout"/> returns null and every consuming test takes its skip path instead of running.
    /// Matching case-insensitively means filename case can never quietly gate coverage.
    /// </summary>
    private static string GetGeoJsonPath(string shortId)
    {
        string preferred = Path.Combine(TestDataDir, $"{shortId.ToLowerInvariant()}.geojson");
        if (File.Exists(preferred) || !Directory.Exists(TestDataDir))
        {
            return preferred;
        }

        string wanted = $"{shortId}.geojson";
        foreach (string candidate in Directory.EnumerateFiles(TestDataDir, "*.geojson"))
        {
            if (string.Equals(Path.GetFileName(candidate), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return preferred;
    }
}
