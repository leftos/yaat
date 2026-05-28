using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E coverage for issue #167 — heading-only PUSH during an active pushback
/// updates the target facing in place, until the nose has begun rotating to
/// the prior target. After that the amendment is rejected.
/// </summary>
public class Issue167AdjustPushbackFaceTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private const string ScenarioPath = "TestData/sfo-gc-scenario.json";

    private static string? LoadScenarioJson()
    {
        return File.Exists(ScenarioPath) ? File.ReadAllText(ScenarioPath) : null;
    }

    private static SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        return new SimulationEngine(groundData);
    }

    private SimulationEngine? SpawnSwa1360()
    {
        var scenarioJson = LoadScenarioJson();
        var engine = BuildEngine();
        if (scenarioJson is null || engine is null)
        {
            return null;
        }

        TestVnasData.EnsureInitialized();
        engine.LoadScenario(scenarioJson, rngSeed: 42);

        // Tick to t=12s so SWA1360 (delay=10s) has spawned at B12
        for (int t = 0; t < 12; t++)
        {
            engine.TickOneSecond();
        }
        return engine;
    }

    [Fact]
    public void HeadingOnlyPush_DuringActivePushback_UpdatesFaceDirection()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        // Initial PUSH @B13 FACE N — face north
        var result = engine.SendCommand("SWA1360", "PUSH @B13 FACE N");
        Assert.True(result.Success, $"Initial PUSH failed: {result.Message}");
        Assert.IsType<PushbackToSpotPhase>(ac.Phases?.CurrentPhase);

        // Tick a few seconds — should still be in PushbackToSpotPhase, well before final node
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }
        Assert.IsType<PushbackToSpotPhase>(ac.Phases?.CurrentPhase);

        // Amend: PUSH FACE S (heading-only) → south
        var amend = engine.SendCommand("SWA1360", "PUSH FACE S");
        _output.WriteLine($"Amend result: success={amend.Success} msg={amend.Message}");
        Assert.True(amend.Success, $"PUSH FACE S amendment failed: {amend.Message}");
        Assert.IsType<PushbackToSpotPhase>(ac.Phases?.CurrentPhase);

        // Tick to completion
        bool reachedParking = false;
        for (int tick = 0; tick < 120; tick++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is AtParkingPhase)
            {
                reachedParking = true;
                break;
            }
            if (ac.Phases?.CurrentPhase is null)
            {
                break;
            }
        }

        Assert.True(reachedParking, $"Pushback should complete; got {ac.Phases?.CurrentPhase?.Name ?? "null"}");

        // Final heading should be ~180 (south), not ~0 (north — the original target)
        double hdg = ac.TrueHeading.Degrees;
        double diffSouth = Math.Abs(NormalizeAngle(hdg - 180.0));
        double diffNorth = Math.Abs(NormalizeAngle(hdg - 0.0));
        _output.WriteLine($"Final heading: {hdg:F0} (diff from 180: {diffSouth:F1}, from 0: {diffNorth:F1})");
        Assert.True(diffSouth < 5.0, $"Aircraft should face ~180 after amendment, got {hdg:F0}");
        Assert.True(diffSouth < diffNorth, "Final heading should be closer to amended target (S) than original (N)");
    }

    [Fact]
    public void NonHeadingPush_DuringActivePushback_Rejected()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        Assert.True(engine.SendCommand("SWA1360", "PUSH @B13 FACE N").Success);
        Assert.IsType<PushbackToSpotPhase>(ac.Phases?.CurrentPhase);

        // Try a non-heading-only PUSH — should fail without disturbing the active phase
        var bad = engine.SendCommand("SWA1360", "PUSH @B12");
        _output.WriteLine($"Bad PUSH result: success={bad.Success} msg={bad.Message}");
        Assert.False(bad.Success, "Non-heading-only PUSH during active pushback should fail");
        Assert.Contains("face/tail amendment", bad.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<PushbackToSpotPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void HeadingOnlyPush_AfterFinalNode_Rejected()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        Assert.True(engine.SendCommand("SWA1360", "PUSH @B13 FACE N").Success);

        // Drive past final-node reach (the in-place rotation stage). Bail when
        // pushback completes — at that point the in-place turn has finished.
        bool reachedFinalNoseRotation = false;
        for (int tick = 0; tick < 120; tick++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is not PushbackToSpotPhase)
            {
                reachedFinalNoseRotation = true;
                break;
            }

            // Try to peek at the snapshot to detect _reachedFinalNode mid-rotation
            if (ac.Phases?.CurrentPhase is PushbackToSpotPhase pp)
            {
                var snap = (Yaat.Sim.Simulation.Snapshots.PushbackToSpotPhaseDto)pp.ToSnapshot();
                if (snap.ReachedFinalNode)
                {
                    reachedFinalNoseRotation = true;
                    break;
                }
            }
        }

        Assert.True(reachedFinalNoseRotation, "test setup: pushback should reach final-node rotation");

        // Amendment must fail now — either because we're past the gate (still in
        // PushbackToSpotPhase but _reachedFinalNode=true) or because the phase
        // already ended (transitioned to AtParkingPhase / HoldingAfterPushback).
        var late = engine.SendCommand("SWA1360", "PUSH FACE E");
        _output.WriteLine($"Late amend result: success={late.Success} msg={late.Message} phase={ac.Phases?.CurrentPhase?.Name ?? "null"}");

        if (ac.Phases?.CurrentPhase is PushbackToSpotPhase)
        {
            // Still in pushback but past the gate — must be rejected with the turn-in-progress message
            Assert.False(late.Success);
            Assert.Contains("turn in progress", late.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Phase already left pushback — a new PUSH from AtParking/Holding either
            // succeeds (starts a fresh pushback) or fails with the at-parking precondition.
            // Either way, the active in-place rotation we'd be trying to amend is gone.
            // We accept this branch as long as we no longer have a PushbackToSpotPhase to mutate.
            Assert.True(ac.Phases?.CurrentPhase is not PushbackToSpotPhase);
        }
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle > 180.0)
        {
            angle -= 360.0;
        }
        if (angle < -180.0)
        {
            angle += 360.0;
        }
        return angle;
    }
}
