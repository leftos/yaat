using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="HoldShortReason"/> must survive a snapshot round-trip. Before the fix,
/// <see cref="TaxiRoute.FromSnapshot"/> and <see cref="HoldingShortPhase.FromSnapshot"/>
/// hardcoded the reason on restore (ExplicitHoldShort / RunwayCrossing), so a
/// <see cref="HoldShortReason.DestinationRunway"/> hold-short was silently reclassified on a
/// timeline rewind — breaking the auto-CTO release gate and the CROSS/RES command-acceptance
/// checks that key off DestinationRunway.
/// </summary>
public class HoldShortReasonSnapshotTests
{
    public HoldShortReasonSnapshotTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void HoldingShortPhase_RoundTrip_PreservesDestinationRunwayReason()
    {
        var phase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 5,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = "28L",
            }
        );

        var dto = (HoldingShortPhaseDto)phase.ToSnapshot();
        var restored = HoldingShortPhase.FromSnapshot(dto);

        Assert.Equal(HoldShortReason.DestinationRunway, restored.HoldShort.Reason);
    }

    [Fact]
    public void TaxiRoute_RoundTrip_PreservesDestinationRunwayReason()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var route = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 999,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28L",
                },
            ],
            CurrentSegmentIndex = 0,
        };

        var dto = route.ToSnapshot();
        var restored = TaxiRoute.FromSnapshot(dto, layout);

        Assert.NotNull(restored);
        Assert.Single(restored!.HoldShortPoints);
        Assert.Equal(HoldShortReason.DestinationRunway, restored.HoldShortPoints[0].Reason);
    }

    [Fact]
    public void HoldingShortPhase_LegacySnapshotWithoutReason_FallsBackToRunwayCrossing()
    {
        var dto = new HoldingShortPhaseDto
        {
            Status = 0,
            ElapsedSeconds = 0,
            HoldShortNodeId = 5,
            RunwayId = "28L",
            // Reason omitted — a pre-schema-12 snapshot.
        };

        var restored = HoldingShortPhase.FromSnapshot(dto);

        Assert.Equal(HoldShortReason.RunwayCrossing, restored.HoldShort.Reason);
    }

    [Fact]
    public void TaxiRoute_LegacySnapshotWithoutReason_FallsBackToExplicitHoldShort()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var dto = new TaxiRouteDto
        {
            Segments = [],
            CurrentSegmentIndex = 0,
            HoldShortPoints =
            [
                new HoldShortPointDto
                {
                    NodeId = 999,
                    RunwayId = "28L",
                    IsSatisfied = false,
                },
            ],
            // HoldShortPointDto.Reason omitted — a pre-schema-12 snapshot.
        };

        var restored = TaxiRoute.FromSnapshot(dto, layout);

        Assert.NotNull(restored);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, restored!.HoldShortPoints[0].Reason);
    }
}
