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
/// <see cref="PrecomputeBuilder"/> on the real KOAK GeoJSON: a build writes an entry keyed as computed now, the same build
/// gives the same bytes whatever the parallelism, the check names each listed airport whose entry is missing or stale and
/// which half, without touching a file, and the envelope refresh writes the shipped envelopes and the pinned FAA copy.
/// </summary>
public class PrecomputeBuilderTests
{
    private const long NavDataSerial = 12_345;

    /// <summary>The shipped ARTCCs folder both the sidecar catalog and <see cref="AirportSidecarHash.For"/> read.</summary>
    private static readonly string ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

    private static readonly Lazy<string> OakGeoJson = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson")));

    private static readonly Lazy<string> SfoGeoJson = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo.geojson")));

    /// <summary>KOAK parsed as a build parses it, for entries whose push targets the check never reads.</summary>
    private static readonly Lazy<AirportGroundLayout> OakLayout = new(() => GeoJsonParser.Parse("oak", OakGeoJson.Value, "OAK"));

    private static readonly IReadOnlySet<string> Gate26 = new HashSet<string>(StringComparer.Ordinal) { "26" };

    public PrecomputeBuilderTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Build_RealKoakGeoJson_WritesEntryWhoseKeyIsCurrent()
    {
        string root = NewRoot();
        try
        {
            var store = new PrecomputeStore(root);

            PrecomputeEntry built = PrecomputeEntryBuild.Build(store, "OAK", OakGeoJson.Value, Context(PushTargetPlannerTests.Sequential), Gate26);

            string md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(OakGeoJson.Value))).ToLowerInvariant();
            var current = PrecomputeKey.Current(md5, NavDataSerial, AirportSidecarHash.For(ArtccsDir, "OAK"));
            Assert.Equal(current, built.Key);
            Assert.Equal("OAK", built.AirportId);
            Assert.True(File.Exists(Path.Combine(root, "OAK.json.br")));
            Assert.True(store.TryRead("OAK", out PrecomputeEntry? read));
            Assert.Equal(current, read.Key);
            Assert.True(read.Key.PushTargetsMatch(current));
            Assert.Contains(read.PushTargets, e => (e.StandName == "26") && (e.Targets.Count > 0));
            Assert.Equal(PushTargetPlannerTests.Json(built.PushTargets), PushTargetPlannerTests.Json(read.PushTargets));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>One build sequential, one parallel: the same file, byte for byte.</summary>
    [Fact]
    public void Build_TwiceToTwoRoots_IsByteIdentical()
    {
        IReadOnlySet<string> stands = StandsNear(OakLayout.Value, "26", 3);
        string firstRoot = NewRoot();
        string secondRoot = NewRoot();
        try
        {
            var first = new PrecomputeStore(firstRoot);
            var second = new PrecomputeStore(secondRoot);

            PrecomputeEntryBuild.Build(first, "OAK", OakGeoJson.Value, Context(PushTargetPlannerTests.Sequential), stands);
            PrecomputeEntryBuild.Build(
                second,
                "OAK",
                OakGeoJson.Value,
                Context(new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }),
                stands
            );

            Assert.Equal(File.ReadAllBytes(first.PathFor("OAK")), File.ReadAllBytes(second.PathFor("OAK")));
        }
        finally
        {
            DeleteRoot(firstRoot);
            DeleteRoot(secondRoot);
        }
    }

    /// <summary>
    /// Two airports built one after another with one task, and together with their stands planned across every core on
    /// one shared scheduler: the same two files, byte for byte.
    /// </summary>
    [Fact]
    public async Task BuildAcrossAirports_InParallel_MatchesSequential_ByteForByte()
    {
        Dictionary<string, (string GeoJson, IReadOnlySet<string> Stands)> airports = new(StringComparer.Ordinal)
        {
            ["OAK"] = (OakGeoJson.Value, StandsNear(OakLayout.Value, "26", 3)),
            ["SFO"] = (SfoGeoJson.Value, StandsNear(GeoJsonParser.Parse("sfo", SfoGeoJson.Value, "SFO"), "D2", 3)),
        };
        string sequentialRoot = NewRoot();
        string parallelRoot = NewRoot();
        try
        {
            var sequential = new PrecomputeStore(sequentialRoot);
            var parallel = new PrecomputeStore(parallelRoot);

            await BuildAll(sequential, new PrecomputeRunLimits(1, 1));
            await BuildAll(parallel, new PrecomputeRunLimits(Math.Max(2, Environment.ProcessorCount), 2));

            Assert.All(airports.Keys, faa => Assert.True(File.Exists(sequential.PathFor(faa)), $"no sequential entry for {faa}"));
            Assert.All(airports.Keys, faa => Assert.Equal(File.ReadAllBytes(sequential.PathFor(faa)), File.ReadAllBytes(parallel.PathFor(faa))));
        }
        finally
        {
            DeleteRoot(sequentialRoot);
            DeleteRoot(parallelRoot);
        }

        Task BuildAll(PrecomputeStore store, PrecomputeRunLimits limits) =>
            limits.ForEachAirportAsync(
                airports.Keys,
                async (faa, _) =>
                    await limits.RunAsync(() =>
                        PrecomputeEntryBuild.Build(store, faa, airports[faa].GeoJson, Context(limits.StandLoop), airports[faa].Stands)
                    )
            );
    }

    /// <summary>
    /// Twice as many airports as may be in flight, each running the planner's stand loop over 64 stands through one
    /// <see cref="PrecomputeRunLimits"/> as the tool does (the loop a task on <see cref="PrecomputeRunLimits.RunAsync{T}"/>,
    /// given <see cref="PrecomputeRunLimits.StandLoop"/>): exactly the cap of loop bodies run at once across every airport,
    /// and never more airports than the limit.
    /// </summary>
    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 2)]
    public async Task RunLimits_CapConcurrencyAcrossComputes(int maxDegreeOfParallelism, int airportsInFlight)
    {
        var limits = new PrecomputeRunLimits(maxDegreeOfParallelism, airportsInFlight);
        int running = 0;
        int peak = 0;
        int airportsRunning = 0;
        int airportsPeak = 0;
        int[] stands = [.. Enumerable.Range(0, 64)];

        await limits.ForEachAirportAsync(
            Enumerable.Range(0, airportsInFlight * 2).Select(i => $"A{i}"),
            async (_, _) =>
            {
                RaiseTo(ref airportsPeak, Interlocked.Increment(ref airportsRunning));
                await limits.RunAsync(() => PushTargetPlanner.PlanInParallel(stands, limits.StandLoop, PlanStand));
                Interlocked.Decrement(ref airportsRunning);
            }
        );

        Assert.Equal(maxDegreeOfParallelism, peak);
        Assert.InRange(airportsPeak, 1, airportsInFlight);

        int PlanStand(int stand)
        {
            RaiseTo(ref peak, Interlocked.Increment(ref running));
            // Holds each body until the cap is reached once, so reaching it does not depend on how fast the pool starts workers.
            SpinWait.SpinUntil(() => Volatile.Read(ref peak) >= maxDegreeOfParallelism, TimeSpan.FromSeconds(5));
            Thread.Sleep(3);
            Interlocked.Decrement(ref running);
            return stand;
        }

        static void RaiseTo(ref int target, int value)
        {
            int seen = Volatile.Read(ref target);
            while (value > seen)
            {
                int previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }

    [Fact]
    public void Check_FreshEntry_Passes()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey());

            PrecomputeCheckResult offline = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, null));
            PrecomputeCheckResult online = Assert.Single(PrecomputeBuilder.Check(store, ["KOAK"], ArtccsDir, Online(Md5(), NavDataSerial)));

            Assert.Equal((PrecomputeCheckStatus.Current, 0), (offline.Status, offline.Reasons.Count));
            Assert.Equal((PrecomputeCheckStatus.Current, 0), (online.Status, online.Reasons.Count));
            Assert.Equal("OAK", online.AirportId);
        });
    }

    [Fact]
    public void Check_ChangedGeoJsonMd5_FailsOnlyOnline()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey());

            PrecomputeCheckResult offline = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, null));
            PrecomputeCheckResult online = Assert.Single(
                PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, Online(new string('0', 32), NavDataSerial))
            );

            Assert.Equal(PrecomputeCheckStatus.Current, offline.Status);
            Assert.Equal(PrecomputeCheckStatus.LayoutStale, online.Status);
            Assert.StartsWith("OAK: layout stale", online.Describe(), StringComparison.Ordinal);
            Assert.Contains("GeoJSON MD5", online.Describe(), StringComparison.Ordinal);
        });
    }

    /// <summary>Online, an airport vNAS no longer has a map for is layout-stale, and the reason says so.</summary>
    [Fact]
    public void Check_OnlineWithNoVnasMap_IsLayoutStale()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey());
            var online = new PrecomputeOnlineFacts(NavDataSerial, new Dictionary<string, string?>(StringComparer.Ordinal) { ["OAK"] = null });

            PrecomputeCheckResult result = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, online));

            Assert.Equal(PrecomputeCheckStatus.LayoutStale, result.Status);
            Assert.Contains("vNAS has no ground map for it now", result.Describe(), StringComparison.Ordinal);
        });
    }

    /// <summary>An entry file that does not decode, and one holding another airport's entry, are both unreadable.</summary>
    [Fact]
    public void Check_UnreadableEntry_IsUnreadable()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey());
            File.Move(store.PathFor("OAK"), store.PathFor("SJC"));
            File.WriteAllBytes(store.PathFor("OAK"), [1, 2, 3]);

            IReadOnlyList<PrecomputeCheckResult> results = PrecomputeBuilder.Check(store, ["OAK", "SJC"], ArtccsDir, null);

            Assert.Equal([PrecomputeCheckStatus.Unreadable, PrecomputeCheckStatus.Unreadable], results.Select(r => r.Status));
            Assert.StartsWith("OAK: unreadable", results[0].Describe(), StringComparison.Ordinal);
            Assert.Contains("holds the entry of OAK, not SJC", results[1].Describe(), StringComparison.Ordinal);
        });
    }

    /// <summary>A sidecar edit stales the push half alone, offline and online: the layout does not read sidecars.</summary>
    [Fact]
    public void Check_ChangedSidecar_FailsPushHalfOnly()
    {
        string artccs = NewRoot();
        try
        {
            string sidecar = Path.Combine(artccs, "ZOA", "Airports", "oak.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.Copy(Path.Combine(ArtccsDir, "ZOA", "Airports", "oak.json"), sidecar);
            WithStore(store =>
            {
                WriteOak(store, PrecomputeKey.Current(Md5(), NavDataSerial, AirportSidecarHash.For(artccs, "OAK")));
                Assert.Equal(PrecomputeCheckStatus.Current, Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], artccs, null)).Status);

                File.AppendAllText(sidecar, "\n");

                PrecomputeCheckResult offline = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], artccs, null));
                PrecomputeCheckResult online = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], artccs, Online(Md5(), NavDataSerial)));
                Assert.Equal(PrecomputeCheckStatus.PushTargetsStale, offline.Status);
                Assert.Equal(PrecomputeCheckStatus.PushTargetsStale, online.Status);
                Assert.StartsWith("OAK: push targets stale", offline.Describe(), StringComparison.Ordinal);
                Assert.Contains("sidecar", offline.Describe(), StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteRoot(artccs);
        }
    }

    [Fact]
    public void Check_ChangedNavDataSerial_FailsOnlyOnline()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey());

            PrecomputeCheckResult offline = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, null));
            PrecomputeCheckResult online = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, Online(Md5(), NavDataSerial + 1)));

            Assert.Equal(PrecomputeCheckStatus.Current, offline.Status);
            Assert.Equal(PrecomputeCheckStatus.LayoutStale, online.Status);
            Assert.Contains("NavData serial", online.Describe(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Check_MissingFileForListedAirport_Fails()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey());

            IReadOnlyList<PrecomputeCheckResult> results = PrecomputeBuilder.Check(store, ["OAK", "KSFO"], ArtccsDir, null);

            Assert.Equal(["OAK", "SFO"], results.Select(r => r.AirportId));
            Assert.Equal(PrecomputeCheckStatus.Current, results[0].Status);
            Assert.Equal(PrecomputeCheckStatus.Missing, results[1].Status);
            Assert.StartsWith("SFO: missing", results[1].Describe(), StringComparison.Ordinal);
            Assert.Contains(store.PathFor("SFO"), results[1].Describe(), StringComparison.Ordinal);
        });
    }

    /// <summary>An entry written under another layout source hash is stale offline: a layout source changed since it was computed.</summary>
    [Fact]
    public void Check_EntryWithStaleSourceHash_Fails()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey() with { LayoutSourceHash = "doctored" });

            PrecomputeCheckResult result = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, null));

            Assert.Equal(PrecomputeCheckStatus.LayoutStale, result.Status);
            Assert.Contains("layout source hash", result.Describe(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Check_DoesNotRewriteAnyFile()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey() with { PushTargetSourceHash = "doctored" });
            Dictionary<string, (byte[] Bytes, DateTime WrittenUtc)> before = Snapshot(store.PathFor("OAK"));

            Assert.NotEmpty(PrecomputeBuilder.Check(store, ["OAK", "SFO"], ArtccsDir, null));
            Assert.NotEmpty(PrecomputeBuilder.Check(store, ["OAK", "SFO"], ArtccsDir, Online(new string('0', 32), NavDataSerial + 1)));

            Dictionary<string, (byte[] Bytes, DateTime WrittenUtc)> after = Snapshot(store.PathFor("OAK"));
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            Assert.All(before, file => Assert.Equal(file.Value.Bytes, after[file.Key].Bytes));
            Assert.All(before, file => Assert.Equal(file.Value.WrittenUtc, after[file.Key].WrittenUtc));
        });
    }

    /// <summary>
    /// The refresh over the pinned FAA copy gives the shipped envelopes byte for byte, and writes the pinned copy back with
    /// the same records (the committed copy's float spelling, <c>92.0</c>, is not System.Text.Json's, so not the same bytes).
    /// </summary>
    [Fact]
    public void RefreshEnvelopes_FromPinnedFaaCopy_WritesTheShippedFile()
    {
        WithRoot(root =>
        {
            string envelopesPath = Path.Combine(root, "design-group-envelopes.json");
            string pinnedPath = Path.Combine(root, "FaaAcd.json");

            PrecomputeBuilder.RefreshEnvelopes(PushTargetPlannerTests.PinnedRecords(), envelopesPath, pinnedPath, out _);

            string shippedPath = Path.Combine(
                TickRecorder.FindRepoRoot(),
                "src",
                "Yaat.Sim",
                "Data",
                "PrecomputeCache",
                "design-group-envelopes.json"
            );
            Assert.Equal(File.ReadAllBytes(shippedPath), File.ReadAllBytes(envelopesPath));
            string pinned = File.ReadAllText(pinnedPath);
            Assert.EndsWith("}\n", pinned, StringComparison.Ordinal);
            Assert.Equal(PushTargetPlannerTests.PinnedRecords(), JsonSerializer.Deserialize<Dictionary<string, FaaAircraftRecord>>(pinned));
        });
    }

    [Fact]
    public void RefreshEnvelopes_EmptyFaaData_RefusesAndLeavesTheFile()
    {
        WithRoot(root =>
        {
            string envelopesPath = Path.Combine(root, "design-group-envelopes.json");
            string pinnedPath = Path.Combine(root, "FaaAcd.json");
            File.WriteAllText(envelopesPath, "envelopes before");
            File.WriteAllText(pinnedPath, "pinned before");

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                PrecomputeBuilder.RefreshEnvelopes(new Dictionary<string, FaaAircraftRecord>(), envelopesPath, pinnedPath, out _)
            );

            Assert.Contains("No FAA aircraft records", ex.Message, StringComparison.Ordinal);
            Assert.Equal("envelopes before", File.ReadAllText(envelopesPath));
            Assert.Equal("pinned before", File.ReadAllText(pinnedPath));
        });
    }

    private static PrecomputeBuildContext Context(ParallelOptions standLoop) =>
        new(ArtccsDir, PushTargetPlannerTests.Sidecars.Value, DesignGroupEnvelopes.LoadShipped(), NavDataSerial, standLoop);

    private static string Md5() => GeoJsonMd5.Of(OakGeoJson.Value);

    private static PrecomputeKey FreshKey() => PrecomputeKey.Current(Md5(), NavDataSerial, AirportSidecarHash.For(ArtccsDir, "OAK"));

    private static PrecomputeOnlineFacts Online(string md5, long navDataSerial) =>
        new(navDataSerial, new Dictionary<string, string?>(StringComparer.Ordinal) { ["OAK"] = md5 });

    private static void WriteOak(PrecomputeStore store, PrecomputeKey key) => store.Write(new PrecomputeEntry("OAK", key, OakLayout.Value, []));

    /// <summary>The <paramref name="count"/> named parking nodes nearest <paramref name="gateName"/>, nearest first then by node id.</summary>
    private static IReadOnlySet<string> StandsNear(AirportGroundLayout layout, string gateName, int count)
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
    private static Dictionary<string, (byte[] Bytes, DateTime WrittenUtc)> Snapshot(string entryPath) =>
        Directory
            .EnumerateFiles(Path.GetDirectoryName(entryPath)!, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal);

    private static void WithStore(Action<PrecomputeStore> test) => WithRoot(root => test(new PrecomputeStore(root)));

    private static void WithRoot(Action<string> test)
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

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "precompute-builder-" + Guid.NewGuid());

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
