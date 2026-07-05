using System.IO.Compression;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Proto;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Bug (follow-up to <see cref="IssueN513sjNimiRvSidCifpMissTests"/>): N513SJ (C421, KOAK 28R, filed
/// "NIMI6 OAK V6 SAC", bare CTO) held runway heading instead of flying the NIMITZ SID's charted initial
/// right turn to heading 315°. The prior fix degraded an unresolvable RV-SID to "hold runway heading,
/// await vectors"; the instructor expected the published 315° turn.
///
/// Root cause: the NIMITZ SID's coded data is absent from the current FAA CIFP (the procedure is still
/// charted, but missing from the CIFP dataset). The 315° lives only in CIFP (the RW28 transition's VM leg).
/// The supplementary CIFP only consulted the single newest cached prior cycle — which was already the first
/// cycle WITHOUT NIMITZ in its CIFP — even though older cached cycles still carry it.
///
/// Fix: walk the recency-capped chain of cached prior cycles (newest→oldest) and resolve from the most
/// recent cycle that still carries the procedure, recovering NIMI5's 315°. Falls back to the
/// hold-runway-heading degradation only when NO cached cycle has it.
/// </summary>
public class IssueN513sjNimi6PriorCycleChainTests
{
    public IssueN513sjNimi6PriorCycleChainTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Nimi6_ResolvesPublishedHeading315_WhenADeeperCachedCycleHasIt()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-nimi-chain-").FullName;
        try
        {
            var fx = BuildChainFixture(dir);
            if (fx is null)
            {
                return;
            }

            var (noNimiCurrent, noNimiPrior, withNimiOlder, navData) = fx.Value;

            // --- Bug repro: no cached cycle has NIMI -> GetSid null -> degrade to runway heading. ---
            var degradeDb = new NavigationDatabase(navData, noNimiCurrent, supplementaryCifpFilePaths: [noNimiPrior]);
            using (NavigationDatabase.ScopedOverride(degradeDb))
            {
                Assert.Null(degradeDb.GetSid("KOAK", "NIMI6"));
                var degraded = DepartureClearanceHandler.ResolveDepartureRoute(new DefaultDeparture(), MakeOakDeparture());
                Assert.NotNull(degraded);
                Assert.True(degraded.RvSidHoldRunwayHeading);
                Assert.Null(degraded.DepartureHeadingMagnetic);
                Assert.Null(degraded.ResolvedFromCycleId);
            }

            // --- Fix: the chain walks PAST the newest prior (no NIMI) to the older cycle that has it. ---
            var fixedDb = new NavigationDatabase(navData, noNimiCurrent, supplementaryCifpFilePaths: [noNimiPrior, withNimiOlder]);
            using (NavigationDatabase.ScopedOverride(fixedDb))
            {
                var sid = fixedDb.GetSid("KOAK", "NIMI6", out var cycle);
                Assert.NotNull(sid);
                Assert.Equal("2604", cycle); // withNimiOlder is named FAACIFP18-2604

                var result = DepartureClearanceHandler.ResolveDepartureRoute(new DefaultDeparture(), MakeOakDeparture());
                Assert.NotNull(result);
                Assert.False(result.RvSidHoldRunwayHeading);
                Assert.NotNull(result.DepartureHeadingMagnetic);
                Assert.Equal(315.0, result.DepartureHeadingMagnetic!.Value, 1.0);
                // Runway-heading CA leg precedes the VM: defer the turn until the 400 ft AGL gate.
                Assert.True(result.RvSidDeferHeadingUntilMinAlt);
                Assert.Equal("2604", result.ResolvedFromCycleId);

                // The instructor advisory names the SID and the source cycle.
                var advisory = DepartureClearanceHandler.PriorCycleSidAdvisory(ClearanceType.ClearedForTakeoff, result, MakeOakDeparture());
                Assert.NotNull(advisory);
                Assert.Contains("NIMI6", advisory);
                Assert.Contains("2604", advisory);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RecencyCap_DoesNotResurrectProcedureBeyondTheLookbackWindow()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-nimi-cap-").FullName;
        try
        {
            var fx = BuildChainFixture(dir);
            if (fx is null)
            {
                return;
            }

            var (noNimiCurrent, _, withNimiOlder, navData) = fx.Value;

            // withNimiOlder is FAACIFP18-2604; from a current cycle 14+ cycles later it is beyond the
            // ~1-year cap and must NOT be resurrected by the cache walk. Verified at the resolver layer.
            var chain = CifpPathResolver.ResolveSupplementaryChainFromCache("2618", CifpPathResolver.MaxSupplementaryLookbackCycles, dir);
            Assert.DoesNotContain(chain, p => Path.GetFileName(p) == "FAACIFP18-2604");

            // ...but within the window it is included.
            var inWindow = CifpPathResolver.ResolveSupplementaryChainFromCache("2606", CifpPathResolver.MaxSupplementaryLookbackCycles, dir);
            Assert.Contains(inWindow, p => Path.GetFileName(p) == "FAACIFP18-2604");
            _ = (noNimiCurrent, withNimiOlder, navData);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetStar_WalksChain_ResolvesFromDeeperCycle()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-star-chain-").FullName;
        try
        {
            var navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
            var bundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");
            if (!File.Exists(navDataPath) || !File.Exists(bundledGz))
            {
                return;
            }

            string withStar = Path.Combine(dir, "FAACIFP18-2604");
            DecompressTo(bundledGz, withStar);

            // Discover a real KOAK STAR id from the full CIFP, then build a "current cycle" that drops it.
            var navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));
            string? starId;
            using (NavigationDatabase.ScopedOverride(new NavigationDatabase(navData, withStar, supplementaryCifpFilePaths: [])))
            {
                starId = NavigationDatabase.Instance.GetStars("KOAK").FirstOrDefault()?.ProcedureId;
            }

            if (starId is null)
            {
                return;
            }

            string noStarCurrent = Path.Combine(dir, "FAACIFP18-2606");
            string token = "KOAKK2E" + starId; // STAR records: section P, subsection E
            File.WriteAllLines(noStarCurrent, File.ReadAllLines(withStar).Where(l => !l.Contains(token)));

            // Primary lacks the STAR; the supplementary cycle still carries it -> resolves with its cycle id.
            var db = new NavigationDatabase(navData, noStarCurrent, supplementaryCifpFilePaths: [withStar]);
            Assert.Null(db.GetStars("KOAK").FirstOrDefault(s => s.ProcedureId.Equals(starId, StringComparison.OrdinalIgnoreCase)));
            var resolved = db.GetStar("KOAK", starId, out var cycle);
            Assert.NotNull(resolved);
            Assert.Equal("2604", cycle);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Builds three CIFP files from the bundled CIFP (which still carries NIMI5):
    /// two "current/prior" copies with the KOAK NIMITZ records dropped (FAACIFP18-2606 / -2605) and one
    /// older copy that retains them (FAACIFP18-2604). Returns null if test data is unavailable or the
    /// bundle does not actually contain NIMI (premise not met).
    /// </summary>
    private static (string NoNimiCurrent, string NoNimiPrior, string WithNimiOlder, NavDataSet NavData)? BuildChainFixture(string dir)
    {
        var navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
        var bundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");
        if (!File.Exists(navDataPath) || !File.Exists(bundledGz))
        {
            return null;
        }

        string withNimi = Path.Combine(dir, "FAACIFP18-2604");
        DecompressTo(bundledGz, withNimi);

        var lines = File.ReadAllLines(withNimi);
        if (!lines.Any(l => l.Contains("KOAKK2DNIMI")))
        {
            return null; // bundle no longer has NIMI — premise not met, skip.
        }

        var noNimi = lines.Where(l => !l.Contains("KOAKK2DNIMI")).ToArray();
        string noNimiCurrent = Path.Combine(dir, "FAACIFP18-2606");
        string noNimiPrior = Path.Combine(dir, "FAACIFP18-2605");
        File.WriteAllLines(noNimiCurrent, noNimi);
        File.WriteAllLines(noNimiPrior, noNimi);

        var navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));
        // Premise: NavData must still recognize NIMI6 (route expansion / RV-SID detection).
        using var _ = NavigationDatabase.ScopedOverride(new NavigationDatabase(navData, noNimiCurrent, supplementaryCifpFilePaths: []));
        if (NavigationDatabase.Instance.ResolveSidId("NIMI6") is null)
        {
            return null;
        }

        return (noNimiCurrent, noNimiPrior, withNimi, navData);
    }

    private static void DecompressTo(string gzPath, string outPath)
    {
        using var gz = new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress);
        using var outF = File.Create(outPath);
        gz.CopyTo(outF);
    }

    private static AircraftState MakeOakDeparture()
    {
        var ac = new AircraftState
        {
            Callsign = "N513SJ",
            AircraftType = "C421",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(292.0),
            Altitude = 9,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KAUN",
                Route = "NIMI6 OAK V6 SAC",
                Altitude = PlannedAltitude.Ifr(5000),
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 292.0, elevationFt: 9) };
        return ac;
    }
}
