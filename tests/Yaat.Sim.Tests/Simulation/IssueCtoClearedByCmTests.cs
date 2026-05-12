using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N157LE (P28A, VFR, KOAK -> O88) was issued <c>CTO 360</c> at
/// t=2662. The phase chain Taxiing -> HoldingShort -> LineUp -> Takeoff
/// -> InitialClimb was installed and the aircraft took off normally,
/// entering InitialClimbPhase at t=2745. At t=2754 the controller issued
/// <c>CM 020</c> (climb maintain 2000 ft) -- and the entire phase chain
/// was wiped because <see cref="InitialClimbPhase.CanAcceptCommand"/>
/// returned <c>ClearsPhase</c> for every command. With the chain gone,
/// the deferred VFR turn (AIM 4-3-2: hold runway heading until past DER
/// and within 300 ft of pattern altitude) never fired -- N157LE
/// continued on runway-28R heading instead of turning right to 360.
///
/// Expected behaviour: CM/DM during initial climb sets the altitude
/// target without disturbing the phase that owns the heading, matching
/// <see cref="TakeoffPhase.CanAcceptCommand"/>.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts.
/// Replay strategy: hybrid (snapshot at t=2750, replay forward through
/// CM at t=2754 and 70 s past it) -- the recording is 3619 s long and
/// the fix is localised to phase-acceptance behaviour from t=2754.
/// </summary>
public class IssueCtoClearedByCmTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/cto-cleared-by-cm-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N157LE";

    /// <summary>Snapshot just before CM 020 fires (t=2754).</summary>
    private const int RestoreAtSeconds = 2750;

    /// <summary>Time CM 020 is dispatched in the recording.</summary>
    private const int CmTime = 2754;

    /// <summary>Reference frame for the secondary heading-converges assertion.</summary>
    private const int AssertAtSeconds = CmTime + 70;

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
    /// PRIMARY assertion: after CM 020 dispatches, InitialClimbPhase must
    /// still be the active phase. Without the fix, CM unconditionally clears
    /// the whole tower-departure chain because CanAcceptCommand returns
    /// ClearsPhase for every CanonicalCommandType.
    /// </summary>
    [Fact]
    public void CmDuringInitialClimb_DoesNotClearPhaseChain()
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

            var preCm = engine.FindAircraft(Callsign);
            Assert.NotNull(preCm);
            Assert.IsType<InitialClimbPhase>(preCm.Phases?.CurrentPhase);

            engine.ReplayRange(startTime, CmTime + 5, recording.Actions);

            var postCm = engine.FindAircraft(Callsign);
            Assert.NotNull(postCm);

            output.WriteLine(
                $"After CM 020 at t={CmTime + 5}: alt={postCm.Altitude:F0} "
                    + $"phasesNull={postCm.Phases is null} "
                    + $"currentPhase={postCm.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} "
                    + $"assignedAlt={postCm.Targets.AssignedAltitude?.ToString() ?? "null"} "
                    + $"targetAlt={postCm.Targets.TargetAltitude}"
            );

            Assert.NotNull(postCm.Phases);
            Assert.IsType<InitialClimbPhase>(postCm.Phases.CurrentPhase);

            // CM still wires the altitude assignment through to the phase tick.
            Assert.Equal(2000, postCm.Targets.AssignedAltitude);
            Assert.Equal(2000.0, postCm.Targets.TargetAltitude);
        }
    }

    /// <summary>
    /// SECONDARY assertion: 70 s after CM, the deferred VFR turn has had
    /// time to fire (aircraft climbs through ~709 ft and InitialClimbPhase
    /// pushes <c>TargetTrueHeading</c> to the CTO 360 heading). Without
    /// the fix the phase is gone, the turn never fires, and the target
    /// heading stays on runway-28R (~292 deg true / ~280 deg mag).
    /// </summary>
    [Fact]
    public void CmDuringInitialClimb_PreservesDeferredVfrTurnTo360()
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

            engine.ReplayRange(startTime, AssertAtSeconds, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var target = ac.Targets.TargetTrueHeading;
            Assert.NotNull(target);

            var targetMag = target.Value.ToMagnetic(ac.Declination);
            var ctoMag = new MagneticHeading(360.0);
            double diffFromCto = targetMag.AbsAngleTo(ctoMag);
            double diffFromRunway = targetMag.AbsAngleTo(new MagneticHeading(280.0));

            output.WriteLine(
                $"t={AssertAtSeconds}: alt={ac.Altitude:F0} "
                    + $"hdgTrue={ac.TrueHeading.Degrees:F1} hdgMag={ac.MagneticHeading.Degrees:F1} "
                    + $"tgtHdgTrue={target.Value.Degrees:F1} tgtHdgMag={targetMag.Degrees:F1} "
                    + $"diffFromCto={diffFromCto:F1} diffFromRunway={diffFromRunway:F1}"
            );

            Assert.True(
                diffFromCto < diffFromRunway,
                $"After 70 s post-CM, target heading should have rolled toward CTO 360 mag, "
                    + $"but was {targetMag.Degrees:F1} mag (off CTO by {diffFromCto:F1}, off "
                    + $"runway-28R by {diffFromRunway:F1}). Without the fix the phase chain "
                    + "is cleared at CM and the deferred VFR turn never fires."
            );
        }
    }
}
