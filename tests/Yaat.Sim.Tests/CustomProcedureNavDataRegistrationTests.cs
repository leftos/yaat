using System.IO.Compression;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT keeps two independent procedure stores: vNAS NavData holds flat SID/STAR fix-name bodies (which
/// drive <see cref="NavigationDatabase.ResolveSidId"/> → <see cref="RouteExpander"/> → route-string parsing
/// and autocomplete), while CIFP holds the typed flyable legs. A custom fragment supplies the CIFP side; for
/// a procedure vNAS does not carry at all, it must also register a NavData-side body or the token would not
/// even parse out of a filed route.
///
/// The registration is deliberately one-directional: it never overwrites a procedure vNAS already publishes.
/// </summary>
public class CustomProcedureNavDataRegistrationTests
{
    public CustomProcedureNavDataRegistrationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SidAbsentFromNavData_BecomesResolvableAndExpandable()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-navdata-").FullName;
        try
        {
            var fx = BuildRenamedNimiFixture(dir);
            if (fx is null)
            {
                return;
            }

            var (cifpPath, artccsDir, navData) = fx.Value;

            // Premise: vNAS has never heard of the renamed procedure, so the token expands as a bare fix.
            var without = new NavigationDatabase(navData, cifpPath, artccsBaseDir: "", supplementaryCifpFilePaths: []);
            Assert.Null(without.ResolveSidId(RenamedSid));
            Assert.Contains(RenamedSid, RouteExpander.Expand($"{RenamedSid} OAK V6 SAC", without, includeAllTransitionsOnMismatch: false));

            var db = new NavigationDatabase(navData, cifpPath, artccsDir, supplementaryCifpFilePaths: []);

            // The fragment registers the NavData-side body, so the id resolves...
            Assert.Equal(RenamedSid, db.ResolveSidId(RenamedSid));

            // ...and RouteExpander treats the token as a SID rather than emitting it as a bare fix.
            var expanded = RouteExpander.Expand($"{RenamedSid} OAK V6 SAC", db, includeAllTransitionsOnMismatch: false);
            Assert.DoesNotContain(RenamedSid, expanded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SidPublishedByNavData_IsNotShadowedByFragment()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-custom-noshadow-").FullName;
        try
        {
            var fx = BuildRenamedNimiFixture(dir);
            if (fx is null)
            {
                return;
            }

            var (cifpPath, _, navData) = fx.Value;

            // A fragment for NIMI5 — which vNAS NavData *does* carry — must leave the vNAS body alone.
            var baseline = new NavigationDatabase(navData, cifpPath, artccsBaseDir: "", supplementaryCifpFilePaths: []);
            var vnasBody = baseline.GetSidBody("NIMI5");

            string artccsDir = Path.Combine(dir, "ARTCCs-nimi5");
            WriteFragment(artccsDir, "ZOA", "koak-nimi.cifp", File.ReadAllLines(cifpPath).Where(IsNimiRecord));

            var db = new NavigationDatabase(navData, cifpPath, artccsDir, supplementaryCifpFilePaths: []);

            Assert.Equal(vnasBody, db.GetSidBody("NIMI5"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string RenamedSid = "ZZTST1";

    private static bool IsNimiRecord(string line) => line.Contains("KOAKK2DNIMI", StringComparison.Ordinal);

    /// <summary>
    /// Builds a CIFP whose KOAK NIMITZ records are renamed to <see cref="RenamedSid"/> — an id vNAS NavData
    /// cannot know — and an ARTCCs tree holding those renamed records as a fragment. The CIFP itself keeps
    /// the original NIMI records too, so <see cref="SidPublishedByNavData_IsNotShadowedByFragment"/> can
    /// reuse the same file. Returns null when test data is unavailable or the bundle lacks NIMITZ.
    /// </summary>
    private static (string CifpPath, string ArtccsDir, NavDataSet NavData)? BuildRenamedNimiFixture(string dir)
    {
        string navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
        string bundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");
        if (!File.Exists(navDataPath) || !File.Exists(bundledGz))
        {
            return null;
        }

        string cifpPath = Path.Combine(dir, "FAACIFP18-2604");
        using (var gz = new GZipStream(File.OpenRead(bundledGz), CompressionMode.Decompress))
        using (var outF = File.Create(cifpPath))
        {
            gz.CopyTo(outF);
        }

        var nimiRecords = File.ReadAllLines(cifpPath).Where(IsNimiRecord).ToList();
        if (nimiRecords.Count == 0)
        {
            return null; // bundle no longer has NIMI — premise not met, skip.
        }

        // Procedure id occupies columns [13..19]; swapping it in place preserves every other field.
        var renamed = nimiRecords.Select(l => l[..13] + RenamedSid.PadRight(6) + l[19..]);

        string artccsDir = Path.Combine(dir, "ARTCCs");
        WriteFragment(artccsDir, "ZOA", "koak-renamed.cifp", renamed);

        var navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));
        return (cifpPath, artccsDir, navData);
    }

    private static void WriteFragment(string artccsDir, string artcc, string fileName, IEnumerable<string> records)
    {
        string categoryDir = Path.Combine(artccsDir, artcc, "Procedures");
        Directory.CreateDirectory(categoryDir);
        File.WriteAllLines(Path.Combine(categoryDir, fileName), records);
    }
}

/// <summary>
/// Guards the CIFP fragments actually committed under <c>Data/ARTCCs/*/Procedures</c>. A fragment whose
/// records the parser rejects would resolve to nothing at runtime and log only a warning — this turns that
/// into a build failure. Mirrors <c>AircraftProfileOverrideTests.OverridesJson_LoadsAndIsSane</c>.
/// </summary>
public class CustomProcedureFragmentsAreValidTests
{
    /// <summary>
    /// The shipped ZOA fragment is the whole point of the feature: KOAK's NIMITZ SID left the FAA CIFP at
    /// cycle 2605 and the prior-cycle chain only recovers it for ~12 months. If this regresses, KOAK RV-SID
    /// departures silently fall back to runway heading again.
    /// </summary>
    [Fact]
    public void ShippedZoaFragment_SuppliesKoakNimitzAt315Degrees()
    {
        string fragment = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs", "ZOA", "Procedures", "koak-nimi.cifp");
        Assert.True(File.Exists(fragment), $"expected the pinned KOAK NIMITZ fragment at {fragment}");

        var sid = Assert.Single(CifpParser.ParseSids(fragment, "KOAK"));
        Assert.Equal("NIMI5", sid.ProcedureId);
        Assert.NotEmpty(sid.RunwayTransitions);

        // Every runway transition climbs on runway heading (CA) then turns to the charted 315 (VM).
        foreach (var (_, transition) in sid.RunwayTransitions)
        {
            var vm = Assert.Single(transition.Legs, l => l.PathTerminator == CifpPathTerminator.VM);
            Assert.NotNull(vm.OutboundCourse);
            Assert.Equal(315.0, vm.OutboundCourse!.Value, 1.0);
            Assert.Contains(transition.Legs, l => l.PathTerminator == CifpPathTerminator.CA);
        }
    }

    [Fact]
    public void EveryCommittedFragment_ParsesToAtLeastOneProcedure()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");
        var result = CustomProcedureLoader.LoadAll(baseDir);

        Assert.DoesNotContain(result.Warnings, w => !w.Contains("not found", StringComparison.OrdinalIgnoreCase));

        foreach (var fragment in result.Fragments)
        {
            foreach (var icao in fragment.AirportIcaos)
            {
                var sids = CifpParser.ParseSids(fragment.FilePath, icao);
                var stars = CifpParser.ParseStars(fragment.FilePath, icao);
                var approaches = CifpParser.ParseApproaches(fragment.FilePath, icao);

                Assert.True(
                    sids.Count + stars.Count + approaches.Count > 0,
                    $"{fragment.FilePath} has {icao} records but parsed to no procedures — the records are malformed."
                );

                foreach (var sid in sids)
                {
                    Assert.False(string.IsNullOrWhiteSpace(sid.ProcedureId), $"{fragment.FilePath}: SID with a blank procedure id");
                    Assert.True(
                        sid.CommonLegs.Count + sid.RunwayTransitions.Count + sid.EnrouteTransitions.Count > 0,
                        $"{fragment.FilePath}: SID {sid.ProcedureId} has no legs"
                    );
                }

                foreach (var star in stars)
                {
                    Assert.False(string.IsNullOrWhiteSpace(star.ProcedureId), $"{fragment.FilePath}: STAR with a blank procedure id");
                    Assert.True(
                        star.CommonLegs.Count + star.RunwayTransitions.Count + star.EnrouteTransitions.Count > 0,
                        $"{fragment.FilePath}: STAR {star.ProcedureId} has no legs"
                    );
                }

                foreach (var approach in approaches)
                {
                    Assert.False(string.IsNullOrWhiteSpace(approach.ApproachId), $"{fragment.FilePath}: approach with a blank id");
                    Assert.True(
                        approach.CommonLegs.Count + approach.Transitions.Count > 0,
                        $"{fragment.FilePath}: approach {approach.ApproachId} has no legs"
                    );
                }
            }
        }
    }
}
