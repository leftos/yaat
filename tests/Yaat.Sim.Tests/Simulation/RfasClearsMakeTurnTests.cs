using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N34945 (airborne, S2-OAK-5, ZOA) was issued <c>R360</c> at t=2043,
/// installing a <see cref="MakeTurnPhase"/> (the Make-Right-360 orbit) at t=2045.
/// At t=2060 the controller issued <c>RFAS</c> (Reduce to Final Approach Speed) --
/// a pure speed instruction -- and the entire turn phase was wiped because
/// <see cref="MakeTurnPhase.CanAcceptCommand"/> allow-listed <c>Speed</c>/<c>Mach</c>
/// but not <c>ReduceToFinalApproachSpeed</c>, so RFAS fell to <c>ClearsPhase</c>.
///
/// Expected behaviour: speed and heading are independent control axes. RFAS sets
/// the speed target without disturbing the lateral maneuver that owns the heading,
/// matching the phase's own documented intent ("Altitude and speed adjustments are
/// additive -- let them pass through without cancelling the orbit").
///
/// Recording: S2-OAK-5 (2) | Practical Exam Preparation/Advanced Concepts.
/// Replay strategy: hybrid (snapshot at t=2050, replay forward through RFAS at
/// t=2060) -- the recording is 4097 s long and the fix is localised to
/// phase-acceptance behaviour from t=2060, so pre-RFAS state is identical with or
/// without the fix.
/// </summary>
public class RfasClearsMakeTurnTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/rfas-clears-r360-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N34945";

    /// <summary>Snapshot just after MakeTurn is installed (t=2045) and before RFAS (t=2060).</summary>
    private const int RestoreAtSeconds = 2050;

    /// <summary>Time RFAS is dispatched in the recording.</summary>
    private const int RfasTime = 2060;

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
    /// PRIMARY assertion: after RFAS dispatches, MakeTurnPhase must still be the
    /// active phase, and RFAS must still have applied its speed reduction. Without
    /// the fix, RFAS clears the turn phase (Phases == null) while applying the speed.
    /// </summary>
    [Fact]
    public void RfasDuringMakeTurn_DoesNotClearTurnPhase()
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
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            var preRfas = engine.FindAircraft(Callsign);
            Assert.NotNull(preRfas);
            Assert.IsType<MakeTurnPhase>(preRfas.Phases?.CurrentPhase);

            engine.ReplayRange(startTime, RfasTime + 5, recording.Actions);

            var postRfas = engine.FindAircraft(Callsign);
            Assert.NotNull(postRfas);

            output.WriteLine(
                $"After RFAS at t={RfasTime + 5}: phasesNull={postRfas.Phases is null} "
                    + $"currentPhase={postRfas.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} "
                    + $"hasExplicitSpeed={postRfas.Targets.HasExplicitSpeedCommand} "
                    + $"assignedSpeed={postRfas.Targets.AssignedSpeed?.ToString() ?? "null"}"
            );

            // The lateral 360 survives the speed instruction.
            Assert.NotNull(postRfas.Phases);
            Assert.IsType<MakeTurnPhase>(postRfas.Phases.CurrentPhase);

            // RFAS still wires its speed reduction through.
            Assert.True(postRfas.Targets.HasExplicitSpeedCommand, "RFAS should still apply its speed reduction");
            Assert.NotNull(postRfas.Targets.AssignedSpeed);
        }
    }
}
