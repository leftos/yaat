using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// What the <see cref="PrecomputeBuilderTests"/> classes share: the KOAK and KSFO GeoJSON, KOAK parsed once per test
/// process, and the store and key helpers.
/// </summary>
internal static class PrecomputeBuilderFixtures
{
    internal const long NavDataSerial = 12_345;

    /// <summary>The shipped ARTCCs folder both the sidecar catalog and <see cref="AirportSidecarHash.For"/> read.</summary>
    internal static readonly string ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

    internal static readonly Lazy<string> OakGeoJson = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson")));

    internal static readonly Lazy<string> SfoGeoJson = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo.geojson")));

    /// <summary>KOAK parsed as a build parses it, for entries whose push targets the check never reads.</summary>
    internal static readonly Lazy<AirportGroundLayout> OakLayout = new(() =>
        GeoJsonParser.Parse("oak", OakGeoJson.Value, "OAK", new LayoutInputs([], VideoMap: null))
    );

    internal static readonly IReadOnlySet<string> Gate26 = new HashSet<string>(StringComparer.Ordinal) { "26" };

    internal static PrecomputeBuildContext Context(ParallelOptions standLoop) =>
        new(ArtccsDir, PushTargetPlannerTests.Sidecars.Value, DesignGroupEnvelopes.LoadShipped(), NavDataSerial, standLoop);

    internal static string Md5() => GeoJsonMd5.Of(OakGeoJson.Value);

    internal static PrecomputeKey FreshKey() =>
        PrecomputeKey.Current(
            Md5(),
            NavDataSerial,
            AirportSidecarHash.For(ArtccsDir, "OAK"),
            AirportSidecarHash.ForStandLeadIns(ArtccsDir, "OAK"),
            string.Empty,
            standoffInputHash: HoldShortStandoff.InputHash(NavigationDatabase.InstanceOrNull, "OAK")
        );

    internal static PrecomputeOnlineFacts Online(string md5, long navDataSerial) =>
        new(navDataSerial, new Dictionary<string, string?>(StringComparer.Ordinal) { ["OAK"] = md5 });

    internal static void WriteOak(PrecomputeStore store, PrecomputeKey key) => store.Write(new PrecomputeEntry("OAK", key, OakLayout.Value, []));

    /// <summary>The <paramref name="count"/> named parking nodes nearest <paramref name="gateName"/>, nearest first then by node id.</summary>
    internal static IReadOnlySet<string> StandsNear(AirportGroundLayout layout, string gateName, int count)
    {
        GroundNode gate = layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == gateName)).OrderBy(n => n.Id).First();
        return layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .OrderBy(n => GeoMath.DistanceNm(gate.Position, n.Position))
            .ThenBy(n => n.Id)
            .Take(count)
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every file under the store's folder, by path, with its bytes and last write time.</summary>
    internal static Dictionary<string, (byte[] Bytes, DateTime WrittenUtc)> Snapshot(string entryPath) =>
        Directory
            .EnumerateFiles(Path.GetDirectoryName(entryPath)!, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal);

    internal static void WithStore(Action<PrecomputeStore> test) => WithRoot(root => test(new PrecomputeStore(root)));

    internal static void WithRoot(Action<string> test)
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    internal static string NewRoot() => Path.Combine(Path.GetTempPath(), "precompute-builder-" + Guid.NewGuid());

    internal static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
