using System.IO.Compression;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Proto;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// ARTCC-supplied CIFP fragments (<c>Data/ARTCCs/{ARTCC}/Procedures/*.cifp</c>) resolve procedures the
/// current FAA cycle has dropped, independently of what AIRAC cycles a deployment happens to have cached.
/// The prior-cycle chain (<see cref="IssueN513sjNimi6PriorCycleChainTests"/>) recovers KOAK's NIMITZ SID
/// only for ~12 months and only on a machine that cached the right cycle; a committed fragment is permanent
/// and deterministic.
///
/// Resolution order is current FAA cycle → ARTCC custom → cached prior cycles.
/// </summary>
public class CustomProcedureResolutionTests
{
    public CustomProcedureResolutionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void CustomFragment_ResolvesSid_WhenNoCachedCycleHasIt()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-sid-").FullName;
        try
        {
            var fx = BuildFixture(dir, "ZOA");
            if (fx is null)
            {
                return;
            }

            var (noNimiCifp, artccsDir, navData) = fx.Value;

            // Premise: with no supplementary chain and no fragment, the SID is unresolvable.
            var withoutFragment = new NavigationDatabase(navData, noNimiCifp, artccsBaseDir: "", supplementaryCifpFilePaths: []);
            Assert.Null(withoutFragment.GetSid("KOAK", "NIMI6"));

            // The committed fragment resolves it — no cached cycles involved.
            var db = new NavigationDatabase(navData, noNimiCifp, artccsDir, supplementaryCifpFilePaths: []);
            var sid = db.GetSid("KOAK", "NIMI6", out var source);

            Assert.NotNull(sid);
            Assert.NotNull(source);
            Assert.Equal(ProcedureSourceKind.ArtccCustom, source.Kind);
            Assert.Equal("ZOA", source.Label);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CustomFragment_FliesPublishedHeading315_WithNoCachedCycles()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-hdg-").FullName;
        try
        {
            var fx = BuildFixture(dir, "ZOA");
            if (fx is null)
            {
                return;
            }

            var (noNimiCifp, artccsDir, navData) = fx.Value;

            var db = new NavigationDatabase(navData, noNimiCifp, artccsDir, supplementaryCifpFilePaths: []);
            using var _ = NavigationDatabase.ScopedOverride(db);

            var result = DepartureClearanceHandler.ResolveDepartureRoute(new DefaultDeparture(), MakeOakDeparture());

            Assert.NotNull(result);
            Assert.False(result.RvSidHoldRunwayHeading);
            Assert.NotNull(result.DepartureHeadingMagnetic);
            Assert.Equal(315.0, result.DepartureHeadingMagnetic!.Value, 1.0);
            Assert.Equal(ProcedureSourceKind.ArtccCustom, result.Source?.Kind);

            // The instructor advisory names the SID and the supplying ARTCC, not an AIRAC cycle.
            var advisory = DepartureClearanceHandler.ProcedureSourceSidAdvisory(ClearanceType.ClearedForTakeoff, result, MakeOakDeparture());
            Assert.NotNull(advisory);
            Assert.Contains("NIMI6", advisory);
            Assert.Contains("ZOA", advisory);
            Assert.DoesNotContain("prior AIRAC cycle", advisory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CurrentCycle_WinsOverCustomFragment()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-prec-").FullName;
        try
        {
            var fx = BuildFixture(dir, "ZOA");
            if (fx is null)
            {
                return;
            }

            var (_, artccsDir, navData) = fx.Value;
            string fullCifp = Path.Combine(dir, "FAACIFP18-2604");

            // Current cycle still carries NIMITZ: the fragment must not be consulted at all.
            var db = new NavigationDatabase(navData, fullCifp, artccsDir, supplementaryCifpFilePaths: []);
            var sid = db.GetSid("KOAK", "NIMI5", out var source);

            Assert.NotNull(sid);
            Assert.Null(source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CustomFragment_WinsOverSupplementaryChain()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-chain-").FullName;
        try
        {
            var fx = BuildFixture(dir, "ZOA");
            if (fx is null)
            {
                return;
            }

            var (noNimiCifp, artccsDir, navData) = fx.Value;
            string fullCifp = Path.Combine(dir, "FAACIFP18-2604");

            // Both the fragment and a cached prior cycle carry NIMITZ — the fragment is authoritative,
            // so the result does not depend on which cycles this machine happens to have cached.
            var db = new NavigationDatabase(navData, noNimiCifp, artccsDir, supplementaryCifpFilePaths: [fullCifp]);
            var sid = db.GetSid("KOAK", "NIMI6", out var source);

            Assert.NotNull(sid);
            Assert.Equal(ProcedureSourceKind.ArtccCustom, source?.Kind);
            Assert.Equal("ZOA", source?.Label);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TwoArtccsSupplyingTheSameAirport_AreCreditedPerProcedure()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-multi-").FullName;
        try
        {
            var fx = BuildFixture(dir, "ZOA");
            if (fx is null)
            {
                return;
            }

            var (noNimiCifp, artccsDir, navData) = fx.Value;
            string fullCifp = Path.Combine(dir, "FAACIFP18-2604");

            // A second ARTCC contributes a *different* KOAK SID under the same airport bucket. The advisory
            // must credit whichever ARTCC actually supplied the matched procedure, not the first one loaded.
            var lines = File.ReadAllLines(fullCifp);
            var renamed = lines.Where(l => l.Contains("KOAKK2DNIMI", StringComparison.Ordinal)).Select(l => l[..13] + "ZZOTH1" + l[19..]);
            WriteFragment(artccsDir, "ZLA", "koak-other.cifp", renamed);

            var db = new NavigationDatabase(navData, noNimiCifp, artccsDir, supplementaryCifpFilePaths: []);

            db.GetSid("KOAK", "NIMI6", out var nimiSource);
            db.GetSid("KOAK", "ZZOTH1", out var otherSource);

            Assert.Equal("ZOA", nimiSource?.Label);
            Assert.Equal("ZLA", otherSource?.Label);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CustomFragment_ResolvesStar_WhenAbsentFromCurrentCycle()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-star-").FullName;
        try
        {
            string fullCifp = Path.Combine(dir, "FAACIFP18-2604");
            var navData = LoadNavDataOrNull(fullCifp);
            if (navData is null)
            {
                return;
            }

            // Discover a real KOAK STAR, then build a current cycle that drops it and a fragment that keeps it.
            string? starId;
            using (NavigationDatabase.ScopedOverride(new NavigationDatabase(navData, fullCifp, artccsBaseDir: "", supplementaryCifpFilePaths: [])))
            {
                starId = NavigationDatabase.Instance.GetStars("KOAK").FirstOrDefault()?.ProcedureId;
            }

            if (starId is null)
            {
                return;
            }

            string token = "KOAKK2E" + starId; // STAR records: section P, subsection E
            var lines = File.ReadAllLines(fullCifp);

            string noStarCifp = Path.Combine(dir, "FAACIFP18-2606");
            File.WriteAllLines(noStarCifp, lines.Where(l => !l.Contains(token, StringComparison.Ordinal)));

            string artccsDir = Path.Combine(dir, "ARTCCs");
            WriteFragment(artccsDir, "ZOA", "koak-star.cifp", lines.Where(l => l.Contains(token, StringComparison.Ordinal)));

            var db = new NavigationDatabase(navData, noStarCifp, artccsDir, supplementaryCifpFilePaths: []);
            Assert.Null(db.GetStars("KOAK").FirstOrDefault(s => s.ProcedureId.Equals(starId, StringComparison.OrdinalIgnoreCase)));

            var resolved = db.GetStar("KOAK", starId, out var source);
            Assert.NotNull(resolved);
            Assert.Equal(new ProcedureSource(ProcedureSourceKind.ArtccCustom, "ZOA"), source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CustomFragment_ResolvesApproach_WhenAbsentFromCurrentCycle()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-appr-").FullName;
        try
        {
            string fullCifp = Path.Combine(dir, "FAACIFP18-2604");
            var navData = LoadNavDataOrNull(fullCifp);
            if (navData is null)
            {
                return;
            }

            string? approachId;
            using (NavigationDatabase.ScopedOverride(new NavigationDatabase(navData, fullCifp, artccsBaseDir: "", supplementaryCifpFilePaths: [])))
            {
                approachId = NavigationDatabase.Instance.GetApproaches("KOAK").FirstOrDefault()?.ApproachId;
            }

            if (approachId is null)
            {
                return;
            }

            string token = "KOAKK2F" + approachId; // Approach records: section P, subsection F
            var lines = File.ReadAllLines(fullCifp);

            string noApprCifp = Path.Combine(dir, "FAACIFP18-2606");
            File.WriteAllLines(noApprCifp, lines.Where(l => !l.Contains(token, StringComparison.Ordinal)));

            string artccsDir = Path.Combine(dir, "ARTCCs");
            WriteFragment(artccsDir, "ZOA", "koak-approach.cifp", lines.Where(l => l.Contains(token, StringComparison.Ordinal)));

            var db = new NavigationDatabase(navData, noApprCifp, artccsDir, supplementaryCifpFilePaths: []);
            Assert.Null(db.GetApproaches("KOAK").FirstOrDefault(a => a.ApproachId.Equals(approachId, StringComparison.OrdinalIgnoreCase)));

            var resolved = db.GetApproach("KOAK", approachId, out var source);
            Assert.NotNull(resolved);
            Assert.Equal(new ProcedureSource(ProcedureSourceKind.ArtccCustom, "ZOA"), source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Builds a fixture from the bundled CIFP (which still carries NIMITZ): the full cycle at
    /// <c>{dir}/FAACIFP18-2604</c>, a "current cycle" with the KOAK NIMITZ records stripped, and an ARTCCs
    /// tree whose <paramref name="artcc"/> folder holds those stripped records as a custom fragment.
    /// Returns null when test data is unavailable or the bundle no longer has NIMITZ (premise not met).
    /// </summary>
    private static (string NoNimiCifp, string ArtccsDir, NavDataSet NavData)? BuildFixture(string dir, string artcc)
    {
        string fullCifp = Path.Combine(dir, "FAACIFP18-2604");
        var navData = LoadNavDataOrNull(fullCifp);
        if (navData is null)
        {
            return null;
        }

        var lines = File.ReadAllLines(fullCifp);
        if (!lines.Any(l => l.Contains("KOAKK2DNIMI", StringComparison.Ordinal)))
        {
            return null; // bundle no longer has NIMI — premise not met, skip.
        }

        string noNimiCifp = Path.Combine(dir, "FAACIFP18-2606");
        File.WriteAllLines(noNimiCifp, lines.Where(l => !l.Contains("KOAKK2DNIMI", StringComparison.Ordinal)));

        string artccsDir = Path.Combine(dir, "ARTCCs");
        WriteFragment(artccsDir, artcc, "koak-nimi.cifp", lines.Where(l => l.Contains("KOAKK2DNIMI", StringComparison.Ordinal)));

        return (noNimiCifp, artccsDir, navData);
    }

    /// <summary>Decompresses the bundled CIFP to <paramref name="cifpOutPath"/> and parses NavData; null when absent.</summary>
    private static NavDataSet? LoadNavDataOrNull(string cifpOutPath)
    {
        string navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
        string bundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");
        if (!File.Exists(navDataPath) || !File.Exists(bundledGz))
        {
            return null;
        }

        using (var gz = new GZipStream(File.OpenRead(bundledGz), CompressionMode.Decompress))
        using (var outF = File.Create(cifpOutPath))
        {
            gz.CopyTo(outF);
        }

        return NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));
    }

    private static void WriteFragment(string artccsDir, string artcc, string fileName, IEnumerable<string> records)
    {
        string categoryDir = Path.Combine(artccsDir, artcc, "Procedures");
        Directory.CreateDirectory(categoryDir);
        File.WriteAllLines(Path.Combine(categoryDir, fileName), ["# test fixture — provenance header is ignored by the parser", .. records]);
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
