using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #10: per-aircraft "once holding short, delete"
/// command for landing aircraft.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts — OAK tower scenario.
/// N569SX lands on 28R, exits, and stops at HoldingAfterExitPhase at ~t=490s,
/// where it sits idle until the user manually issued DEL at t=567s.
///
/// New behaviour: <c>ONHS DEL</c> (On Hold-Short: Delete) queues a conditional
/// block that fires the instant the aircraft enters HoldingAfterExitPhase,
/// removing the aircraft via the existing delete path. <c>NODEL</c> cancels
/// the queued auto-delete and reinstates <c>AutoDeleteExempt</c>.
/// </summary>
public class Issue10OnHoldShortDeleteTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/0ee0513aa9f0.zip";
    private const string Callsign = "N569SX";

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
    /// Diagnostic: replay through landing, log phase transitions so the test
    /// author can sanity-check the timing for the recording.
    /// </summary>
    [Fact]
    public void Diagnostic_LogPhaseTransitionsAroundLanding()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 440);

        string? lastPhase = null;
        for (int t = 0; t <= 150; t++)
        {
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                output.WriteLine($"t={440 + t}: aircraft despawned");
                return;
            }

            string phase = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            if (phase != lastPhase)
            {
                output.WriteLine($"t={440 + t}: phase={phase} gs={ac.GroundSpeed:F0} alt={ac.Altitude:F0}");
                lastPhase = phase;
            }

            engine.TickOneSecond();
        }
    }

    /// <summary>
    /// Core feature: <c>ONHS DEL</c> issued during landing rollout removes the
    /// aircraft as soon as it stops at the HoldingAfterExit hold-short.
    /// </summary>
    [Fact]
    public void OnHsDel_DeletesAircraftWhenItReachesHoldingAfterExit()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 450);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.False(ac.Phases?.CurrentPhase is HoldingAfterExitPhase, "Aircraft should not yet be in HoldingAfterExitPhase at t=450");

        var result = engine.SendCommand(Callsign, "ONHS DEL");
        Assert.True(result.Success, $"ONHS DEL should dispatch successfully: {result.Message}");

        bool sawHoldingAfterExit = false;
        for (int t = 1; t <= 180; t++)
        {
            engine.TickOneSecond();

            // Inspect phase BEFORE the sweep — the trigger fires during physics, so the
            // aircraft may transition into HoldingAfterExitPhase and be queued for delete
            // in the same tick. Without this pre-sweep check we'd miss the transition
            // every time.
            var beforeSweep = engine.FindAircraft(Callsign);
            if (beforeSweep?.Phases?.CurrentPhase is HoldingAfterExitPhase)
            {
                sawHoldingAfterExit = true;
            }

            engine.SweepPendingAutoDeletes();

            var live = engine.FindAircraft(Callsign);
            if (live is null)
            {
                output.WriteLine($"aircraft auto-deleted at +{t}s (replay t={450 + t})");
                Assert.True(sawHoldingAfterExit, "Aircraft must transition through HoldingAfterExitPhase before being auto-deleted");
                return;
            }
        }

        Assert.Fail($"{Callsign} was not auto-deleted within 180 ticks after ONHS DEL");
    }

    /// <summary>
    /// <c>NODEL</c> bare verb strips a queued ONHS DEL block before it fires,
    /// so the aircraft survives reaching HoldingAfterExit.
    /// </summary>
    [Fact]
    public void NoDel_CancelsQueuedOnHsDel_AircraftSurvives()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 450);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var onhsResult = engine.SendCommand(Callsign, "ONHS DEL");
        Assert.True(onhsResult.Success, $"ONHS DEL should dispatch: {onhsResult.Message}");

        Assert.Contains(ac.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.EnteringHoldingAfterExit);

        var nodelResult = engine.SendCommand(Callsign, "NODEL");
        Assert.True(nodelResult.Success, $"NODEL should dispatch: {nodelResult.Message}");

        Assert.DoesNotContain(ac.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.EnteringHoldingAfterExit);

        // Tick through HoldingAfterExit; aircraft must still be present.
        bool reachedHoldingAfterExit = false;
        for (int t = 1; t <= 180; t++)
        {
            engine.TickOneSecond();
            engine.SweepPendingAutoDeletes();
            var live = engine.FindAircraft(Callsign);
            if (live is null)
            {
                Assert.Fail($"{Callsign} was deleted at +{t}s after NODEL cancel — auto-delete should have been suppressed");
            }

            if (live!.Phases?.CurrentPhase is HoldingAfterExitPhase)
            {
                reachedHoldingAfterExit = true;
            }
        }

        Assert.True(reachedHoldingAfterExit, "Aircraft must have entered HoldingAfterExitPhase during the tick loop for this test to be meaningful");
    }

    /// <summary>
    /// <c>NODEL</c> also flips <c>AutoDeleteExempt</c> back on, so scenario-level
    /// auto-delete (if enabled) won't pick the aircraft up either.
    /// </summary>
    [Fact]
    public void NoDel_RestoresAutoDeleteExempt()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 450);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        // Simulate the state after a prior command flipped exempt off.
        ac.Ground.AutoDeleteExempt = false;

        var nodelResult = engine.SendCommand(Callsign, "NODEL");
        Assert.True(nodelResult.Success, $"NODEL should dispatch: {nodelResult.Message}");

        Assert.True(ac.Ground.AutoDeleteExempt, "NODEL should re-arm AutoDeleteExempt = true");
    }

    /// <summary>
    /// Snapshot round-trip: a queued ONHS DEL block must survive snapshot
    /// serialization with its trigger intact. Real-world bundle replays load
    /// state from a snapshot mid-session, and the queued auto-delete must keep
    /// firing afterwards.
    /// </summary>
    [Fact]
    public void OnHsDel_QueuedBlockSurvivesSnapshotRoundTrip()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 450);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var result = engine.SendCommand(Callsign, "ONHS DEL");
        Assert.True(result.Success, $"ONHS DEL should dispatch: {result.Message}");

        // Round-trip the queue via DTO snapshot
        var queueSnapshot = ac.Queue.ToSnapshot();
        var restored = CommandQueue.FromSnapshot(queueSnapshot);

        Assert.Contains(restored.Blocks, b => b.Trigger?.Type == BlockTriggerType.EnteringHoldingAfterExit);
    }
}
