using System.Diagnostics;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The seed the Push Back To menu fills from: <see cref="PrecomputeStore.TryReadPushTargets"/> reads the committed
/// <c>OAK.json.br</c>'s push targets without its layout, and <see cref="PushTargetSeed"/> finds the one entry a stand and
/// design group need out of them.
/// </summary>
public class PushTargetSeedTests : IClassFixture<PushTargetSeedFixture>
{
    /// <summary>The shipped cache directory, as the test output has it.</summary>
    internal static readonly string ShippedCacheDir = Path.Combine(AppContext.BaseDirectory, "Data", "PrecomputeCache");

    /// <summary>The shipped ARTCC sidecars the seed's sidecar hash is taken over.</summary>
    internal static readonly string ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

    private readonly ITestOutputHelper _output;

    private readonly PushTargetSeedFixture _seed;

    public PushTargetSeedTests(ITestOutputHelper output, PushTargetSeedFixture seed)
    {
        _output = output;
        _seed = seed;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The push-only read of the committed OAK file gives the whole-entry read's key and push targets, so a client that
    /// has its own layout gets the same seed without paying for a second parse of the stored one.
    /// </summary>
    [Fact]
    public void TryReadPushTargets_Oak_EqualsTryReadsPushTargets()
    {
        var store = new PrecomputeStore(ShippedCacheDir);

        Assert.True(store.TryRead("OAK", out PrecomputeEntry? entry));
        Assert.NotEmpty(entry.PushTargets);
        Assert.True(store.TryReadPushTargets("OAK", out PrecomputeKey? key, out IReadOnlyList<PushTargetEntry>? pushTargets));

        Assert.NotNull(key);
        Assert.NotNull(pushTargets);
        Assert.Equal(entry.Key, key);
        Assert.Equal(PushTargetPlannerTests.Json(entry.PushTargets), PushTargetPlannerTests.Json(pushTargets));
    }

    [Fact]
    public void TryReadPushTargets_MissingFile_ReturnsFalse()
    {
        string root = NewRoot();

        try
        {
            var store = new PrecomputeStore(root);

            Assert.False(store.TryReadPushTargets("ZZZZ", out PrecomputeKey? key, out IReadOnlyList<PushTargetEntry>? pushTargets));
            Assert.Null(key);
            Assert.Null(pushTargets);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void TryReadPushTargets_CorruptFile_ThrowsNamingPath()
    {
        string root = NewRoot();

        try
        {
            Directory.CreateDirectory(root);
            var store = new PrecomputeStore(root);
            string path = store.PathFor("ZZZZ");
            File.WriteAllText(path, "this is not a brotli stream");

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.TryReadPushTargets("ZZZZ", out _, out _));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// One stand and one design group planned on their own are exactly what the stand-by-stand compute stores for that
    /// stand and group. The one-stand time is printed: it is what the client's background fill pays before the menu
    /// shows the live plan.
    /// </summary>
    [Fact]
    public void ComputeStand_OakGate26GroupIII_EqualsComputeStandsForThatGroup()
    {
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        var stopwatch = Stopwatch.StartNew();

        PushTargetEntry? live = PushTargetPlanner.ComputeStand(
            layout,
            envelopes,
            PushTargetPlannerTests.Sidecars.Value,
            "26",
            AirplaneDesignGroup.III
        );
        stopwatch.Stop();
        _output.WriteLine($"OAK gate 26, group III, one stand and one group: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");

        Assert.NotNull(live);
        PushTargetEntry stored = Assert.Single(
            PushTargetPlanner.ComputeStands(
                layout,
                envelopes,
                PushTargetPlannerTests.Sidecars.Value,
                new HashSet<string>(StringComparer.Ordinal) { "26" },
                PushTargetPlannerTests.Sequential
            ),
            e => e.DesignGroup == "III"
        );
        Assert.NotEmpty(stored.Targets);
        Assert.Equal(PushTargetPlannerTests.Json(stored), PushTargetPlannerTests.Json(live));
    }

    [Fact]
    public void ComputeStand_UnknownStand_ReturnsNull()
    {
        PushTargetEntry? entry = PushTargetPlanner.ComputeStand(
            PushTargetPlannerTests.Oak(),
            DesignGroupEnvelopes.LoadShipped(),
            PushTargetPlannerTests.Sidecars.Value,
            "NO-SUCH-STAND",
            AirplaneDesignGroup.III
        );

        Assert.Null(entry);
    }

    [Fact]
    public void GroupForType_B738_IsIII() => Assert.Equal(AirplaneDesignGroup.III, AirplaneDesignGroups.GroupForType("B738"));

    /// <summary>
    /// A type the FAA database has no record for takes its group from its span, which is the category fallback's (a jet
    /// is 118 ft), so the smallest group covering that span is IV.
    /// </summary>
    [Fact]
    public void GroupForType_UnknownType_FallsBackToSpan()
    {
        Assert.Null(FaaAircraftDatabase.Get("ZZZZ"));
        var footprint = AircraftFootprint.FromType("ZZZZ");
        Assert.Equal(118.0, footprint.WingspanFt);
        Assert.Equal(AirplaneDesignGroup.IV, AirplaneDesignGroups.SmallestCoveringSpan(footprint.WingspanFt));
        Assert.Equal(AirplaneDesignGroup.IV, AirplaneDesignGroups.GroupForType("ZZZZ"));
    }

    [Fact]
    public void Find_CorruptFile_ReturnsNull()
    {
        string root = NewRoot();
        var capture = WarningLogCapture.Install();

        try
        {
            Directory.CreateDirectory(root);
            var store = new PrecomputeStore(root);
            File.WriteAllText(store.PathFor("OAK"), "this is not a brotli stream");
            var seed = new PushTargetSeed(store, ArtccsDir);

            Assert.Null(seed.Find("OAK", "26", AirplaneDesignGroup.III));
            (string Message, Exception? Exception) logged = Assert.Single(
                capture.Entries,
                entry => entry.Message.Contains("cannot be read", StringComparison.Ordinal)
            );
            Assert.IsType<InvalidDataException>(logged.Exception);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// A cache file another process holds open is a failure of this call alone: the seed answers null, remembers
    /// nothing, and reads the file again once it is free.
    /// </summary>
    [Fact]
    public void Find_FileLockedThenFreed_RetriesAndFindsTheSeed()
    {
        string root = NewRoot();

        try
        {
            PrecomputeStore store = WriteSeedCopy(root, PrecomputeKey.CurrentOffline(CurrentSidecarHash()));
            var seed = new PushTargetSeed(store, ArtccsDir);

            Assert.Null(FindWhileTheFileIsLocked(seed, store.PathFor("OAK")));
            Assert.Equal("26", seed.Find("OAK", "26", AirplaneDesignGroup.III)?.StandName);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>The seed's answer for gate 26, group III, while <paramref name="path"/> is held with no sharing at all.</summary>
    private static PushTargetEntry? FindWhileTheFileIsLocked(PushTargetSeed seed, string path)
    {
        using FileStream held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return seed.Find("OAK", "26", AirplaneDesignGroup.III);
    }

    /// <summary>KOAK's sidecar hash, as a client over the shipped sidecars computes it.</summary>
    internal static string CurrentSidecarHash() => AirportSidecarHash.For(ArtccsDir, "OAK");

    /// <summary>
    /// The seed finds gate 26's group-III entry out of the committed OAK targets: the entry it hands back is the file's
    /// own, stand and group and every target field alike.
    /// </summary>
    [Fact]
    public void Find_OakGate26GroupIII_ReturnsTheStoredEntry()
    {
        Assert.True(_seed.Store.TryReadPushTargets("OAK", out _, out IReadOnlyList<PushTargetEntry>? stored));
        PushTargetEntry expected = Assert.Single(stored, e => (e.StandName == "26") && (e.DesignGroup == "III"));
        Assert.NotEmpty(expected.Targets);

        PushTargetEntry? found = _seed.Seed.Find("OAK", "26", AirplaneDesignGroup.III);

        Assert.NotNull(found);
        Assert.Equal(expected.StandName, found.StandName);
        Assert.Equal(expected.DesignGroup, found.DesignGroup);
        Assert.Equal(PushTargetPlannerTests.Json(expected.Targets), PushTargetPlannerTests.Json(found.Targets));
    }

    [Fact]
    public void Find_UnknownStand_ReturnsNull() => Assert.Null(_seed.Seed.Find("OAK", "NO-SUCH-STAND", AirplaneDesignGroup.III));

    /// <summary>
    /// An entry whose sidecar hash is not the airport's is not a seed: the stand's geometry may have moved under it, so
    /// the client plans the stand live instead.
    /// </summary>
    [Fact]
    public void Find_StaleSidecarHash_ReturnsNull()
    {
        const string OtherSidecarHash = "0000000000000000000000000000000000000000000000000000000000000000";
        string root = NewRoot();

        try
        {
            PrecomputeStore store = WriteSeedCopy(root, PrecomputeKey.CurrentOffline(OtherSidecarHash));
            var seed = new PushTargetSeed(store, ArtccsDir);

            Assert.Null(seed.Find("OAK", "26", AirplaneDesignGroup.III));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// Writes the committed OAK entry to <paramref name="root"/> under <paramref name="key"/>, keeping its targets.
    /// </summary>
    internal static PrecomputeStore WriteSeedCopy(string root, PrecomputeKey key)
    {
        var shipped = new PrecomputeStore(ShippedCacheDir);
        if (!shipped.TryRead("OAK", out PrecomputeEntry? entry))
        {
            throw new InvalidOperationException($"The committed OAK cache file is missing from {ShippedCacheDir}");
        }

        var store = new PrecomputeStore(root);
        store.Write(entry with { Key = key });
        return store;
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "push-target-seed-" + Guid.NewGuid());

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// A store over a copy of the committed OAK entry keyed the way a client with no vNAS keys it
/// (<see cref="PrecomputeKey.CurrentOffline"/>), so the seed's staleness check passes over targets that were really
/// planned. The committed key is stale between the maintainer's regenerations (the branch regenerates the cache before
/// its pull request merges), so the seed is compared against a key the client can build, never the file's own key.
/// </summary>
public sealed class PushTargetSeedFixture : IDisposable
{
    private readonly string _root;

    public PushTargetSeedFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "push-target-seed-" + Guid.NewGuid());
        string sidecarHash = PushTargetSeedTests.CurrentSidecarHash();
        Store = PushTargetSeedTests.WriteSeedCopy(_root, PrecomputeKey.CurrentOffline(sidecarHash));
        Seed = new PushTargetSeed(Store, PushTargetSeedTests.ArtccsDir);
    }

    /// <summary>The store the copied entry sits in.</summary>
    public PrecomputeStore Store { get; }

    /// <summary>The seed over <see cref="Store"/>.</summary>
    public PushTargetSeed Seed { get; }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
