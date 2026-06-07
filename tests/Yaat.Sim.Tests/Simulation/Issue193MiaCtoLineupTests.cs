using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #193: at KMIA an aircraft cleared for takeoff at the
/// 8R hold short never lined up, and a second CTO was rejected with "Aircraft is
/// not lined up and waiting."
///
/// ENY3516 (E75L) reached the 8R hold short on a taxiway running parallel to the
/// runway — stopped ~279 ft south of the centerline, heading ~284° true (nearly
/// the 26L reciprocal). Lining up on 8R (087°) is a 163° net turn, which the old
/// <see cref="LineUpGeometry"/> rejected on its single-arc 150° cap, leaving the
/// aircraft frozen in a faulted LineUp. The fix scopes that cap (and the
/// convergence check) to the aligned path only, so the pivot maneuver lines the
/// aircraft up via two ~90° turns and it takes off.
///
/// Recording: T1 S2 Practical Exam (MIA East), ARTCC ZMA, KMIA.
/// </summary>
public class Issue193MiaCtoLineupTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue193-mia-cto-lineup-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "ENY3516";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

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

    [Fact]
    public void ENY3516_ClearedForTakeoff_LinesUpAndTakesOff()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Full replay applies both CTOs (t=128 stored during taxi, consumed at
        // t=150; t=161 redundant). By the recording end the aircraft is mid-line-up.
        engine.Replay(recording, (int)recording.TotalElapsedSeconds);

        var aircraft = engine.FindAircraft(Callsign);
        if (aircraft is null)
        {
            output.WriteLine($"Skipped: {Callsign} not present at end of replay");
            return;
        }

        output.WriteLine(
            $"t=end: phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} "
                + $"hdg={aircraft.TrueHeading.Degrees:F0} ias={aircraft.IndicatedAirspeed:F0} "
                + $"onGround={aircraft.IsOnGround} chain={Chain(aircraft)}"
        );

        // The aircraft must have been cleared for takeoff (LineUp/Takeoff/InitialClimb
        // chain in hand), not left taxiing or in a holding phase.
        bool hasTowerChain = aircraft.Phases?.Phases.Any(p => p is LineUpPhase or TakeoffPhase or InitialClimbPhase) == true;
        Assert.True(hasTowerChain, $"{Callsign} has no lineup/takeoff chain: {Chain(aircraft)}");

        // Tick forward and require the aircraft to leave the faulted/frozen state:
        // line up (heading converges to runway 087°), roll, and become airborne.
        bool becameAirborne = false;
        double minHeadingErr = double.MaxValue;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            double hdgErr = GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, 87.37);
            minHeadingErr = Math.Min(minHeadingErr, hdgErr);

            if (t % 15 == 0)
            {
                output.WriteLine(
                    $"t+{t}: phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} "
                        + $"hdg={ac.TrueHeading.Degrees:F0} (err {hdgErr:F0}) ias={ac.IndicatedAirspeed:F0} "
                        + $"alt={ac.Altitude:F0} onGround={ac.IsOnGround}"
                );
            }

            if (!ac.IsOnGround && ac.Altitude > 50)
            {
                becameAirborne = true;
                output.WriteLine($"Airborne at t+{t}: alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0}");
                break;
            }
        }

        // Lined up on the runway at some point (the old bug froze it at 284°, a
        // ~163° error that never improved).
        Assert.True(
            minHeadingErr < 5.0,
            $"{Callsign} never aligned with runway 087° (best error {minHeadingErr:F0}°) — still stuck in faulted lineup"
        );
        Assert.True(becameAirborne, $"{Callsign} never became airborne after CTO");
    }

    private static string Chain(AircraftState ac) => string.Join(", ", ac.Phases?.Phases.Select(p => $"{p.GetType().Name}:{p.Status}") ?? []);
}
