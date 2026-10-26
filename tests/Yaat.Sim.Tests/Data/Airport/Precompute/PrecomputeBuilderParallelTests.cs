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
/// Builds of two airports, one after another and in parallel, write the same files byte for byte.
/// </summary>
public class PrecomputeBuilderParallelTests
{
    public PrecomputeBuilderParallelTests()
    {
        TestVnasData.EnsureInitialized();
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
            ["SFO"] = (
                SfoGeoJson.Value,
                StandsNear(GeoJsonParser.Parse("sfo", SfoGeoJson.Value, "SFO", new LayoutInputs([], VideoMap: null)), "D2", 3)
            ),
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
}
