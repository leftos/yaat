using Xunit;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for an airborne MRT issued after the departure phase chain has completed.
///
/// Recording: "S1-OAK-7 | Evaluation Preparation" (ZOA). N248ZV is a VFR C150 that taxied
/// <c>F C B HS 33 RWY 28R</c>, crossed 33 after <c>RES</c> (t=244), got <c>CTO</c> (t=387), and
/// departed 28R. Its chain (Taxiing, HoldingShort, CrossingRunway, Taxiing, LineUp, Takeoff,
/// InitialClimb) completed at t=515. At t=570 the controller issued <c>MRT</c> while the aircraft
/// was on runway heading at pattern altitude.
///
/// Observed bug: <c>TryChangePatternDirection</c> spliced the circuit onto the completed chain via
/// <c>InsertAfterCurrent</c>, leaving the new UpwindPhase Pending at the current index. PhaseRunner's
/// pending-start heuristic treated the Pending current phase as a freshly installed list and called
/// <c>PhaseList.Start</c>, rewinding CurrentIndex to 0. The spent Taxiing and HoldingShort phases
/// instantly re-completed and the aircraft re-activated its old CrossingRunway phase mid-air:
/// speed snapped to 12 kt taxi speed, altitude froze at 1006 ft, and it crawled along the old
/// crossing geometry.
///
/// Expected: MRT on an airborne departure enters right closed traffic — UpwindPhase activates,
/// the aircraft stays airborne at flying speed, and it turns right crosswind.
/// </summary>
public class AirborneMrtCompletedChainE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/airborne-mrt-completed-chain-recording.zip";
    private const string Callsign = "N248ZV";

    /// <summary>Snapshot restore point: N248ZV upwind on runway heading, 10 s before the MRT.</summary>
    private const int RestoreTime = 560;

    /// <summary>Just past the recorded MRT at t=570.</summary>
    private const int AfterMrtTime = 572;

    /// <summary>Enough time for the upwind to complete and the crosswind turn to establish.</summary>
    private const int EndTime = 660;

    /// <summary>True heading of OAK 28R.</summary>
    private const double RunwayTrueHeading = 292.256;

    /// <summary>Signed difference between two headings, normalized to (-180, 180]. Positive = right of reference.</summary>
    private static double SignedHeadingDiff(double heading, double reference)
    {
        double diff = (heading - reference) % 360.0;
        if (diff > 180.0)
        {
            diff -= 360.0;
        }
        if (diff <= -180.0)
        {
            diff += 360.0;
        }
        return diff;
    }

    private static bool IsGroundPhase(object? phase) =>
        phase is TaxiingPhase or HoldingShortPhase or CrossingRunwayPhase or Yaat.Sim.Phases.Tower.LineUpPhase;

    [Fact]
    public void N248ZV_AirborneMrtAfterCompletedDepartureChain_FliesRightTrafficNotOldGroundPhases()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            TestVnasData.EnsureInitialized();
            if (TestVnasData.NavigationDb is null)
            {
                return;
            }

            var engine = new SimulationEngine(new TestAirportGroundData());
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreTime);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Sanity: N248ZV is airborne with a fully completed departure chain (no current phase).
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.False(pre.IsOnGround);
            Assert.Null(pre.Phases?.CurrentPhase);
            output.WriteLine($"t={RestoreTime}: alt={pre.Altitude:F0} ias={pre.IndicatedAirspeed:F0} chain complete");

            // Replay the recorded MRT (t=570) with current code.
            engine.FastForwardTo(AfterMrtTime, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            var afterMrtPhase = ac.Phases?.CurrentPhase;
            output.WriteLine(
                $"t={AfterMrtTime}: phase={afterMrtPhase?.GetType().Name ?? "(none)"} alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0}"
            );
            // Already 3 nm past the departure end at pattern altitude, so the spliced Upwind may
            // complete within the same fast-forward window — Crosswind is equally correct.
            Assert.True(
                afterMrtPhase is UpwindPhase or CrosswindPhase,
                $"Expected the MRT circuit to be flying (Upwind/Crosswind) at t={AfterMrtTime}, got {afterMrtPhase?.GetType().Name ?? "(none)"}."
            );

            // Fly out the circuit entry. The aircraft must never fall back into a ground phase,
            // must stay airborne at flying speed, and must turn right crosswind.
            int? crosswindTurnStartedAt = null;
            int? crosswindPhaseAt = null;
            for (int t = AfterMrtTime + 1; t <= EndTime; t++)
            {
                engine.ReplayOneSecond();
                ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);

                var phase = ac.Phases?.CurrentPhase;
                string phaseName = phase?.GetType().Name ?? "(none)";
                Assert.False(
                    IsGroundPhase(phase),
                    $"t={t}: N248ZV rewound into its already-completed ground phase {phaseName} "
                        + $"(alt {ac.Altitude:F0}, ias {ac.IndicatedAirspeed:F0}) after an airborne MRT."
                );
                Assert.False(ac.IsOnGround, $"t={t}: N248ZV ended up on the ground after an airborne MRT.");
                Assert.True(
                    ac.IndicatedAirspeed > 40,
                    $"t={t}: N248ZV slowed to taxi speed ({ac.IndicatedAirspeed:F0} kt) in {phaseName} after an airborne MRT."
                );

                double offRunwayHeading = SignedHeadingDiff(ac.TrueHeading.Degrees, RunwayTrueHeading);
                if (crosswindTurnStartedAt is null && offRunwayHeading > 20.0)
                {
                    crosswindTurnStartedAt = t;
                    output.WriteLine($"t={t}: turning right, {offRunwayHeading:F0} deg off runway heading, alt={ac.Altitude:F0}");
                }
                if (crosswindPhaseAt is null && phase is CrosswindPhase)
                {
                    crosswindPhaseAt = t;
                    output.WriteLine($"t={t}: CrosswindPhase active");
                }

                if (t % 15 == 0)
                {
                    output.WriteLine($"t={t} alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0} hdg={ac.TrueHeading.Degrees:F0} phase={phaseName}");
                }
            }

            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.True(crosswindPhaseAt is not null, $"N248ZV never reached CrosswindPhase by t={EndTime} — MRT did not enter right traffic.");
            Assert.True(crosswindTurnStartedAt is not null, $"N248ZV never turned crosswind by t={EndTime} (heading {ac.TrueHeading.Degrees:F0}).");

            // Right traffic: the turn must be to the right of the runway heading.
            double finalOffHeading = SignedHeadingDiff(ac.TrueHeading.Degrees, RunwayTrueHeading);
            Assert.True(finalOffHeading > 0, $"N248ZV turned left ({finalOffHeading:F0} deg off runway heading) after MRT — expected a right turn.");
        }
    }
}
