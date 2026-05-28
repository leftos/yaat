using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.PathfinderGrid;

/// <summary>
/// Smoke stress grid: compares v1 and v2 pathfinders on a representative set
/// of OAK (Oakland) origin-destination pairs. Since v2 currently delegates to
/// v1, every comparison returns a zero diff. Once v2 is replaced with a
/// cleanroom implementation, these tests will show real diffs.
///
/// <para>
/// Pairs are chosen to cover diverse airport regions: north GA ramp (SIG1,
/// GA3), south commercial gates (Gate 4, Gate 22), east cargo (FDX5), and
/// cross-field routes that require a runway crossing. Both piston and jet
/// categories are represented.
/// </para>
/// </summary>
[Trait("Category", "PathfinderGrid")]
public class OakStressGridTests(ITestOutputHelper output)
{
    private static readonly ITaxiPathfinder V1 = new TaxiPathfinderV1Adapter();
    private static readonly ITaxiPathfinder V2 = new TaxiPathfinderV2();

    public static IEnumerable<object[]> Pairs()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            yield break;
        }

        // Pairs drawn from TaxiCoverageData.OakSmoke — same nodes, different purpose:
        // here we only compare pathfinder output, not simulate the full taxi run.
        foreach (var pair in SmokeInput(layout))
        {
            yield return new object[] { pair.Label, pair.FromNodeId, pair.ToNodeId };
        }
    }

    private static IEnumerable<(string Label, int FromNodeId, int ToNodeId)> SmokeInput(AirportGroundLayout layout)
    {
        // Helper: resolve parking node id by name, skip when absent
        int? Parking(string name)
        {
            var node = layout.FindParkingByName(name);
            return node?.Id;
        }

        // Helper: resolve the full-length lineup hold-short for a departure runway
        int? Departure(string runway)
        {
            var holdShorts = layout.GetRunwayHoldShortNodes(runway);
            if (holdShorts.Count == 0)
            {
                return null;
            }
            var hs = TaxiPathfinder.FindFullLengthLineupHoldShort(layout, holdShorts[0], runway, holdShorts);
            return hs.Id;
        }

        var sig1 = Parking("SIG1");
        var ga3 = Parking("GA3");
        var sig4 = Parking("SIG4");
        var kai1 = Parking("KAI1");
        var gate4 = Parking("4");
        var gate22 = Parking("22");
        var fdx5 = Parking("FDX5");

        var rwy28r = Departure("28R");
        var rwy28l = Departure("28L");
        var rwy30 = Departure("30");

        // North GA → north runway
        if ((sig1 is not null) && (rwy28r is not null))
        {
            yield return ("OAK SIG1→28R", sig1.Value, rwy28r.Value);
        }
        if ((ga3 is not null) && (rwy28r is not null))
        {
            yield return ("OAK GA3→28R", ga3.Value, rwy28r.Value);
        }
        if ((sig4 is not null) && (rwy28r is not null))
        {
            yield return ("OAK SIG4→28R", sig4.Value, rwy28r.Value);
        }
        if ((kai1 is not null) && (rwy28r is not null))
        {
            yield return ("OAK KAI1→28R", kai1.Value, rwy28r.Value);
        }

        // South commercial / cargo → south runway
        if ((gate4 is not null) && (rwy30 is not null))
        {
            yield return ("OAK Gate4→30", gate4.Value, rwy30.Value);
        }
        if ((gate22 is not null) && (rwy30 is not null))
        {
            yield return ("OAK Gate22→30", gate22.Value, rwy30.Value);
        }
        if ((fdx5 is not null) && (rwy30 is not null))
        {
            yield return ("OAK FDX5→30", fdx5.Value, rwy30.Value);
        }

        // Cross-field: north GA → south gate (requires runway crossing)
        if ((sig1 is not null) && (gate4 is not null))
        {
            yield return ("OAK SIG1→Gate4 cross-field", sig1.Value, gate4.Value);
        }
        if ((gate22 is not null) && (sig1 is not null))
        {
            yield return ("OAK Gate22→SIG1 cross-field", gate22.Value, sig1.Value);
        }

        // South cargo → south commercial
        if ((fdx5 is not null) && (gate22 is not null))
        {
            yield return ("OAK FDX5→Gate22", fdx5.Value, gate22.Value);
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Compare_V1_V2_ExpectZeroDiff(string label, int fromNodeId, int toNodeId)
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            output.WriteLine($"SKIP {label}: OAK layout not available");
            return;
        }

        var result = PathfinderComparison.Compare(V1, V2, layout, fromNodeId, toNodeId);
        output.WriteLine($"{label}: {PathfinderComparison.FormatReport(result)}");

        // While v2 delegates to v1, every pair must agree on success/failure and route.
        Assert.True(
            result.BothSucceeded || result.BothFailed,
            $"{label}: V1 and V2 disagree on success (V1FailReason={result.V1FailReason}, V2FailReason={result.V2FailReason})"
        );
        Assert.True(result.SameRoute, $"{label}: V1 and V2 returned different routes");
        Assert.Equal(result.V1SegmentCount, result.V2SegmentCount);
        Assert.Equal(result.V1UTurnCount, result.V2UTurnCount);
    }
}
