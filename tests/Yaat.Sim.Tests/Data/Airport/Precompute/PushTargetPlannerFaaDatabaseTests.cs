using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PushTargetPlanner.Compute"/> never reads <see cref="FaaAircraftDatabase"/>. The test sets the database, so it
/// runs alone in the <c>FaaDatabaseMutator</c> collection and puts back exactly the records it found.
/// </summary>
[Collection("FaaDatabaseMutator")]
public class PushTargetPlannerFaaDatabaseTests
{
    private static readonly IReadOnlySet<string> Gate26 = new HashSet<string>(StringComparer.Ordinal) { "26" };

    public PushTargetPlannerFaaDatabaseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Computed once with the database holding the pinned records and once with it empty; a planner that read it would plan
    /// with the empty database's fallback dimensions and differ.
    /// </summary>
    [Fact]
    public void Compute_DoesNotReadFaaAircraftDatabase()
    {
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        var original = new Dictionary<string, FaaAircraftRecord>(FaaAircraftDatabase.Records, StringComparer.OrdinalIgnoreCase);

        try
        {
            FaaAircraftDatabase.Initialize(PushTargetPlannerTests.PinnedRecords());
            IReadOnlyList<PushTargetEntry> withDatabase = PushTargetPlanner.ComputeStands(
                layout,
                envelopes,
                PushTargetPlannerTests.Sidecars.Value,
                Gate26
            );
            FaaAircraftDatabase.Initialize([]);
            Assert.False(FaaAircraftDatabase.IsInitialized);
            IReadOnlyList<PushTargetEntry> withoutDatabase = PushTargetPlanner.ComputeStands(
                layout,
                envelopes,
                PushTargetPlannerTests.Sidecars.Value,
                Gate26
            );

            Assert.Equal(PushTargetPlannerTests.Json(withDatabase), PushTargetPlannerTests.Json(withoutDatabase));
        }
        finally
        {
            FaaAircraftDatabase.Initialize(original);
        }
    }
}
