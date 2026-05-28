using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.PathfinderGrid;

/// <summary>
/// Smoke stress grid: compares v1 and v2 pathfinders on a representative set
/// of SFO (San Francisco) origin-destination pairs. Since v2 currently delegates
/// to v1, every comparison returns a zero diff. Once v2 is replaced with a
/// cleanroom implementation, these tests will show real diffs.
///
/// <para>
/// Pairs cover diverse SFO terminal regions: Terminal A (international), Terminal B
/// (domestic), International Terminal F, east cargo, and cross-terminal routes.
/// Both departure and arrival directions are represented.
/// </para>
/// </summary>
[Trait("Category", "PathfinderGrid")]
public class SfoStressGridTests(ITestOutputHelper output)
{
    private static readonly ITaxiPathfinder V1 = new TaxiPathfinderV1Adapter();
    private static readonly ITaxiPathfinder V2 = new TaxiPathfinderV2();

    public static IEnumerable<object[]> Pairs()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            yield break;
        }

        foreach (var pair in SmokeInput(layout))
        {
            yield return new object[] { pair.Label, pair.FromNodeId, pair.ToNodeId };
        }
    }

    private static IEnumerable<(string Label, int FromNodeId, int ToNodeId)> SmokeInput(AirportGroundLayout layout)
    {
        int? Parking(string name)
        {
            var node = layout.FindParkingByName(name);
            return node?.Id;
        }

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

        var a12 = Parking("A12");
        var b5 = Parking("B5");
        var f5 = Parking("F5");

        var rwy1l = Departure("1L");
        var rwy28r = Departure("28R");

        // Terminal A → runway 1L (west-flow departure)
        if ((a12 is not null) && (rwy1l is not null))
        {
            yield return ("SFO A12→1L", a12.Value, rwy1l.Value);
        }

        // Terminal B → runway 1L
        if ((b5 is not null) && (rwy1l is not null))
        {
            yield return ("SFO B5→1L", b5.Value, rwy1l.Value);
        }

        // International Terminal F → runway 28R (east-flow departure)
        if ((f5 is not null) && (rwy28r is not null))
        {
            yield return ("SFO F5→28R", f5.Value, rwy28r.Value);
        }

        // Terminal A → runway 28R (cross-field, full length)
        if ((a12 is not null) && (rwy28r is not null))
        {
            yield return ("SFO A12→28R cross-field", a12.Value, rwy28r.Value);
        }

        // Cross-terminal parking: A12 → F5
        if ((a12 is not null) && (f5 is not null))
        {
            yield return ("SFO A12→F5 cross-terminal", a12.Value, f5.Value);
        }

        // Reverse cross-terminal: F5 → A12
        if ((f5 is not null) && (a12 is not null))
        {
            yield return ("SFO F5→A12 cross-terminal", f5.Value, a12.Value);
        }

        // B5 → F5 (domestic to international)
        if ((b5 is not null) && (f5 is not null))
        {
            yield return ("SFO B5→F5 domestic-to-intl", b5.Value, f5.Value);
        }

        // F5 → B5 (international to domestic)
        if ((f5 is not null) && (b5 is not null))
        {
            yield return ("SFO F5→B5 intl-to-domestic", f5.Value, b5.Value);
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Compare_V1_V2_ExpectZeroDiff(string label, int fromNodeId, int toNodeId)
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            output.WriteLine($"SKIP {label}: SFO layout not available");
            return;
        }

        var result = PathfinderComparison.Compare(V1, V2, layout, fromNodeId, toNodeId);
        output.WriteLine($"{label}: {PathfinderComparison.FormatReport(result)}");

        Assert.True(
            result.BothSucceeded || result.BothFailed,
            $"{label}: V1 and V2 disagree on success (V1FailReason={result.V1FailReason}, V2FailReason={result.V2FailReason})"
        );
        Assert.True(result.SameRoute, $"{label}: V1 and V2 returned different routes");
        Assert.Equal(result.V1SegmentCount, result.V2SegmentCount);
        Assert.Equal(result.V1UTurnCount, result.V2UTurnCount);
    }
}
