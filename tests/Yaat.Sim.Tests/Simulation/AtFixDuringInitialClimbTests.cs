using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for AT-conditional dispatch tearing down active phases.
///
/// Scenario S2-OAK-4 | VFR Transitions/Radar Concepts. N172SP departs OAK,
/// reaches InitialClimb at t=360, and at t=426 the user issues
/// "AT OAK30NUM DCT VPMID". The DCT VPMID is supposed to be queued behind a
/// ReachFix(OAK30NUM) trigger and only fire once OAK30NUM is sequenced.
/// Instead, CommandDispatcher.DispatchWithPhase consults
/// InitialClimbPhase.CanAcceptCommand for the wrapped DCT verb (which
/// returns ClearsPhase for everything), tears the phase down at dispatch
/// time, and emits "InitialClimb cancelled by AT OAK30NUM DCT VPMID".
///
/// Bundle: at-fix-cancels-phase-recording.yaat-bug-report-bundle.zip
/// </summary>
public class AtFixDuringInitialClimbTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/at-fix-cancels-phase-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replays past the user's "AT OAK30NUM DCT VPMID" at t=426 and asserts
    /// that the active phase chain (InitialClimb at this point) is preserved
    /// and the conditional block is sitting in the queue with a ReachFix
    /// trigger for OAK30NUM. No "cancelled by" warning should have fired.
    /// </summary>
    [Fact]
    public void AtFixConditional_DoesNotCancelInitialClimb()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay through the dispatch of "AT OAK30NUM DCT VPMID" (recorded at t=426).
        // A few extra seconds give the warning a chance to drain into PendingWarnings.
        engine.Replay(recording, 432);

        var aircraft = engine.FindAircraft("N172SP");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"phase={aircraft.Phases?.CurrentPhase?.Name ?? "(null)"} "
                + $"alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F0} "
                + $"queueBlocks={aircraft.Queue.Blocks.Count} "
                + $"warnings={aircraft.PendingWarnings.Count}"
        );

        foreach (var w in aircraft.PendingWarnings)
        {
            output.WriteLine($"  WRN: {w}");
        }

        foreach (var b in aircraft.Queue.Blocks)
        {
            var triggerDesc = b.Trigger is null ? "(none)" : $"{b.Trigger.Type} fix={b.Trigger.FixName} alt={b.Trigger.Altitude}";
            output.WriteLine($"  BLOCK trigger={triggerDesc} applied={b.IsApplied} dims={b.Dimensions}");
        }

        // The InitialClimb chain must NOT have been torn down by the conditional dispatch.
        // The aircraft is still climbing toward its assigned altitude at t=432, well before
        // InitialClimb's natural completion, so the phase chain should still be active.
        Assert.NotNull(aircraft.Phases);
        Assert.NotNull(aircraft.Phases.CurrentPhase);
        Assert.IsType<InitialClimbPhase>(aircraft.Phases.CurrentPhase);

        // No phase-cancellation warning tied to the conditional command.
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("cancelled by") && w.Contains("OAK30NUM"));

        // The conditional block is queued with a ReachFix(OAK30NUM) trigger,
        // unapplied, waiting for the trigger to fire on fix sequencing.
        var queued = aircraft.Queue.Blocks.SingleOrDefault(b =>
            b.Trigger is { Type: BlockTriggerType.ReachFix } t && string.Equals(t.FixName, "OAK30NUM", StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(queued);
        Assert.False(queued.IsApplied, "AT OAK30NUM block should not yet be applied — OAK30NUM has not been sequenced");
    }
}
