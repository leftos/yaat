using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
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

    /// <summary>
    /// A hold's wait for its follow's lead and the call it made for that wait survive a JSON round trip of its snapshot as true,
    /// and are not written at all while null.
    /// </summary>
    [Fact]
    public void HoldingShortPhaseDto_JsonRoundTrip_KeepsTheWaitForLeadAndItsCall()
    {
        var waiting = new HoldingShortPhaseDto
        {
            Status = 0,
            ElapsedSeconds = 0,
            HoldShortNodeId = 5,
            RunwayId = "01R/19L",
            Reason = HoldShortReason.RunwayCrossing,
            WaitingForLead = true,
            SaidWaitForLead = true,
        };

        string json = JsonSerializer.Serialize(waiting, RecordingJsonOptions.Default);
        HoldingShortPhaseDto restored = Assert.IsType<HoldingShortPhaseDto>(
            JsonSerializer.Deserialize<HoldingShortPhaseDto>(json, RecordingJsonOptions.Default)
        );
        Assert.True(restored.WaitingForLead);
        Assert.True(restored.SaidWaitForLead);

        string idleJson = JsonSerializer.Serialize(
            new HoldingShortPhaseDto
            {
                Status = 0,
                ElapsedSeconds = 0,
                HoldShortNodeId = 5,
                RunwayId = "01R/19L",
            },
            RecordingJsonOptions.Default
        );
        Assert.DoesNotContain(nameof(HoldingShortPhaseDto.WaitingForLead), idleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(HoldingShortPhaseDto.SaidWaitForLead), idleJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiRoute_RoundTrip_PreservesDestinationRunwayReason()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
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

        TaxiRouteDto dto = route.ToSnapshot();
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
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
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
