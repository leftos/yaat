using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Acceptance;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression tests for the S2-OAK-3 post-landing taxi reversals. Two commands
/// from the recording produced <see cref="TaxiRoute.Segments"/> lists that
/// contained a U-turn — an (a,b) segment immediately followed by (b,a). The
/// walk overshot the ramp branch-off on the last explicit taxiway, and the
/// A* extension back to parking retraced the overshoot.
///
/// - N9225L at t=424: <c>TAXI D @NEW1</c> (102 segments, reversal at index 81).
/// - N436MS at t=455: <c>TAXI C @JSX1</c> (59 segments, reversal at index 46).
/// </summary>
[Collection("Acceptance")]
public class OakPostLandingReversalsTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-follow-runaway-ias-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData(FilletMode.Standard);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static int CountReversals(IReadOnlyList<TaxiRouteSegment> segments)
    {
        int count = 0;
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            var a = segments[i];
            var b = segments[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                count++;
            }
        }

        return count;
    }

    private static void TickUntilAtParking(SimulationEngine engine, string callsign, int maxTicks)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(callsign);
            if (ac?.Phases?.CurrentPhase?.Name == "At Parking")
            {
                return;
            }
        }
    }

    [Fact]
    public void N9225L_TaxiD_AtNEW1_HasNoReversals()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            using var _ = TickRecorder.Attach(engine, Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "oak-n9225l-taxi.json"), "N9225L");

            // The recording's TAXI D @NEW1 fires at t=424, but the route a TAXI resolves
            // depends on the aircraft's exact position when the command is issued. Replaying
            // to a fixed time makes that route hostage to rollout/exit timing — the
            // navigator's slower exit leaves N9225L still mid-exit of 28R/10L at
            // t=424, so the recorded command resolves from a transient position and produces
            // a worse (double-crossing) route. Anchor on the aircraft's STATE, not the clock:
            // replay to t=423 (before the recorded TAXI), then tick — which does not fire
            // recorded commands — until it settles into HoldingAfterExit (fully clear of the
            // runway, stopped), then issue TAXI D @NEW1 from that deterministic position.
            engine.Replay(recording, 423);

            var ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            for (int t = 0; (t < 120) && (ac!.Phases?.CurrentPhase?.Name != "Holding After Exit"); t++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft("N9225L");
            }

            Assert.Equal("Holding After Exit", ac!.Phases?.CurrentPhase?.Name);

            var taxi = engine.SendCommand("N9225L", "TAXI D @NEW1");
            Assert.True(taxi.Success, $"TAXI D @NEW1 failed: {taxi.Message}");

            ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            Assert.NotNull(ac.Ground.AssignedTaxiRoute);

            var segments = ac.Ground.AssignedTaxiRoute.Segments;
            int reversals = CountReversals(segments);
            output.WriteLine($"N9225L AssignedTaxiRoute: {segments.Count} segments, {reversals} reversal(s)");
            Assert.True(reversals == 0, $"N9225L TAXI D @NEW1 produced {reversals} reversal(s) in {segments.Count} segments");

            // Tick forward so the aircraft taxis to NEW1 — also produces the full trajectory
            // in the attached recorder's CSV for post-hoc visualization with LayoutInspector.
            TickUntilAtParking(engine, "N9225L", maxTicks: 600);
            ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            Assert.Equal("At Parking", ac.Phases?.CurrentPhase?.Name);
        }
    }

    /// <summary>
    /// An aircraft still exiting the runway it just landed on (RunwayExitPhase), given a
    /// TAXI whose route's first runway hold-short is that SAME runway, must clear forward —
    /// finishing its exit — not hold short of the runway it is leaving (which would strand
    /// it on the runway). Mirrors the holding-short implicit first-crossing clearance for
    /// the exit case. Regression for #56: the slower exit leaves N9225L still mid-exit of
    /// 28R/10L when TAXI D @NEW1 fires; without this it holds short of 28R/10L and never
    /// reaches parking.
    /// </summary>
    [Fact]
    public void TaxiWhileExitingRunway_ClearsOwnExitHoldShort()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            // Advance until N9225L is mid-exit of the runway it just landed on. Replay
            // applies the recorded landing clearance; ticking then carries it through the
            // rollout into RunwayExitPhase (TickOneSecond does not fire recorded commands).
            engine.Replay(recording, 400);
            RunwayExitPhase? exitPhase = null;
            for (int t = 0; t < 60 && exitPhase is null; t++)
            {
                var probe = engine.FindAircraft("N9225L");
                exitPhase = probe?.Phases?.CurrentPhase as RunwayExitPhase;
                if (exitPhase is null)
                {
                    engine.TickOneSecond();
                }
            }

            Assert.NotNull(exitPhase);
            Assert.False(string.IsNullOrEmpty(exitPhase.RunwayId), "exit phase must know which runway it is exiting");
            string exitRwy = exitPhase.RunwayId!;
            output.WriteLine($"N9225L caught mid-exit of {exitRwy}");

            var result = engine.SendCommand("N9225L", "TAXI D @NEW1");
            Assert.True(result.Success, $"TAXI D @NEW1 failed: {result.Message}");

            var ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            var route = ac.Ground.AssignedTaxiRoute;
            Assert.NotNull(route);

            var firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
            Assert.NotNull(firstCrossing);
            Assert.True(
                RunwayIdentifier.Parse(firstCrossing.TargetName!).Overlaps(RunwayIdentifier.Parse(exitRwy)),
                $"route's first crossing ({firstCrossing.TargetName}) should be the exit runway {exitRwy}"
            );
            Assert.True(
                firstCrossing.IsCleared,
                $"aircraft exiting {exitRwy} must clear its own exit hold-short ({firstCrossing.TargetName} at node {firstCrossing.NodeId}), not hold short of the runway it is leaving"
            );
        }
    }

    /// <summary>
    /// End-to-end guard for the intermediate-transition direction bug: N9225L, just off 28R/10L on
    /// taxiway G, given <c>TAXI C D @NEW1</c>. Naming C makes G→C an intermediate (tail-probed)
    /// transition; at OAK's collapsed G/C/D interchange the probe used to pick the wrong-way-onto-C
    /// junction (its tail dead-ends where turning onto D is an inadmissible U-turn), producing a loop
    /// that threads taxiway E and doubles back across runway 28R before reaching NEW1. This exercises
    /// the real command path — the CurrentTaxiway="G" prepend (<c>[G,C,D]</c>) and start-heading
    /// plumbing in GroundCommandHandler.TryTaxi — from the deterministic HoldingAfterExit state, and
    /// asserts the route goes directly to NEW1 without re-crossing the runway.
    /// </summary>
    [Fact]
    public void N9225L_TaxiCD_AtNEW1_GoesDirectNotWrongWayAcrossRunway()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            // Anchor on STATE, not the clock (see N9225L_TaxiD_AtNEW1_HasNoReversals): replay to just
            // before the recorded taxi, then tick (which does not fire recorded commands) until N9225L
            // settles into HoldingAfterExit — a deterministic position off the runway on G.
            engine.Replay(recording, 423);

            var ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            for (int t = 0; (t < 120) && (ac!.Phases?.CurrentPhase?.Name != "Holding After Exit"); t++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft("N9225L");
            }

            Assert.Equal("Holding After Exit", ac!.Phases?.CurrentPhase?.Name);

            var taxi = engine.SendCommand("N9225L", "TAXI C D @NEW1");
            Assert.True(taxi.Success, $"TAXI C D @NEW1 failed: {taxi.Message}");

            ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            Assert.NotNull(ac.Ground.AssignedTaxiRoute);

            var segments = ac.Ground.AssignedTaxiRoute.Segments;
            output.WriteLine($"N9225L AssignedTaxiRoute: {segments.Count} segments, {ac.Ground.AssignedTaxiRoute.TotalDistanceNm:F2} nm");

            // The wrong-way loop's signature is threading taxiway E and re-crossing runway 28R/10L on
            // the way to NEW1. The direct route is G → D → ramp; C is only touched at the interchange.
            Assert.DoesNotContain(segments, s => s.Edge.Edge.IsRunwayCenterline);
            Assert.DoesNotContain(segments, s => s.TaxiwayName == "E");
            Assert.Equal(0, CountReversals(segments));

            TickUntilAtParking(engine, "N9225L", maxTicks: 600);
            ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            Assert.Equal("At Parking", ac.Phases?.CurrentPhase?.Name);
        }
    }

    [Fact]
    public void N436MS_TaxiC_AtJSX1_HasNoReversals()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            using var _ = TickRecorder.Attach(engine, Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "oak-n436ms-taxi.json"), "N436MS");

            // The recording's TAXI C @JSX1 at t=455 is rejected during replay because our
            // re-simulated physics has N436MS still in the Landing phase at t=455 (minor
            // drift from the original run). Replay to the end of the recording so the
            // aircraft settles into HoldingAfterExit, then re-issue the command.
            engine.Replay(recording, 614);

            var ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);
            output.WriteLine(
                $"N436MS at t=614: phase={ac.Phases?.CurrentPhase?.Name} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) gs={ac.GroundSpeed:F1}"
            );

            var taxi = engine.SendCommand("N436MS", "TAXI C @JSX1");
            Assert.True(taxi.Success, $"TAXI C @JSX1 failed after replay: {taxi.Message}");

            ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);
            Assert.NotNull(ac.Ground.AssignedTaxiRoute);

            var segments = ac.Ground.AssignedTaxiRoute.Segments;
            int reversals = CountReversals(segments);
            output.WriteLine($"N436MS AssignedTaxiRoute: {segments.Count} segments, {reversals} reversal(s)");
            Assert.True(reversals == 0, $"N436MS TAXI C @JSX1 produced {reversals} reversal(s) in {segments.Count} segments");

            TickUntilAtParking(engine, "N436MS", maxTicks: 600);
            ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);
            Assert.Equal("At Parking", ac.Phases?.CurrentPhase?.Name);
        }
    }
}
