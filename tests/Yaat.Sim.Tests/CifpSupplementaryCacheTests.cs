using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Supplementary CIFP resolution from the local cache: when the current AIRAC cycle's file lacks a
/// procedure (e.g. NIMI dropped from cycle 2605 during the NIMI5 → NIMI6 amendment gap), the newest
/// cached PRIOR cycle is used as the supplementary source so the procedure (and its published heading)
/// can still be resolved. See <see cref="CifpPathResolver.ResolveSupplementaryFromCache(string, string)"/>.
/// </summary>
public class CifpSupplementaryCacheTests
{
    [Fact]
    public void ResolveSupplementaryFromCache_PicksNewestStrictlyPriorCycle()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-cifp-cache-test-").FullName;
        try
        {
            foreach (var name in new[] { "FAACIFP18-2602", "FAACIFP18-2603", "FAACIFP18-2604", "FAACIFP18-2605", "FAACIFP18-bundled.dat" })
            {
                File.WriteAllText(Path.Combine(dir, name), "x");
            }

            // Current cycle 2605: newest strictly-older cached cycle is 2604.
            var supp = CifpPathResolver.ResolveSupplementaryFromCache("2605", dir);
            Assert.NotNull(supp);
            Assert.Equal("FAACIFP18-2604", Path.GetFileName(supp));
            // The bundled (non-cycle) file is never selected.
            Assert.DoesNotContain("bundled", supp);

            // No cached cycle strictly older than the oldest present -> null (a fresh install).
            Assert.Null(CifpPathResolver.ResolveSupplementaryFromCache("2602", dir));

            // Missing cache directory -> null.
            Assert.Null(CifpPathResolver.ResolveSupplementaryFromCache("2605", Path.Combine(dir, "does-not-exist")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
