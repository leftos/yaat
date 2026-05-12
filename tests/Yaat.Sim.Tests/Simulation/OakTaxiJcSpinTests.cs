using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the OAK post-landing taxi-out spin bug from the S2-OAK-3
/// VFR Sequencing bundle.
///
/// N70CS (C25C jet) lands 28L at t=800, exits via J at HS 498, holds at t=865.
/// User issues <c>TAXI J C HS 28R</c> at t=879. The aircraft accelerates to
/// ~27 kts for one second, then within 5 s the navigator advances 6 segments
/// (CurrentSegmentIndex 1 -> 7), TrueHeading flips from 26 deg to 126 deg,
/// IAS collapses from 27 to 6 kts, and over the next 220 s the aircraft spins
/// in tight circles (~0.0001 deg lat/lon range) at IAS &lt; 4 kts, never
/// progressing past target node 383. The user gives up and DELs at t=1108.
///
/// Same class of bug as <see cref="OakGaSpawnTurnAroundTests"/> and the memory
/// note <c>project_fillet_arc_natural_forward.md</c>: a fillet arc
/// (<see cref="GroundArc"/>) traversed in its reverse bezier direction
/// (<c>fromNode.Id == Nodes[1].Id</c> rather than <c>Nodes[0].Id</c>) flips the
/// exit tangent 180 deg, so the navigator writes a target heading opposite the
/// next segment's natural direction. At low taxi speed the fixed-rate turn
/// never converges and the aircraft spirals in place.
///
/// **Replay strategy:** Hybrid (snapshot restore at t=875, then
/// <see cref="SimulationEngine.ReplayRange"/> forward through the TAXI at
/// t=879 and 65 s of subsequent taxi). Hybrid pins the pre-TAXI state
/// (specifically the runway exit chosen by <see cref="RunwayExitPhase"/> and
/// the resulting hold-short anchor at HS 498) so future fixes that touch
/// post-landing rollout selection don't shift which exit was used and
/// invalidate the test setup.
/// </summary>
public class OakTaxiJcSpinTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";
    private const string Callsign = "N70CS";

    /// <summary>Seconds before the TAXI command (t=879) where we restore from snapshot.</summary>
    private const int RestoreAtSeconds = 875;

    /// <summary>How many seconds after the TAXI to assert progress over.</summary>
    private const int TicksAfterTaxi = 60;

    /// <summary>End time of the assertion window. TAXI fires at t=879; we let it tick to t=940.</summary>
    private const int AssertAtSeconds = 940;

    /// <summary>
    /// First segment whose endpoint is the runway hold-short at node 501
    /// (HS 28R/10L). When CurrentSegmentIndex reaches this value, the
    /// aircraft has cleared the entire south-bound J leg and is at the
    /// commanded hold-short. The bug freezes the index at 7.
    /// </summary>
    private const int IndexAtFirstHoldShort = 14;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Assertion: 60 s after the TAXI command fires, N70CS must have advanced
    /// past the buggy zone (target node 383, segment index 7). A clean
    /// south-bound taxi on J at 30 kts covers the J leg (segs 0..13, ~0.3 nm)
    /// in well under 60 s and reaches the hold-short at node 501 (segIdx 14).
    /// We require a more lenient bound (segIdx &gt; 7) so the test isolates
    /// the spin-stall bug without coupling to exact arrival timing at HS 501.
    ///
    /// The bug freezes CurrentSegmentIndex at 7 and IAS below 4 kts forever.
    /// </summary>
    [Fact]
    public void TaxiOut_N70CS_AdvancesPastNode383_Within60Seconds()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            engine.ReplayRange(startTime, AssertAtSeconds, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var route = ac.Ground?.AssignedTaxiRoute;
            Assert.NotNull(route);

            int segIdx = route.CurrentSegmentIndex;
            double ias = ac.IndicatedAirspeed;

            output.WriteLine(
                $"After {AssertAtSeconds - 879}s of taxi: segIdx={segIdx}/{route.Segments.Count} ias={ias:F1} "
                    + $"hdg={ac.TrueHeading.Degrees:F1} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
            );

            Assert.True(
                segIdx > 7,
                $"N70CS stuck at segIdx={segIdx} after {TicksAfterTaxi}s of taxi - the spin-stall bug "
                    + $"freezes the navigator at segIdx 7 (target node 383). IAS={ias:F1} kts. "
                    + "Expected segIdx > 7 (any forward progress past the buggy fillet zone)."
            );
        }
    }

    /// <summary>
    /// Stricter follow-up: N70CS should reach the commanded hold-short at HS
    /// 501 (28R/10L) within 60 s. This catches softer regressions where the
    /// aircraft makes some progress but still stalls partway down J.
    /// </summary>
    [Fact]
    public void TaxiOut_N70CS_ReachesHoldShortAt28R_Within60Seconds()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            engine.ReplayRange(startTime, AssertAtSeconds, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var route = ac.Ground?.AssignedTaxiRoute;
            Assert.NotNull(route);

            // Either reached the hold-short segment, or holding short at HS 501
            // (the runtime sets IsCleared = true once the aircraft settles at the
            // hold-short and a CROSS / RES command lifts the gate; before that
            // the aircraft phase will be HoldingShortPhase).
            bool atOrPastHoldShort = route.CurrentSegmentIndex >= IndexAtFirstHoldShort;
            bool holdingShort = ac.Phases?.CurrentPhase is HoldingShortPhase;

            Assert.True(
                atOrPastHoldShort || holdingShort,
                $"N70CS only at segIdx={route.CurrentSegmentIndex}/{route.Segments.Count} after {TicksAfterTaxi}s; "
                    + $"expected segIdx >= {IndexAtFirstHoldShort} (HS 28R/10L at node 501) or HoldingShortPhase."
            );
        }
    }
}
