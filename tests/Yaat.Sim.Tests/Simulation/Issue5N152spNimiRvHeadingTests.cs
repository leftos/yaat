using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for bug "N152SP was NIMI6 OAK V6 SAC, was not on 315 heading on
/// departure until frequency handoff". NIMI6 is a radar-vectors SID published
/// with 315° on KOAK 28R; the aircraft should hold 315° magnetic from takeoff
/// until ATC vectors or hands off. Instead, after CTO at t=1100 the aircraft
/// turned toward the first V6-expansion fix FESIK (~41° true) and stayed
/// there until the user manually issued FH 315 at t=1234.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA). N152SP
/// (C172) spawns at parking NEW7 at t=540, taxis on preset
/// "TAXI D C E 28R HS E" + "SAY REQUEST 28R @ E", controller issues TAXI at
/// t=695 and CTO at t=1100. Snapshot at t=1180 (just after liftoff into
/// InitialClimbPhase) shows DepartureRoute populated with V6 fixes but
/// SidDepartureHeadingMagnetic = null and ActiveSidId = null — signature of
/// the NavData fallback in ResolveDepartureRoute firing instead of the
/// CIFP RV-SID branch.
///
/// Companion green test S2Oak4RvSidCtoTests covers N436MS / N346G with the
/// same NIMI6/OAK6 SIDs but on a DIFFERENT bundle and slightly different
/// flight plans — the route here ("NIMI6 OAK V6 SAC") with the OAK enroute
/// extension is what reproduces the failure mode.
/// </summary>
public class Issue5N152spNimiRvHeadingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue5-n152sp-nimi6-rv-sid-not-held-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N152SP";
    private const double NimiRvHeadingMag = 315.0;

    // Pin the CIFP-backed singleton before any test method runs to avoid
    // racing with other test classes that initialize on demand.
    static Issue5N152spNimiRvHeadingTests() => TestVnasData.EnsureInitialized();

    private SimulationEngine? BuildEngine()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("InitialClimbPhase", LogLevel.Debug)
            .EnableCategory("DepartureClearanceHandler", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Regression: N152SP files NIMI6 OAK V6 SAC, gets bare CTO (no assigned altitude),
    /// takes off from 28R. The published RV-SID heading is 315° magnetic. Until ATC
    /// vectors or hands off the aircraft, the InitialClimbPhase must keep holding 315°
    /// — it must NOT prematurely complete and let FlightPhysics chase the V6 nav route.
    ///
    /// Bug observed in recording: at t=1180 the aircraft is at heading 41° true
    /// (chasing FESIK) rather than ~328° true (315° magnetic + ZOA declination).
    /// </summary>
    [Fact]
    public void N152SP_AfterCto_HoldsRvHeadingUntilFhCommand()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(1095);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1095 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={snapshotTime}");

            // Replay across CTO (t=1100) and through liftoff into InitialClimb,
            // stopping before the user's recorded FH 315 workaround at t=1234.
            // The recording dispatches "FH 315" mid-window, so cap the replay
            // at t=1230 to keep the assertion oriented at the bug-window only.
            const int AssertStartTime = 1180;
            const int AssertEndTime = 1230;
            engine.ReplayRange(snapshotTime, AssertEndTime, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            // The InitialClimbPhase must still be the active phase — otherwise
            // the heading hold has stopped and FlightPhysics is navigating to
            // the route (the bug).
            var climb = ac.Phases?.CurrentPhase as InitialClimbPhase;
            Assert.True(
                climb is not null,
                $"Expected InitialClimbPhase still active at t={AssertEndTime} (heading hold). "
                    + $"Actual current phase: {ac.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}. "
                    + $"Chain: [{string.Join(", ", ac.Phases?.Phases.Select(p => $"{p.GetType().Name}:{p.Status}") ?? [])}]"
            );

            // RV-SID heading 315° magnetic, converted to true via the aircraft's
            // ZOA declination (~12.8°E in 2026). Magnetic + east = true; expect ~327.8°.
            double declination = ac.Declination;
            double expectedTrue = new MagneticHeading(NimiRvHeadingMag).ToTrue(declination).Degrees;

            // Tolerance accommodates the deferred-turn application from runway
            // heading (~292° true) to the RV vectors heading (~327.8° true) —
            // the aircraft is rolling out of that turn around t=1180-1190.
            // Past t=1200 the heading should be steady on the RV vectors heading.
            double actualTrue = ac.TrueHeading.Degrees;
            double headingError = Math.Abs(NormalizeAngle(actualTrue - expectedTrue));
            output.WriteLine(
                $"t={AssertEndTime}: actualTrue={actualTrue:F1}° expectedTrue={expectedTrue:F1}° "
                    + $"err={headingError:F1}° decl={declination:F1}° alt={ac.Altitude:F0}ft"
            );
            Assert.True(
                headingError <= 7.0,
                $"Aircraft should be holding 315° magnetic (~{expectedTrue:F1}° true) at t={AssertEndTime}, "
                    + $"actual heading was {actualTrue:F1}° true (err={headingError:F1}°). "
                    + $"This means the RV-SID heading hold released early or never engaged."
            );

            // Heading hold must remain stable through the bug window — sample
            // ticks back to AssertStartTime to confirm no premature route chase.
            // Walk backward by re-running the replay from snapshot to each tick
            // would be expensive; instead use the recorded snapshots.
            for (int t = AssertStartTime; t <= AssertEndTime; t += 10)
            {
                var sample = archive.ReadSnapshotAt(t);
                if (sample is null)
                {
                    continue;
                }
                var snapAc = sample.State.Aircraft.FirstOrDefault(a => a.Callsign == Callsign);
                if (snapAc is null)
                {
                    continue;
                }
                // Skip the assertion at t<1190 where the aircraft is still
                // rolling out of the turn from runway heading. Use 1200+ as
                // the "steady on RV heading" gate.
                if (t < 1200)
                {
                    continue;
                }

                // Note: snapshots here capture the BUGGY behavior (the recording
                // is from before the fix). The assertion above on the post-fix
                // engine state at t=AssertEndTime is what actually proves the
                // fix; this loop only exercises the snapshot-reading helper as
                // a smoke test and writes diagnostic output. Don't assert on
                // recorded snapshots because they hold the bug, not the fix.
                output.WriteLine(
                    $"recorded snap t={t}: hdg={snapAc.TrueHeadingDeg:F1}° "
                        + $"tgtTrue={snapAc.Targets?.TargetTrueHeadingDeg?.ToString("F1") ?? "(null)"}°"
                );
            }
        }
    }

    private static double NormalizeAngle(double deg)
    {
        double n = deg % 360.0;
        if (n > 180.0)
        {
            n -= 360.0;
        }
        else if (n < -180.0)
        {
            n += 360.0;
        }
        return n;
    }
}
