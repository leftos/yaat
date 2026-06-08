using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #195 (ZDV ASELC1): an IFR aircraft departing KASE RWY 33 on the
/// LINDZ ONE SID (bare <c>CTO</c>) begins its turn to the SID's initial heading (343°) far too
/// late. The first coded leg is a VA "climb heading 343° to 9100"; the turn off runway heading to
/// 343° should start at 400 ft AGL (TERPS — AIM 5-2-9.e.1 / 7110.65 5-8-3 NOTE), but the sim held
/// runway heading until the aircraft physically crossed the departure end of runway, delaying the
/// turn to ~1000 ft AGL on Aspen's long RWY 33.
///
/// Recording: SKW4757 receives <c>CTO</c> at t=197. Hybrid replay restores the snapshot at t=250 —
/// the aircraft is airborne in <see cref="Yaat.Sim.Phases.Tower.InitialClimbPhase"/> at ~411 ft AGL
/// (≈8249 ft MSL), already above the 400 ft floor, with the deferred-turn gate still armed because
/// it has not yet crossed the DER. Ticking forward: pre-fix the turn (transition into
/// <see cref="Yaat.Sim.Phases.Tower.DepartureProcedurePhase"/>, the VA leg) is delayed to ~864 ft
/// AGL; post-fix it fires immediately at ~411 ft AGL. (Hybrid replay is used because the test
/// ground-data has no ASE layout, so a full replay from t=0 cannot taxi the aircraft out — and the
/// fix is localized to the airborne turn gate, so the frozen pre-snapshot taxi/takeoff is irrelevant.)
/// </summary>
public class Issue195AseLindzTurnAltitudeTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue195-ase-lindz1-turn-altitude-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "SKW4757";
    private const double FieldElevationFt = 7838.0;
    private const double IfrTurnAglFloorFt = 400.0;
    private const int SnapshotSeconds = 250;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void Skw4757_BeginsLindzTurnAt400Agl_NotPastDer()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            output.WriteLine("Skipped: recording not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                output.WriteLine("Skipped: NavData not available");
                return;
            }

            engine.Replay(recording, 0); // load scenario + weather + nav setup
            var snapshot = archive.ReadSnapshotAt(SnapshotSeconds);
            if (snapshot is null)
            {
                output.WriteLine("Skipped: snapshot not available");
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);

            // Sanity: at restore the aircraft is airborne in InitialClimb above the 400 ft floor,
            // with the deferred turn still armed (it has not yet crossed the DER).
            var start = engine.FindAircraft(Callsign);
            Assert.NotNull(start);
            double startAgl = start.Altitude - FieldElevationFt;
            output.WriteLine(
                $"restore t={snapshot.ElapsedSeconds:F0}s phase={start.Phases?.CurrentPhase?.Name} alt={start.Altitude:F0} agl={startAgl:F0}"
            );
            Assert.Equal("InitialClimb", start.Phases?.CurrentPhase?.Name);
            Assert.True(startAgl > IfrTurnAglFloorFt, $"Fixture invariant: restored above the 400 ft floor (agl={startAgl:F0}).");

            double? turnAgl = null;
            bool reachedDepartureProcedure = false;

            for (int t = 1; t <= 120; t++)
            {
                engine.TickOneSecond();
                var aircraft = engine.FindAircraft(Callsign);
                if (aircraft is null)
                {
                    break;
                }

                string phase = aircraft.Phases?.CurrentPhase?.Name ?? "(none)";
                double agl = aircraft.Altitude - FieldElevationFt;

                if (phase == "DepartureProcedure")
                {
                    reachedDepartureProcedure = true;
                    turnAgl = agl;
                }

                if ((t % 5 == 0) || reachedDepartureProcedure)
                {
                    output.WriteLine(
                        $"t=+{t, 3} {phase, -18} alt={aircraft.Altitude, 6:F0} agl={agl, 5:F0} hdg={aircraft.TrueHeading.Degrees, 3:F0}"
                    );
                }

                if (reachedDepartureProcedure)
                {
                    break;
                }
            }

            // The aircraft must actually reach the VA leg (turn to 343°) — otherwise the assertion
            // below would pass vacuously on a replay that never got off the ground.
            Assert.True(reachedDepartureProcedure, $"{Callsign} never entered DepartureProcedure (turn to 343°) within the replay window.");
            Assert.NotNull(turnAgl);

            output.WriteLine($"Turn to 343° began at {turnAgl:F0} ft AGL.");

            // Turn must begin at ~400 ft AGL (the IFR TERPS floor), not after physically crossing the
            // DER. Pre-fix this was ~864 ft AGL; allow a small climb margin above the 400 ft floor.
            Assert.True(
                turnAgl < IfrTurnAglFloorFt + 100,
                $"{Callsign} should begin the LINDZ turn to 343° at ~400 ft AGL, but it began at {turnAgl:F0} ft AGL "
                    + "(the deferred turn was gated on physically crossing the departure end of runway — issue #195)."
            );
        }
    }
}
