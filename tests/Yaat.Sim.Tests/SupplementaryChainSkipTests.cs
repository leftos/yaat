using System.IO.Compression;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Tests;

/// <summary>
/// The supplementary-CIFP chain resolves version drift in procedures an airport still publishes
/// (LONGZ3 → LONGZ4, or a recently decoded-out SID like NIMI6). An airport with NO procedures of
/// that kind in the current cycle never had a version to drift from — walking the chain there is a
/// burst of full prior-cycle file scans (observed on the tick thread when route tokens are probed
/// at procedure-less fields like HDN). The chain must be skipped in that case; the version-drift
/// case (other procedures present) is guarded by <see cref="IssueN513sjNimi6PriorCycleChainTests"/>.
/// </summary>
public class SupplementaryChainSkipTests
{
    [Fact]
    public void Sid_ChainSkipped_WhenAirportHasNoSidsInCurrentCycle()
    {
        RunSkipCase(
            stripMarker: "KOAKK2D",
            pickProcedureId: full => CifpParser.ParseSids(full, "KOAK").FirstOrDefault()?.ProcedureId,
            resolve: (db, id) =>
            {
                var match = db.GetSid("KOAK", id, out var source);
                return (match is not null, source);
            }
        );
    }

    [Fact]
    public void Star_ChainSkipped_WhenAirportHasNoStarsInCurrentCycle()
    {
        RunSkipCase(
            stripMarker: "KOAKK2E",
            pickProcedureId: full => CifpParser.ParseStars(full, "KOAK").FirstOrDefault()?.ProcedureId,
            resolve: (db, id) =>
            {
                var match = db.GetStar("KOAK", id, out var source);
                return (match is not null, source);
            }
        );
    }

    [Fact]
    public void Approach_ChainSkipped_WhenAirportHasNoApproachesInCurrentCycle()
    {
        RunSkipCase(
            stripMarker: "KOAKK2F",
            pickProcedureId: full => CifpParser.ParseApproaches(full, "KOAK").FirstOrDefault()?.ApproachId,
            resolve: (db, id) =>
            {
                var match = db.GetApproach("KOAK", id, out var source);
                return (match is not null, source);
            }
        );
    }

    private static void RunSkipCase(
        string stripMarker,
        Func<string, string?> pickProcedureId,
        Func<NavigationDatabase, string, (bool Resolved, ProcedureSource? Source)> resolve
    )
    {
        var navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
        var bundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");
        if (!File.Exists(navDataPath) || !File.Exists(bundledGz))
        {
            return; // offline test-data premise not met
        }

        var dir = Directory.CreateTempSubdirectory("yaat-chain-skip-").FullName;
        try
        {
            var full = Path.Combine(dir, "FAACIFP18-2604");
            using (var gz = new GZipStream(File.OpenRead(bundledGz), CompressionMode.Decompress))
            using (var outF = File.Create(full))
            {
                gz.CopyTo(outF);
            }

            // Premise: the supplementary bundle must actually carry a procedure of this kind at KOAK.
            var procedureId = pickProcedureId(full);
            if (procedureId is null)
            {
                return;
            }

            // Current cycle: KOAK publishes NO procedures of this kind at all.
            var strippedCurrent = Path.Combine(dir, "FAACIFP18-2606");
            File.WriteAllLines(strippedCurrent, File.ReadLines(full).Where(l => !l.Contains(stripMarker)));

            var navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));
            var db = new NavigationDatabase(navData, strippedCurrent, artccsBaseDir: "", supplementaryCifpFilePaths: [full]);

            var (resolved, source) = resolve(db, procedureId);
            Assert.False(resolved, $"{procedureId} must NOT resolve from the supplementary chain when the current cycle lists none of its kind");
            Assert.Null(source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
