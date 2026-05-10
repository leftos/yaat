using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Confirms that with the parallel-runway sidestep fix on the S2-OAK-3 (1) VFR
/// Sequencing bundle, EF 28L issued at t=334 does NOT clear the
/// FinalApproachPhase and rebuild a PatternEntry chain — it keeps the existing
/// chain and just retargets the runway.
/// </summary>
public class S2Oak3ErbEfDiagnosticTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak-3-vfr-seq-erb-eflv-recording.yaat-bug-report-bundle.zip";

    /// <summary>
    /// Confirms that with the parallel-runway sidestep fix, EF 28L issued at t=334
    /// does NOT clear the FinalApproachPhase and rebuild a PatternEntry chain — it
    /// keeps the existing chain and just retargets the runway. Without the fix the
    /// aircraft would fly the teardrop / 360 to reach the standard glideslope-TPA
    /// intercept point ~3.2 nm east of 28L.
    /// </summary>
    [Fact]
    public void EfSidestep_KeepsFinalApproachPhaseRunning()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());

        // Replay to t=333 (1s before EF 28L). N42416 should be on FinalApproach for 28R.
        engine.Replay(recording, 333);
        var preAc = engine.FindAircraft("N42416");
        Assert.NotNull(preAc);
        Assert.IsType<Yaat.Sim.Phases.Tower.FinalApproachPhase>(preAc.Phases?.CurrentPhase);
        Assert.Equal("28R", preAc.Phases?.AssignedRunway?.Designator);
        var preFinalApproach = preAc.Phases!.CurrentPhase;
        output.WriteLine(
            $"t=333 (pre-EF): rwy={preAc.Phases.AssignedRunway?.Designator} alt={preAc.Altitude:F0} hdg={preAc.TrueHeading.Degrees:F0} phase={preFinalApproach!.GetType().Name}"
        );

        // Tick one more second — the t=334 action `EF 28L, CLAND` applies during this tick.
        engine.ReplayOneSecond();
        var postAc = engine.FindAircraft("N42416");
        Assert.NotNull(postAc);
        output.WriteLine(
            $"t=334 (post-EF): rwy={postAc.Phases?.AssignedRunway?.Designator} alt={postAc.Altitude:F0} hdg={postAc.TrueHeading.Degrees:F0} phase={postAc.Phases?.CurrentPhase?.GetType().Name} chain={DescribePhases(postAc)}"
        );

        // Sidestep must have applied: same FinalApproachPhase instance still active,
        // assigned runway switched to 28L, no new PatternEntryPhase inserted.
        Assert.Same(preFinalApproach, postAc.Phases?.CurrentPhase);
        Assert.Equal("28L", postAc.Phases?.AssignedRunway?.Designator);
        Assert.DoesNotContain(postAc.Phases!.Phases, p => p is Yaat.Sim.Phases.Pattern.PatternEntryPhase);
    }

    private static string DescribePhases(AircraftState ac)
    {
        if (ac.Phases is null)
        {
            return "(null)";
        }
        var plist = ac.Phases.Phases;
        if (plist.Count == 0)
        {
            return "(empty)";
        }
        return string.Join(" -> ", plist.Select((p, i) => i == ac.Phases.CurrentIndex ? $"[{p.GetType().Name}]" : p.GetType().Name));
    }
}
