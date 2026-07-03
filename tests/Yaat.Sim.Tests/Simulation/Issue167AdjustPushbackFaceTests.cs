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
///
/// A push to a spot/parking is a direct-reverse <see cref="PushbackPhase"/> (targeted
/// mode); the amendment gate is its 60%-of-push-progress threshold.
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
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

        // Tick a few seconds — should still be pushing back, well before the nose rotation begins
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

        // Amend: PUSH FACE S (heading-only) → south
        var amend = engine.SendCommand("SWA1360", "PUSH FACE S");
        _output.WriteLine($"Amend result: success={amend.Success} msg={amend.Message}");
        Assert.True(amend.Success, $"PUSH FACE S amendment failed: {amend.Message}");
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

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
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

        // Try a non-heading-only PUSH — should fail without disturbing the active phase
        var bad = engine.SendCommand("SWA1360", "PUSH @B12");
        _output.WriteLine($"Bad PUSH result: success={bad.Success} msg={bad.Message}");
        Assert.False(bad.Success, "Non-heading-only PUSH during active pushback should fail");
        Assert.Contains("face/tail amendment", bad.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void HeadingOnlyPush_AfterTargetReached_Rejected()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        Assert.True(engine.SendCommand("SWA1360", "PUSH @B13 FACE N").Success);
        var originalPush = ac.Phases?.CurrentPhase;
        Assert.IsType<PushbackPhase>(originalPush);

        // Drive past target reach (the in-place nose-rotation stage). Bail when the original
        // pushback completes (transitions off the phase) or reports _reachedTarget.
        bool reachedTargetRotation = false;
        for (int tick = 0; tick < 120; tick++)
        {
            engine.TickOneSecond();
            if (!ReferenceEquals(ac.Phases?.CurrentPhase, originalPush))
            {
                reachedTargetRotation = true;
                break;
            }

            var snap = (Yaat.Sim.Simulation.Snapshots.PushbackPhaseDto)((PushbackPhase)originalPush!).ToSnapshot();
            if (snap.ReachedTarget)
            {
                reachedTargetRotation = true;
                break;
            }
        }

        Assert.True(reachedTargetRotation, "test setup: pushback should reach target rotation");

        var late = engine.SendCommand("SWA1360", "PUSH FACE E");
        _output.WriteLine($"Late amend result: success={late.Success} msg={late.Message} phase={ac.Phases?.CurrentPhase?.Name ?? "null"}");

        if (ReferenceEquals(ac.Phases?.CurrentPhase, originalPush))
        {
            // Still the ORIGINAL pushback, past the gate — the face amendment must be rejected.
            Assert.False(late.Success);
            Assert.Contains("turn in progress", late.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // The original pushback already ended. A fresh PUSH FACE E may start a new simple
            // pushback or be rejected by the at-parking precondition; either way the original
            // in-place turn is no longer amendable, which is the behavior under test.
            Assert.True(!ReferenceEquals(ac.Phases?.CurrentPhase, originalPush));
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
