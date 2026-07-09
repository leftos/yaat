using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E regression for N929AW (BE33) at OAK 28R. User cleared the aircraft to
/// land (<c>CLAND</c>) and then issued an extended right base (<c>ERB 28R</c>).
/// <c>CLAND</c> sets <c>phases.TrafficDirection=null</c>; <c>ERB</c> rebuilds the chain to
/// end in <see cref="LandingPhase"/> (full-stop terminator) but then stamps
/// <c>phases.TrafficDirection=Right</c>. When the Landing rollout completed,
/// <see cref="PhaseRunner"/>'s auto-cycle branch fired (because TrafficDirection
/// was set), appending <c>Upwind → Crosswind → Downwind → Base → FinalApproach →
/// TouchAndGo</c>, and the aircraft accelerated from rollout speed back to the
/// upwind target while still on the ground — rolling off the end of 28L into
/// the grass.
///
/// The fix discriminates the post-completion behavior by terminator phase type:
/// LandingPhase → exit the runway, regardless of TrafficDirection.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts. The recording is replayed only up to
/// t=2100; the <c>ERB</c> that arms the bug is issued by the test. The recorded <c>EF 28L</c> at
/// t=2137 was the controller reacting to issue #284 (EF routed a base-leg aircraft outbound down
/// the final), so once EF is fixed the operator inputs after it no longer describe a flyable
/// situation — the recorded ERB lands on an aircraft already 0.9 nm from the threshold and is
/// correctly refused "too close for base".
/// </summary>
public class N929awClandErbRunwayOverrunTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/606cf53c33a1.zip";
    private const string Callsign = "N929AW";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay time (s) chosen to sit after the recorded <c>CLAND</c> (t=2050) and while N929AW is
    /// still on the downwind for 28R, but before the recorded <c>EF 28L</c> (t=2137). That EF was
    /// the controller reacting to issue #284 (EF flew the aircraft outbound down the final); with
    /// EF fixed, the operator inputs after it no longer describe a flyable situation. The ERB that
    /// sets up this bug is therefore issued by the test rather than replayed.
    /// </summary>
    private const int PreErbSeconds = 2100;

    [Fact]
    public void N929AW_LandingAfterClandThenErb_ExitsRunwayInsteadOfCyclingPattern()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, PreErbSeconds);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        output.WriteLine(
            $"t={PreErbSeconds}: phase={ac.Phases.CurrentPhase?.Name}, clearance={ac.Phases.LandingClearance}/{ac.Phases.ClearedRunwayId}"
        );

        // CLAND (t=2050) is already standing for 28R. Issue the extended right base ourselves so
        // this test owns its own precondition instead of inheriting it from the recording. The
        // explicit 2 nm final is used because the aircraft is barely past the abeam point on the
        // downwind, where a no-distance ERB is (correctly) refused "too close for base".
        var erb = engine.SendCommand(Callsign, "ERB 28R 2");
        output.WriteLine($"ERB 28R 2 -> Success={erb.Success} Message='{erb.Message}'");
        Assert.True(erb.Success, $"ERB 28R 2 must set up the CLAND+ERB precondition. Got: '{erb.Message}'");

        ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);

        // Sanity: chain must end in LandingPhase (CLAND full-stop intent),
        // and TrafficDirection must be non-null (set by ERB) — this is the
        // precondition that triggers the bug.
        Phase? terminator = ac.Phases.Phases.LastOrDefault(p =>
            p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
        );
        Assert.IsType<LandingPhase>(terminator);
        Assert.NotNull(ac.Phases.TrafficDirection);

        // Walk forward until LandingPhase completes. Assert the chain never
        // grows a pattern-cycle suffix (Upwind/Crosswind/TouchAndGo) at any
        // point — that's the bug signature. Tick rather than replay: the recorded
        // commands from here on were reactions to the #284 bug.
        bool landingStarted = false;
        bool landingCompleted = false;
        double maxIasAfterLanding = 0;
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.NotNull(ac.Phases);

            bool hasCyclePhase = ac.Phases.Phases.Any(p => p is UpwindPhase or CrosswindPhase or TouchAndGoPhase);
            Assert.False(
                hasCyclePhase,
                $"t+{t}: pattern cycle phase auto-appended after Landing — "
                    + $"chain is now [{string.Join(" -> ", ac.Phases.Phases.Select(p => p.Name))}]. "
                    + "CLAND aircraft should exit the runway, not cycle the pattern."
            );

            if (ac.Phases.CurrentPhase is LandingPhase)
            {
                landingStarted = true;
            }
            else if (landingStarted && !landingCompleted)
            {
                landingCompleted = true;
                output.WriteLine(
                    $"t+{t}: Landing complete. Current phase = "
                        + $"{ac.Phases.CurrentPhase?.Name ?? "(none)"}, "
                        + $"IAS={ac.IndicatedAirspeed:F1}, IsOnGround={ac.IsOnGround}"
                );
            }

            if (landingCompleted)
            {
                maxIasAfterLanding = Math.Max(maxIasAfterLanding, ac.IndicatedAirspeed);
                if (ac.Phases.CurrentPhase is RunwayExitPhase or HoldingAfterExitPhase)
                {
                    output.WriteLine(
                        $"t+{t}: reached {ac.Phases.CurrentPhase.Name}. "
                            + $"IAS={ac.IndicatedAirspeed:F1}, maxIasAfterLanding={maxIasAfterLanding:F1}"
                    );
                    break;
                }
            }
        }

        Assert.True(landingStarted, "LandingPhase was never entered within the 400s walk window");
        Assert.True(landingCompleted, "LandingPhase never completed within the 400s walk window");

        // The current phase after Landing must be a ground-exit phase, not a
        // re-launched pattern leg.
        ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.True(
            ac.Phases.CurrentPhase is RunwayExitPhase or HoldingAfterExitPhase,
            $"Post-Landing current phase should be RunwayExitPhase or HoldingAfterExitPhase, " + $"was {ac.Phases.CurrentPhase?.Name ?? "(none)"}"
        );

        // After Landing, IAS must stay below the upwind target (≈86 kt for
        // BE33). Auto-cycle would re-accelerate toward that target.
        Assert.True(
            maxIasAfterLanding < 60,
            $"After Landing the aircraft re-accelerated to {maxIasAfterLanding:F1} kt — " + "auto-cycle pattern resumption is the bug signature."
        );
    }

    /// <summary>
    /// Companion test for the same recording — N342T was a DA42 in MRT pattern
    /// cycling touch-and-goes (chain ends in <see cref="TouchAndGoPhase"/>).
    /// PhaseRunner's auto-cycle branch must still fire after a
    /// <see cref="TouchAndGoPhase"/> completes, appending another full circuit
    /// that also ends in <see cref="TouchAndGoPhase"/>. Verifies the fix's
    /// "wasCycleTerminator" branch is still wired for the TG path.
    /// </summary>
    [Fact]
    public void N342T_TouchAndGoCompletes_AutoCyclesIntoAnotherTouchAndGoCircuit()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // First TouchAndGoPhase entered at t=830 in the recording. Replay to
        // t=820 so the aircraft is still on FinalApproach approaching the TG.
        engine.Replay(recording, 820);

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);

        // Sanity: the active chain must terminate in TouchAndGoPhase (TG intent).
        Phase? preTerminator = ac.Phases.Phases.LastOrDefault(p =>
            p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
        );
        Assert.IsType<TouchAndGoPhase>(preTerminator);

        int preTerminatorIndex = ac.Phases.Phases.IndexOf(preTerminator);

        // Walk forward until the auto-cycle appends a new pattern circuit.
        // That is detected by the appearance of additional pattern phases
        // after the original TG terminator.
        Phase? postCycleTerminator = null;
        for (int t = 1; t <= 180; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N342T");
            Assert.NotNull(ac);
            Assert.NotNull(ac.Phases);

            // The first TG must have advanced past Status.Active for the cycle
            // to have fired. Once that happens the chain length grows.
            if (ac.Phases.Phases.Count > preTerminatorIndex + 1)
            {
                postCycleTerminator = ac.Phases.Phases.LastOrDefault(p =>
                    p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
                );
                output.WriteLine($"t+{t} (replay t={820 + t}): auto-cycle fired. " + $"New terminator = {postCycleTerminator?.Name ?? "(none)"}");
                break;
            }
        }

        Assert.NotNull(postCycleTerminator);
        Assert.IsType<TouchAndGoPhase>(postCycleTerminator);
    }
}
