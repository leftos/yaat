using System.IO.Compression;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Guards the coupling between recordings and the ground layouts they replay against.
///
/// <see cref="TestAirportGroundData.GetLayout"/> returns null for an airport with no committed
/// geojson, and consuming tests treat null as "skip". A geojson containing only runway
/// <c>LineString</c>s defeats that: the layout is non-null, so the skip never fires, but it has no
/// taxiways, parking, or hold-shorts — every ground query silently degrades instead of bailing.
/// These tests make both shapes visible so a new recording cannot quietly land on one.
/// </summary>
public class TestLayoutCoverageTests(ITestOutputHelper output)
{
    private const string TestDataDir = "TestData";

    /// <summary>
    /// Fixtures that contain runway <c>LineString</c>s only. They give airborne traffic runway geometry
    /// (thresholds, headings) at airports whose ground movement no test exercises; the parsed layout has
    /// an empty node/edge graph, so any ground query against one degrades rather than bailing. Committing
    /// the full vNAS map for each would add hundreds of KB for no coverage — but an airport must not be
    /// added here to silence a failure. If a test needs to taxi there, commit the real map.
    /// </summary>
    private static readonly HashSet<string> RunwayOnlyFixtures = new(StringComparer.OrdinalIgnoreCase) { "FAT", "HWD", "MER", "RNO", "SJC" };

    /// <summary>
    /// Airports a recording's manifest declares a ground layout for, but whose fixture cannot support
    /// ground movement (missing entirely, or runway-only). Replay of any aircraft there runs against a
    /// degraded graph. Each entry records why that is tolerable; every one is currently a secondary
    /// airport whose traffic only ever appears airborne in the asserting tests.
    /// </summary>
    private static readonly Dictionary<string, string> DeclaredButDegraded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ASE"] = "issue195-ase-lindz1 — asserts the LINDZ1 departure turn altitude; no committed map, documented in that test.",
        ["HOU"] = "issue187-star-dvia — IAH is primary; the KHOU traffic is asserted only on its departure altitude cap.",
        ["SJC"] = "issue216-auto-handoff and multi-cfix-preset — both assert handoff/queue state on airborne traffic.",
    };

    /// <summary>
    /// A layout can route ground traffic only if it has at least one edge that is not runway
    /// centerline or runway-crossing link — i.e. an actual taxiway, ramp, or parking connection.
    /// </summary>
    private static bool IsTaxiCapable(AirportGroundLayout layout) => layout.Edges.Any(e => !e.IsRunwayCenterline && !e.IsRunwayCrossingLink);

    /// <summary>Manifests record airport ids in mixed case and either ICAO or short form.</summary>
    private static string NormalizeId(string airportId)
    {
        string upper = airportId.ToUpperInvariant();
        return upper.Length == 4 && upper[0] == 'K' ? upper[1..] : upper;
    }

    private static List<string> CommittedGeoJsonShortIds() =>
        Directory.Exists(TestDataDir)
            ? Directory
                .EnumerateFiles(TestDataDir, "*.geojson")
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>
    /// Reads <c>manifest.json</c> from either archive shape: a v4 archive stores it at the root, a
    /// bug-report bundle nests the archive as <c>recording.yaat-recording.zip</c>. Returns null for
    /// anything else (legacy brotli/gzip recordings carry no manifest).
    /// </summary>
    private static RecordingManifest? ReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry("manifest.json") is { } direct)
            {
                using var stream = direct.Open();
                return JsonSerializer.Deserialize<RecordingManifest>(stream, RecordingJsonOptions.Default);
            }

            if (zip.GetEntry("recording.yaat-recording.zip") is not { } nested)
            {
                return null;
            }

            using var nestedStream = nested.Open();
            using var buffer = new MemoryStream();
            nestedStream.CopyTo(buffer);
            buffer.Position = 0;
            using var innerZip = new ZipArchive(buffer, ZipArchiveMode.Read);
            if (innerZip.GetEntry("manifest.json") is not { } innerManifest)
            {
                return null;
            }

            using var innerStream = innerManifest.Open();
            return JsonSerializer.Deserialize<RecordingManifest>(innerStream, RecordingJsonOptions.Default);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Every (recording, declared layout airport) pair across the committed corpus.</summary>
    private static List<(string Recording, string AirportId)> DeclaredLayoutAirports()
    {
        List<(string, string)> declared = [];
        if (!Directory.Exists(TestDataDir))
        {
            return declared;
        }

        foreach (string zipPath in Directory.EnumerateFiles(TestDataDir, "*.zip").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadManifest(zipPath)?.LayoutAirportIds is not { Count: > 0 } airports)
            {
                continue;
            }

            foreach (string airportId in airports)
            {
                declared.Add((Path.GetFileName(zipPath), airportId));
            }
        }

        return declared;
    }

    [Fact]
    public void RunwayOnlyFixtures_MatchTheCommittedList()
    {
        var shortIds = CommittedGeoJsonShortIds();
        if (shortIds.Count == 0)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        List<string> runwayOnly = [];
        foreach (string shortId in shortIds)
        {
            if (groundData.GetLayout(shortId) is { } layout && !IsTaxiCapable(layout))
            {
                runwayOnly.Add(shortId.ToUpperInvariant());
                output.WriteLine($"runway-only: {shortId} ({layout.Edges.Count} edges, {layout.Nodes.Count} nodes)");
            }
        }

        Assert.Equal(RunwayOnlyFixtures.Order(StringComparer.OrdinalIgnoreCase), runwayOnly.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecordingsDeclaringAGroundLayout_ResolveToATaxiCapableFixture()
    {
        var declared = DeclaredLayoutAirports();
        if (declared.Count == 0)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SortedSet<string> degraded = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string recording, string airportId) in declared)
        {
            var layout = groundData.GetLayout(airportId);
            if (layout is null)
            {
                output.WriteLine($"{recording}: {airportId} has NO committed geojson — replays with GroundLayout == null");
                degraded.Add(NormalizeId(airportId));
            }
            else if (!IsTaxiCapable(layout))
            {
                output.WriteLine($"{recording}: {airportId} is runway-only — ground queries degrade silently");
                degraded.Add(NormalizeId(airportId));
            }
        }

        Assert.Equal(DeclaredButDegraded.Keys.Order(StringComparer.OrdinalIgnoreCase), degraded.Order(StringComparer.OrdinalIgnoreCase));
    }
}
