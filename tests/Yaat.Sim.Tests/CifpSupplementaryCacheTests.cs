using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Supplementary CIFP chain resolution from the local cache: when the current AIRAC cycle's file lacks a
/// procedure (e.g. NIMI dropped from cycle 2605 but still present in 2604/2603/2602), the cached prior
/// cycles within the recency cap are returned newest→oldest so procedure resolution can walk back to the
/// most recent cycle that still carries it. See
/// <see cref="CifpPathResolver.ResolveSupplementaryChainFromCache(string, int, string)"/>.
/// </summary>
public class CifpSupplementaryCacheTests
{
    private static string[] Names(IReadOnlyList<string> paths) => paths.Select(p => Path.GetFileName(p)!).ToArray();

    [Fact]
    public void ResolveSupplementaryChain_ReturnsPriorCyclesNewestFirst_WithinCap()
    {
        var dir = Directory.CreateTempSubdirectory("yaat-cifp-cache-test-").FullName;
        try
        {
            foreach (var name in new[] { "FAACIFP18-2602", "FAACIFP18-2603", "FAACIFP18-2604", "FAACIFP18-2605", "FAACIFP18-bundled.dat" })
            {
                File.WriteAllText(Path.Combine(dir, name), "x");
            }

            // Current cycle 2606, generous cap: all four cached prior cycles, newest→oldest.
            // The bundled (non-cycle) file is never selected by the cache walk.
            var chain = CifpPathResolver.ResolveSupplementaryChainFromCache("2606", 13, dir);
            Assert.Equal(new[] { "FAACIFP18-2605", "FAACIFP18-2604", "FAACIFP18-2603", "FAACIFP18-2602" }, Names(chain));

            // Recency cap of 1: only the immediately-prior cycle (2605); 2604 (age 2) is excluded.
            var capped = CifpPathResolver.ResolveSupplementaryChainFromCache("2606", 1, dir);
            Assert.Equal(new[] { "FAACIFP18-2605" }, Names(capped));

            // No cached cycle strictly older than the oldest present -> empty (a fresh install).
            Assert.Empty(CifpPathResolver.ResolveSupplementaryChainFromCache("2602", 13, dir));

            // Missing cache directory -> empty.
            Assert.Empty(CifpPathResolver.ResolveSupplementaryChainFromCache("2606", 13, Path.Combine(dir, "does-not-exist")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
