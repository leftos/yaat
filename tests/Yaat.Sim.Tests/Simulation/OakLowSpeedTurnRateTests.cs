using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A taxiing aircraft can only swing its nose as fast as it rolls forward: ground yaw rate is
/// ω = v / R (nose-wheel steering sets the path curvature 1/R, so heading change per unit distance
/// is fixed and heading change per unit time scales with groundspeed), hard-capped at the
/// gear/tiller-limited <see cref="CategoryPerformance.GroundTurnRate"/> ceiling. Previously the flat
/// ceiling (piston 35 °/s) was applied with no speed coupling, so a light single creeping out of a
/// parking spot at 3–5 kt whipped its heading at the full 35 °/s — a 120° turn in ~3 s, "turning in
/// place." This regressed a real pilot's realism report.
///
/// Replays two GA pistons taxiing out of KOAK parking (N346G, N172SP) and asserts neither slews its
/// heading faster than a realistic light-single ground ceiling. Aviation-reviewed target: piston
/// ceiling 20 °/s (≈ brisk full-rudder + differential-brake pivot); a 120° turn then takes ~6 s.
/// </summary>
public class OakLowSpeedTurnRateTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-ga-spawn-turnaround-recording.yaat-bug-report-bundle.zip";

    // Realistic light-single ground yaw ceiling (deg/s) plus a small aggregation margin. The
    // aviation-reviewed piston GroundTurnRate is 20 °/s; per-second sampling can straddle a
    // sub-tick so allow a hair over. Pre-fix the pistons hit ~35 °/s, well over this.
    private const double PistonYawCeilingDegPerSec = 22.0;

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

    [Theory]
    [InlineData("N346G")]
    [InlineData("N172SP")]
    public void TaxiOut_DoesNotPivotFasterThanRealistic(string callsign)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        double prevHdg = ac.TrueHeading.Degrees;
        int total = (int)Math.Min(40, recording.TotalElapsedSeconds);

        double peakYaw = 0;
        int peakT = -1;
        double peakGs = 0;
        for (int t = 1; t <= total; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            double curHdg = ac.TrueHeading.Degrees;
            double yaw = Math.Abs((((curHdg - prevHdg) + 540.0) % 360.0) - 180.0);
            prevHdg = curHdg;

            if (yaw > peakYaw)
            {
                peakYaw = yaw;
                peakT = t;
                peakGs = ac.GroundSpeed;
            }
        }

        double impliedTurn120Sec = peakYaw > 0.01 ? 120.0 / peakYaw : double.PositiveInfinity;
        output.WriteLine(
            $"{callsign}: peak yaw {peakYaw:F1} deg/s at t={peakT}s (gs={peakGs:F1} kt) — "
                + $"a 120° turn at that rate takes {impliedTurn120Sec:F1} s"
        );

        Assert.True(
            peakYaw <= PistonYawCeilingDegPerSec,
            $"{callsign} slewed its heading at {peakYaw:F1} deg/s (gs={peakGs:F1} kt, t={peakT}s) — a light single "
                + $"taxiing out of a spot should stay at or below ~{PistonYawCeilingDegPerSec:F0} deg/s (a 120° turn in "
                + $"~6 s, not ~{impliedTurn120Sec:F1} s). Ground yaw must be v/R-coupled and capped at the realistic "
                + "GroundTurnRate ceiling."
        );
    }
}
