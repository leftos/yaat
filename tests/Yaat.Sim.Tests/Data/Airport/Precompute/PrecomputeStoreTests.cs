using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PrecomputeStore"/> round-trips the real KOAK layout, writes identical bytes for the same entry, and
/// tells a missing file, a corrupt file and an entry without a key, layout or push targets apart.
/// </summary>
public class PrecomputeStoreTests
{
    private const string GeoJsonMd5 = "5d41402abc4b2a76b9719d911017c592";

    private const string SidecarHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public PrecomputeStoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void RoundTrip_RealKoakLayout()
    {
        AirportGroundLayout layout = RealKoakLayout();
        var key = PrecomputeKey.Current(GeoJsonMd5, 12_345, SidecarHash);
        var entry = new PrecomputeEntry(layout.AirportId, key, layout, []);
        string root = NewRoot();

        try
        {
            var store = new PrecomputeStore(root);
            store.Write(entry);

            Assert.True(store.TryRead(layout.AirportId, out PrecomputeEntry? read));
            Assert.NotNull(read);
            Assert.Equal(layout.AirportId, read.AirportId);
            Assert.Equal(key, read.Key);
            Assert.Equal(layout.Nodes.Count, read.Layout.Nodes.Count);
            Assert.Equal(layout.Edges.Count, read.Layout.Edges.Count);
            Assert.Equal(layout.Arcs.Count, read.Layout.Arcs.Count);

            GroundNode connected = layout.Nodes.Values.First(node => node.Edges.Count != 0);
            Assert.Equal(connected.Edges.Count, read.Layout.Nodes[connected.Id].Edges.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// Gate 26's computed push targets read back through <see cref="PrecomputeStore.TryRead"/> serialise to the same JSON
    /// as the ones written (the list fields compare by reference, so the records themselves are not compared).
    /// </summary>
    [Fact]
    public void RoundTrip_ComputedGate26PushTargets()
    {
        AirportGroundLayout layout = RealKoakLayout();
        IReadOnlyList<PushTargetEntry> targets = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            PushTargetPlannerTests.Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "26" }
        );
        Assert.Contains(targets, e => e.Targets.Count > 0);
        var entry = new PrecomputeEntry(layout.AirportId, PrecomputeKey.Current(GeoJsonMd5, 12_345, SidecarHash), layout, targets);
        string root = NewRoot();

        try
        {
            var store = new PrecomputeStore(root);
            store.Write(entry);

            Assert.True(store.TryRead(layout.AirportId, out PrecomputeEntry? read));
            Assert.Equal(PushTargetPlannerTests.Json(targets), PushTargetPlannerTests.Json(read.PushTargets));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// Two independent parses rather than two references to the harness's cached layout, so this also catches bytes
    /// that vary between parses of the same GeoJSON.
    /// </summary>
    [Fact]
    public void Write_IsByteIdentical()
    {
        AirportGroundLayout first = ParseKoakLayout();
        AirportGroundLayout second = ParseKoakLayout();
        var key = PrecomputeKey.Current(GeoJsonMd5, 12_345, SidecarHash);
        string firstRoot = NewRoot();
        string secondRoot = NewRoot();

        try
        {
            var firstStore = new PrecomputeStore(firstRoot);
            var secondStore = new PrecomputeStore(secondRoot);
            firstStore.Write(new PrecomputeEntry(first.AirportId, key, first, []));
            secondStore.Write(new PrecomputeEntry(second.AirportId, key, second, []));

            Assert.Equal(File.ReadAllBytes(firstStore.PathFor(first.AirportId)), File.ReadAllBytes(secondStore.PathFor(second.AirportId)));
        }
        finally
        {
            DeleteRoot(firstRoot);
            DeleteRoot(secondRoot);
        }
    }

    /// <summary>
    /// Both lists hold two targets on one stand, so the second list having A1/I before A1/II on the way in fails the
    /// assertion when the sort drops its design-group tiebreak and a stable sort keeps that input order.
    /// </summary>
    [Fact]
    public void Write_ShuffledTargets_IsByteIdentical()
    {
        AirportGroundLayout layout = RealKoakLayout();
        var key = PrecomputeKey.Current(GeoJsonMd5, 12_345, SidecarHash);
        PushTargetEntry[] targets = [new("A1", "II", []), new("A1", "I", []), new("B2", "III", [])];
        var first = new PrecomputeEntry(layout.AirportId, key, layout, [.. targets]);
        var second = new PrecomputeEntry(layout.AirportId, key, layout, [targets[2], targets[1], targets[0]]);
        string firstRoot = NewRoot();
        string secondRoot = NewRoot();

        try
        {
            var firstStore = new PrecomputeStore(firstRoot);
            var secondStore = new PrecomputeStore(secondRoot);
            firstStore.Write(first);
            secondStore.Write(second);

            Assert.Equal(File.ReadAllBytes(firstStore.PathFor(layout.AirportId)), File.ReadAllBytes(secondStore.PathFor(layout.AirportId)));
        }
        finally
        {
            DeleteRoot(firstRoot);
            DeleteRoot(secondRoot);
        }
    }

    [Fact]
    public void MissingFile_ReturnsFalse()
    {
        string root = NewRoot();

        try
        {
            var store = new PrecomputeStore(root);
            Assert.False(store.TryRead("ZZZZ", out PrecomputeEntry? entry));
            Assert.Null(entry);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CorruptFile_ThrowsNamingPath()
    {
        string root = NewRoot();

        try
        {
            Directory.CreateDirectory(root);
            var store = new PrecomputeStore(root);
            string path = store.PathFor("ZZZZ");
            File.WriteAllText(path, "this is not a brotli stream");

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.TryRead("ZZZZ", out _));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("Key")]
    [InlineData("Layout")]
    [InlineData("PushTargets")]
    public void MissingRequiredMember_ThrowsNamingPath(string dropped)
    {
        AirportGroundLayout layout = RealKoakLayout();
        string root = NewRoot();

        try
        {
            Directory.CreateDirectory(root);
            var store = new PrecomputeStore(root);
            string path = store.PathFor(layout.AirportId);
            var key = PrecomputeKey.Current(GeoJsonMd5, 12_345, SidecarHash);
            var document = new JsonObject
            {
                ["AirportId"] = layout.AirportId,
                ["Key"] = JsonSerializer.SerializeToNode(key, GroundLayoutSerializer.Options),
                ["PushTargets"] = new JsonArray(),
            };
            document.Remove(dropped);

            using (FileStream file = File.Create(path))
            {
                using var brotli = new BrotliStream(file, CompressionLevel.Optimal);
                brotli.Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
            }

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.TryRead(layout.AirportId, out _));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void PathFor_UpperCasesTheAirportId()
    {
        var store = new PrecomputeStore(NewRoot());

        Assert.EndsWith("KOAK.json.br", store.PathFor("koak"));
    }

    [Fact]
    public void Write_AirportIdMismatch_Throws()
    {
        AirportGroundLayout layout = RealKoakLayout();
        var entry = new PrecomputeEntry("KSFO", PrecomputeKey.Current(GeoJsonMd5, 12_345, SidecarHash), layout, []);
        var store = new PrecomputeStore(NewRoot());

        ArgumentException ex = Assert.Throws<ArgumentException>(() => store.Write(entry));
        Assert.Contains("KSFO", ex.Message);
    }

    private static AirportGroundLayout RealKoakLayout()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("KOAK");
        return layout ?? throw new InvalidOperationException("The KOAK test layout (tests/Yaat.Sim.Tests/TestData/oak.geojson) is missing.");
    }

    private static AirportGroundLayout ParseKoakLayout()
    {
        var groundData = new TestAirportGroundData();
        string geoJson = groundData.GetSourceGeoJson("KOAK") ?? throw new InvalidOperationException("The KOAK test GeoJSON is missing.");
        return GeoJsonParser.Parse("OAK", geoJson, "OAK", FilletMode.Standard);
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "precompute-" + Guid.NewGuid());

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
