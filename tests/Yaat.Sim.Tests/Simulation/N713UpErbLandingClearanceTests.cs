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
/// E2E regression for N713UP (BE36) at OAK 28R. The controller issued
/// <c>CLAND</c> (full-stop), then <c>EF 28R</c>, then <c>ERB 28R</c>. The first
/// pattern entry stamped <c>phases.TrafficDirection=Right</c>; the second read
/// that non-null direction and rebuilt the chain ending in
/// <see cref="TouchAndGoPhase"/> instead of <see cref="LandingPhase"/>. So the
/// aircraft flew the approach, touched, and climbed away ("went around") and
/// re-entered the pattern — even though it was still cleared to land — without
/// any radio call. A touch-and-go must be requested explicitly (<c>TG</c>/
/// <c>COPT</c>), never as a side effect of re-entering the pattern.
///
/// The fix derives the rebuilt circuit's terminal from the standing landing
/// clearance, so <c>ClearedToLand</c> always full-stops.
///
/// Recording: S2-OAK-5 | Practical Exam Preparation/Advanced Concepts.
/// Timeline: CLAND t=532, EF 28R t=589, ERB 28R t=607, touchdown ~t=820.
/// </summary>
public class N713UpErbLandingClearanceTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/b55a82ade9d9.zip";
    private const string Callsign = "N713UP";

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

    private static Phase? Terminator(AircraftState ac)
    {
        return ac.Phases!.Phases.LastOrDefault(p =>
            p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
        );
    }

    [Fact]
    public void ErbAfterClandAndEf_KeepsLandingTerminal()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay just past ERB 28R (t=607) — the command that downgraded the
        // terminal to TouchAndGo before the fix.
        engine.Replay(recording, 615);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);

        // Still cleared to land; the rebuilt circuit must end in LandingPhase.
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        var terminator = Terminator(ac);
        output.WriteLine($"t=615 terminator: {terminator?.Name ?? "(none)"}, clearance={ac.Phases.LandingClearance}");
        Assert.IsType<LandingPhase>(terminator);
    }

    [Fact]
    public void ClearedToLand_FullStops_DoesNotGoAroundIntoPattern()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Short final, before the ~t=820 touchdown.
        engine.Replay(recording, 815);

        // Walk through the threshold and rollout. A full-stop landing stays on
        // the runway; the bug's touch-and-go climbed back to pattern altitude
        // (~1000 ft) and re-entered Upwind by ~t=845.
        double maxAltAfterThreshold = 0;
        bool reachedGround = false;
        for (int t = 1; t <= 60; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            Assert.NotNull(ac.Phases);
            bool cyclePhasePresent = ac.Phases.Phases.Any(p => p is UpwindPhase or CrosswindPhase or TouchAndGoPhase);
            Assert.False(
                cyclePhasePresent,
                $"t={815 + t}: pattern-cycle phase present — chain is "
                    + $"[{string.Join(" -> ", ac.Phases.Phases.Select(p => p.Name))}]. "
                    + "A cleared-to-land aircraft must full-stop, not go around into the pattern."
            );

            if (ac.IsOnGround)
            {
                reachedGround = true;
            }
            if (reachedGround)
            {
                maxAltAfterThreshold = Math.Max(maxAltAfterThreshold, ac.Altitude);
            }

            if (ac.Phases.CurrentPhase is RunwayExitPhase or HoldingAfterExitPhase)
            {
                output.WriteLine($"t={815 + t}: reached {ac.Phases.CurrentPhase.Name}, alt={ac.Altitude:F0}");
                break;
            }
        }

        Assert.True(reachedGround, "N713UP never touched down within the walk window");
        // Field elevation is 9 ft; a full-stop rollout never climbs away.
        Assert.True(
            maxAltAfterThreshold < 200,
            $"After touchdown the aircraft climbed to {maxAltAfterThreshold:F0} ft — it went around instead of landing."
        );
    }
}
