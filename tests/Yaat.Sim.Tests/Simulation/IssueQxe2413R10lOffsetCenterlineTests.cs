using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression test for the user-reported KSFO RWY 10L offset-approach centerline bug.
/// QXE2413 was cleared for the RNAV (RNP) Y RWY 10L approach (CAPP at t=132 in the
/// recording). Pre-fix: the FAC-to-runway-heading ramp lerped only the heading, leaving
/// lateral guidance pinned to the FAC line all the way to threshold. Aircraft landed
/// ~150 ft offset from runway centerline despite executing the offset approach cleanly.
///
/// Post-fix: <see cref="FinalApproachPhase"/> lerps both the lateral course AND anchor
/// (so the aim-point converges to the runway centerline by ramp end), and
/// <see cref="LandingPhase"/> StabilizedApproach + Flare share the rollout's bounded
/// XTE crab so the heading is continuous across the handoff.
///
/// Recording: S3-NCTB-6 (B) | SFO10 (user-reported bug bundle).
/// </summary>
public class IssueQxe2413R10lOffsetCenterlineTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/qxe2413-r10l-offset-landing-centerline-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "QXE2413";

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
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Information));
        SimLog.InitializeForTest(loggerFactory);

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TouchdownIsCentered()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay the full session up to touchdown. The recording is 520s long and the
        // aircraft touches down before t=480s; iterate via ReplayOneSecond so the
        // recorded actions are applied at their original timestamps.
        engine.Replay(recording, 1);

        double xteFtAtTouchdown = double.NaN;
        TrueHeading? rwyHeading = null;
        double thresholdLat = 0;
        double thresholdLon = 0;

        for (int t = 1; t <= 520; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            if (rwyHeading is null && ac.Phases?.AssignedRunway is not null)
            {
                rwyHeading = ac.Phases.AssignedRunway.TrueHeading;
                thresholdLat = ac.Phases.AssignedRunway.ThresholdLatitude;
                thresholdLon = ac.Phases.AssignedRunway.ThresholdLongitude;
            }

            // First tick where aircraft is on the ground = touchdown. Capture XTE
            // relative to runway centerline at that moment (before rollout XTE
            // correction starts pulling it toward centerline).
            if (ac.IsOnGround && double.IsNaN(xteFtAtTouchdown))
            {
                Assert.NotNull(rwyHeading);
                double xteNm = GeoMath.SignedCrossTrackDistanceNm(ac.Position, new LatLon(thresholdLat, thresholdLon), rwyHeading.Value);
                xteFtAtTouchdown = xteNm * 6076.12;
                output.WriteLine($"Touchdown at t={t}s: pos=({ac.Position.Lat:F6}, {ac.Position.Lon:F6}), xte={xteFtAtTouchdown:F1} ft");
                break;
            }
        }

        Assert.False(double.IsNaN(xteFtAtTouchdown), "QXE2413 never touched down within 520s of replay.");

        // KSFO 10L is 200 ft wide; landing within 30 ft of centerline is "centered" with
        // margin to spare. Pre-fix the aircraft landed ~150 ft offset (well outside
        // half-width); the lateral-guidance ramp + LandingPhase XTE crab should keep it
        // tightly on centerline.
        Assert.True(
            Math.Abs(xteFtAtTouchdown) < 30.0,
            $"QXE2413 touched down {xteFtAtTouchdown:F0} ft off centerline — offset-approach lateral convergence regressed."
        );
    }
}
