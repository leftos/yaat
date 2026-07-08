using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests: aircraft holding short of a crossing runway en route to its
/// destination must resume taxiing when issued RES, regardless of whether the
/// hold-short was explicit (HS X in TAXI command) or implicit (auto-added at
/// a runway crossing on the route).
///
/// Recording: S2-OAK-5 Practical Exam Preparation/Advanced Concepts —
/// N7LJ (LJ45) preset <c>TAXI D C B W W1 30 HS 28R</c>. The aircraft must
/// cross 28R/10L and 28L/10R on its way to 30. The user typed RES at both
/// hold-shorts; the RunwayCrossing one (28L/10R, no explicit HS) was rejected
/// with the misleading message "Aircraft is not held".
///
/// Bug: <c>CommandDispatcher</c> only matched ResumeCommand against
/// HoldingShortPhase when the reason was ExplicitHoldShort. RunwayCrossing
/// fell through to the generic ground handler which checks
/// <c>aircraft.Ground.IsImmobile</c> (false during a HoldingShortPhase) and
/// returned "Aircraft is not held". Fix: broaden the dispatch case so RES
/// clears any taxi hold-short (Explicit or Crossing). DestinationRunway stays
/// restricted (use CTO/LUAW).
/// </summary>
public class N7ljResExplicitHoldShortTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/a67670e50d58.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Regression test for the explicit hold-short case (already worked before
    /// the fix). At t=1216 N7LJ is in HoldingShortPhase at node 507 (28R/10L, B
    /// crossing) with reason ExplicitHoldShort because the preset included <c>HS 28R</c>.
    /// </summary>
    [Fact]
    public void Res_ClearsExplicitHoldShort_AndAircraftResumesTaxi()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1216);

        var ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);

        var holdPhase = ac.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(holdPhase);
        // Node 507 = the B/28R-10L hold-short (graph-dependent; regenerate via
        // `Yaat.LayoutInspector --exits 28R` if the ground graph changes).
        Assert.Equal(507, holdPhase.HoldShort.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, holdPhase.HoldShort.Reason);

        var result = engine.SendCommand("N7LJ", "RES");
        output.WriteLine($"RES (Explicit) result: success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"RES should clear explicit hold-short, got: {result.Message}");

        // After 5 ticks the aircraft should have left HoldingShortPhase.
        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }
        ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);
        Assert.IsNotType<HoldingShortPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The actual reported bug. At t=1300 N7LJ is in HoldingShortPhase at
    /// 28L/10R with reason RunwayCrossing (the preset only specified HS 28R).
    /// Pre-fix, RES failed with "Aircraft is not held". Post-fix, RES clears
    /// the hold and the aircraft proceeds across 28L toward runway 30.
    /// </summary>
    [Fact]
    public void Res_ClearsRunwayCrossingHoldShort_AndAircraftResumesTaxi()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1300);

        var ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);

        var holdPhase = ac.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(holdPhase);
        Assert.Equal("28L/10R", holdPhase.HoldShort.TargetName);
        Assert.Equal(HoldShortReason.RunwayCrossing, holdPhase.HoldShort.Reason);
        output.WriteLine(
            $"pre-RES: phase=HoldingShort target={holdPhase.HoldShort.TargetName} reason={holdPhase.HoldShort.Reason} nodeId={holdPhase.HoldShort.NodeId}"
        );

        int preSegIndex = ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex ?? -1;

        var result = engine.SendCommand("N7LJ", "RES");
        output.WriteLine($"RES (RunwayCrossing) result: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"RES should clear crossing-runway hold-short, got: {result.Message}");

        // After RES, the phase list advances to CrossingRunway then Taxiing.
        // Within ~10 ticks the aircraft should be off HoldingShort and moving.
        bool leftHoldShort = false;
        for (int t = 1; t <= 15; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N7LJ");
            Assert.NotNull(ac);

            if (ac.Phases?.CurrentPhase is not HoldingShortPhase)
            {
                leftHoldShort = true;
                output.WriteLine(
                    $"t=+{t}: phase={ac.Phases?.CurrentPhase?.GetType().Name} gs={ac.GroundSpeed:F1} segIdx={ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}"
                );
                break;
            }
        }

        Assert.True(leftHoldShort, "Aircraft never left HoldingShortPhase after RES");
        Assert.True(ac.GroundSpeed > 0, $"Aircraft should be moving after RES, gs={ac.GroundSpeed}");

        int postSegIndex = ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex ?? -1;
        Assert.True(postSegIndex >= preSegIndex, $"CurrentSegmentIndex should not regress, pre={preSegIndex} post={postSegIndex}");
    }
}
