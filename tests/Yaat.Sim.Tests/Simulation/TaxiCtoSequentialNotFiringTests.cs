using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for a sequential-compound dispatch bug: the second block of
/// <c>TAXI E RWY 28R; CTO MRT</c> never fired after the aircraft reached
/// the hold-short, leaving N152SP stuck holding short of 28R until the
/// instructor manually re-sent <c>CTO MRT</c> 34 s later.
///
/// Root cause: the TAXI block was consumed by the tower path during
/// dispatch (installs TaxiingPhase), and the CTO block was enqueued with
/// no trigger. <see cref="Yaat.Sim.FlightPhysics"/>'s queue advancement
/// early-returns whenever a phase is active and the conditional-block
/// loop breaks on the first untriggered block, so the queued CTO was
/// never examined when TaxiingPhase advanced to HoldingShortPhase —
/// which would have accepted it (<see cref="HoldingShortPhase.CanAcceptCommand"/>
/// returns <c>ClearsPhase</c> for <c>ClearedForTakeoff</c>).
///
/// Recording: S2-OAK-3 (2) VFR Sequencing, callsign N152SP.
/// Replay strategy: hybrid (snapshot just before the dispatch, then
/// <see cref="SimulationEngine.ReplayRange"/> through the compound and
/// onward). The full-replay path diverges from the recording too far for
/// this bundle (other ground traffic + RNG-driven yielding bend the
/// timeline). The fix is queue/phase plumbing, so restoring an exact
/// snapshot doesn't mask the bug.
/// </summary>
public class TaxiCtoSequentialNotFiringTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/c0c9f6aa6cb7.zip";
    private const string Callsign = "N152SP";

    /// <summary>Snapshot timestamp just before the <c>TAXI E RWY 28R; CTO MRT</c> dispatch at t=607.</summary>
    private const int RestoreAtSeconds = 605;

    /// <summary>The compound TAXI E RWY 28R; CTO MRT was dispatched at t=607.</summary>
    private const int DispatchTime = 607;

    /// <summary>
    /// Stop the assertion loop right before the instructor's manual re-send at
    /// t=641 so the test only exercises the original sequential dispatch.
    /// </summary>
    private const int AssertByTime = 640;

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
    /// Primary assertion: after <c>TAXI E RWY 28R; CTO MRT</c> dispatches at t=607
    /// the aircraft should taxi to the hold-short and then automatically take off
    /// because CTO MRT is the queued head block. By <see cref="AssertByTime"/>
    /// the queued CTO block must be <c>IsApplied=true</c> and the active phase
    /// must be a downstream takeoff phase (LineUp/LinedUpAndWaiting/Takeoff/Upwind).
    /// </summary>
    [Fact]
    public void Taxi_Then_Cto_Sequential_FiresWhenHoldShortReached()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0); // load scenario + actions cursor

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                output.WriteLine($"Skipped: no snapshot at t={RestoreAtSeconds}");
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            // Replay just past the compound dispatch (t=607) to confirm the
            // CTO block was enqueued with no trigger and unapplied — this is
            // the precondition the fix is supposed to flip.
            engine.ReplayRange(startTime, DispatchTime + 1, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            Assert.NotEmpty(ac.Queue.Blocks);
            var ctoBlock = ac.Queue.Blocks[ac.Queue.Blocks.Count - 1];
            Assert.Null(ctoBlock.Trigger);
            Assert.False(ctoBlock.IsApplied);
            Assert.Contains("CTO", ctoBlock.Description ?? "");

            output.WriteLine(
                $"t={DispatchTime + 1}: phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"} "
                    + $"queued=[{string.Join(", ", ac.Queue.Blocks.Select(b => b.Description))}]"
            );

            // Replay forward through taxi → hold-short. Log every phase transition.
            string? lastPhaseName = ac.Phases?.CurrentPhase?.GetType().Name;
            for (int t = DispatchTime + 2; t <= AssertByTime; t++)
            {
                engine.ReplayRange(t - 1, t, recording.Actions);
                ac = engine.FindAircraft(Callsign);
                if (ac is null)
                {
                    Assert.Fail($"Aircraft despawned at t={t}");
                    return;
                }

                var phaseName = ac.Phases?.CurrentPhase?.GetType().Name;
                if (phaseName != lastPhaseName)
                {
                    output.WriteLine($"t={t}: phase {lastPhaseName ?? "null"} → {phaseName ?? "null"}");
                    lastPhaseName = phaseName;
                }
            }

            Assert.NotNull(ac);
            var finalPhase = ac.Phases?.CurrentPhase;
            output.WriteLine(
                $"t={AssertByTime} final: phase={finalPhase?.GetType().Name ?? "null"} "
                    + $"alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0} "
                    + $"queueBlocks={ac.Queue.Blocks.Count} "
                    + $"queue=[{string.Join(", ", ac.Queue.Blocks.Select(b => $"{b.Description}(applied={b.IsApplied})"))}]"
            );

            Assert.True(
                ac.Queue.Blocks.Any(b => b.IsApplied && (b.Description ?? "").Contains("CTO")),
                "Queued CTO MRT block was never applied. Without the fix the block sits "
                    + "untriggered/unapplied because the queue path skips untriggered blocks while a phase is active."
            );

            Assert.True(
                finalPhase is LineUpPhase or LinedUpAndWaitingPhase or TakeoffPhase or UpwindPhase,
                $"Expected LineUp/LinedUpAndWaiting/Takeoff/Upwind phase at t={AssertByTime}; got {finalPhase?.GetType().Name ?? "null"}."
            );
        }
    }
}
