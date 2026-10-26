using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerFixtures;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerTests;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PushTargetPlanner.ComputeStands"/> across every core gives the store bytes a sequential run gives.
/// </summary>
public class PushTargetPlannerParallelTests
{
    private const string GeoJsonMd5 = "5d41402abc4b2a76b9719d911017c592";

    public PushTargetPlannerParallelTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The parallel planner writes byte-identical store output to the sequential one: the same targets for every stand,
    /// whatever the degree of parallelism and whatever order the stands finish in. The stand set is the eight named
    /// parking nodes nearest the airport's reference gate, so a run is cheap and yet wide enough for the stands to
    /// overlap. Two parallel runs, because a race need not show on every one.
    /// </summary>
    [Theory]
    [InlineData("KOAK")]
    [InlineData("KSFO")]
    public void Parallel_MatchesSequential_ByteForByte(string airport)
    {
        AirportGroundLayout layout = airport == "KOAK" ? Oak() : Sfo();
        GroundNode reference = Stand(layout, airport == "KOAK" ? "26" : "D5");
        IReadOnlySet<string> stands = NearestStandNames(layout, reference, 8);
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        IReadOnlyList<PushTargetEntry> sequential = PushTargetPlanner.ComputeStands(layout, envelopes, Sidecars.Value, stands, Sequential);
        var key = PrecomputeKey.Current(
            GeoJsonMd5,
            12_345,
            AirportSidecarHash.For(ArtccsDir, airport),
            AirportSidecarHash.ForStandLeadIns(ArtccsDir, airport),
            string.Empty
        );
        string sequentialRoot = NewRoot();
        string[] parallelRoots = [NewRoot(), NewRoot()];

        try
        {
            var sequentialStore = new PrecomputeStore(sequentialRoot);
            sequentialStore.Write(new PrecomputeEntry(layout.AirportId, key, layout, sequential));
            byte[] expected = File.ReadAllBytes(sequentialStore.PathFor(layout.AirportId));

            foreach (string root in parallelRoots)
            {
                IReadOnlyList<PushTargetEntry> parallel = PushTargetPlanner.ComputeStands(
                    layout,
                    envelopes,
                    Sidecars.Value,
                    stands,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }
                );
                var store = new PrecomputeStore(root);
                store.Write(new PrecomputeEntry(layout.AirportId, key, layout, parallel));
                Assert.Equal(expected, File.ReadAllBytes(store.PathFor(layout.AirportId)));
            }
        }
        finally
        {
            DeleteRoot(sequentialRoot);
            foreach (string root in parallelRoots)
            {
                DeleteRoot(root);
            }
        }
    }
}
