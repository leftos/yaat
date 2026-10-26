using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PrecomputeBuilderFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PrecomputeBuilder"/> on the real KOAK GeoJSON: a build writes an entry keyed as computed now, the same build
/// gives the same bytes whatever the parallelism, the check names each listed airport whose entry is missing or stale and
/// which half, without touching a file, and the envelope refresh writes the shipped envelopes and the pinned FAA copy.
/// </summary>
public class PrecomputeBuilderTests
{
    public PrecomputeBuilderTests()
    {
        TestVnasData.EnsureInitialized();
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

    /// <summary>
    /// A sidecar edit outside the stand lead-ins stales the push half alone, offline and online: the layout build reads only
    /// the lead-ins.
    /// </summary>
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
                WriteOak(
                    store,
                    PrecomputeKey.Current(
                        Md5(),
                        NavDataSerial,
                        AirportSidecarHash.For(artccs, "OAK"),
                        AirportSidecarHash.ForStandLeadIns(artccs, "OAK"),
                        string.Empty
                    )
                );
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

    /// <summary>A stand lead-in edit stales the layout half, offline and online: the layout build reads the lead-ins.</summary>
    [Fact]
    public void Check_ChangedStandLeadIn_FailsLayoutHalf()
    {
        string artccs = NewRoot();
        try
        {
            string sidecar = Path.Combine(artccs, "ZOA", "Airports", "oak.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.Copy(Path.Combine(ArtccsDir, "ZOA", "Airports", "oak.json"), sidecar);
            WithStore(store =>
            {
                WriteOak(
                    store,
                    PrecomputeKey.Current(
                        Md5(),
                        NavDataSerial,
                        AirportSidecarHash.For(artccs, "OAK"),
                        AirportSidecarHash.ForStandLeadIns(artccs, "OAK"),
                        string.Empty
                    )
                );
                Assert.Equal(PrecomputeCheckStatus.Current, Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], artccs, null)).Status);

                string leadIn = """{ "standLeadIns": [{ "stand": "26", "taxiway": "W", "point": [-122.2161, 37.7176] }], """;
                File.WriteAllText(sidecar, leadIn + File.ReadAllText(sidecar).TrimStart()[1..]);

                PrecomputeCheckResult offline = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], artccs, null));
                PrecomputeCheckResult online = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], artccs, Online(Md5(), NavDataSerial)));
                Assert.Equal(PrecomputeCheckStatus.LayoutStale, offline.Status);
                Assert.Equal(PrecomputeCheckStatus.LayoutStale, online.Status);
                Assert.Contains("stand lead-in hash", offline.Describe(), StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteRoot(artccs);
        }
    }

    /// <summary>The check has no videomap to hash, so an entry written with a map's hash reports layout-stale either way.</summary>
    [Fact]
    public void Check_EntryWithAVideoMapHash_ReportsVideoMapStale()
    {
        WithStore(store =>
        {
            WriteOak(store, FreshKey() with { VideoMapHash = "abc" });

            PrecomputeCheckResult offline = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, null));
            PrecomputeCheckResult online = Assert.Single(PrecomputeBuilder.Check(store, ["OAK"], ArtccsDir, Online(Md5(), NavDataSerial)));

            Assert.Equal(PrecomputeCheckStatus.LayoutStale, offline.Status);
            Assert.Contains("the videomap hash differs", offline.Describe(), StringComparison.Ordinal);
            Assert.Equal(PrecomputeCheckStatus.LayoutStale, online.Status);
        });
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
}
