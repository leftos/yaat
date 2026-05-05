using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Diagnostic for S2-OAK-3 (1) VFR Sequencing bundle. Live session: user issued
/// `DCT VPCBT; ERB 28R` to N42416 at t=0, then `EF 28L, CLAND` at t=334. Bundle
/// snapshots show N42416 has Phases=null all the way through t=334. This test
/// replays the recording with current code and reports actual N42416 state at
/// key times so we can tell whether Bug A (compound DCT;ERB silently dropped)
/// still affects this scenario or whether something else is going on.
/// </summary>
public class S2Oak3ErbEfDiagnosticTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak-3-vfr-seq-erb-eflv-recording.yaat-bug-report-bundle.zip";

    [Fact]
    public void Diagnostic_N42416_AfterErb28R()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            output.WriteLine("Recording not loadable — skipping");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 5);

        var ac = engine.FindAircraft("N42416");
        Assert.NotNull(ac);

        output.WriteLine("=== N42416 at t=5 (5s after DCT VPCBT; ERB 28R) ===");
        output.WriteLine($"  pos=({ac.Position.Lat:F4}, {ac.Position.Lon:F4})");
        output.WriteLine($"  alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F0}");
        output.WriteLine($"  AssignedAlt={ac.Targets.AssignedAltitude}");
        output.WriteLine($"  navRoute=[{string.Join(",", ac.Targets.NavigationRoute.Select(n => n.Name))}]");
        output.WriteLine($"  phases: {DescribePhases(ac)}");
        output.WriteLine($"  queue: {ac.Queue.Blocks.Count} blocks, idx={ac.Queue.CurrentBlockIndex}");
        for (int i = 0; i < ac.Queue.Blocks.Count; i++)
        {
            var b = ac.Queue.Blocks[i];
            output.WriteLine($"    [{i}] '{b.SourceCommandText}' applied={b.IsApplied} allComplete={b.AllComplete}");
        }
        output.WriteLine($"  PendingWarnings: {string.Join(" | ", ac.PendingWarnings)}");
    }

    [Fact]
    public void Diagnostic_N42416_AtT334_BeforeEf28L()
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
        engine.Replay(recording, 333);

        var ac = engine.FindAircraft("N42416");
        Assert.NotNull(ac);

        output.WriteLine("=== N42416 at t=333 (1s before EF 28L) ===");
        output.WriteLine($"  pos=({ac.Position.Lat:F4}, {ac.Position.Lon:F4})");
        output.WriteLine($"  alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F0}");
        output.WriteLine($"  navRoute=[{string.Join(",", ac.Targets.NavigationRoute.Select(n => n.Name))}]");
        output.WriteLine($"  phases: {DescribePhases(ac)}");
    }

    [Fact]
    public void Diagnostic_N42416_FullPhaseTimeline()
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
        engine.Replay(recording, 1);

        string? lastDesc = null;
        for (int t = 1; t <= 382; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("N42416");
            if (ac is null)
            {
                continue;
            }
            string desc = DescribePhases(ac);
            if (desc != lastDesc)
            {
                output.WriteLine(
                    $"t={t, 4} hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0} rwy={ac.Phases?.AssignedRunway?.Designator ?? "-"} navRoute=[{string.Join(",", ac.Targets.NavigationRoute.Select(n => n.Name))}] phases={desc}"
                );
                lastDesc = desc;
            }
        }
    }

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
