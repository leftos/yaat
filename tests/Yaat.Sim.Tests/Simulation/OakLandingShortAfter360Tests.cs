using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E regression: a 360 on final makes the aircraft skip the approach and land
/// far short of the runway.
///
/// N2BP (piston GA) was on a straight-in final to OAK 28L when the controller
/// issued <c>L360</c> for spacing. <see cref="Yaat.Sim.Commands.PatternCommandHandler"/>
/// re-inserts a clone of the current leg after a 360 so the aircraft resumes it, but
/// <c>ClonePatternPhase</c> only handled Downwind/Base/Crosswind/Upwind — NOT
/// <see cref="Yaat.Sim.Phases.Tower.FinalApproachPhase"/>. So the 360 inserted only the
/// turn, and when it completed the chain advanced straight to
/// <see cref="Yaat.Sim.Phases.Tower.LandingPhase"/> at ~700 ft AGL / 2.2 nm out.
/// LandingPhase descends at the category rate toward field elevation with no glideslope
/// tracking, so the aircraft sank below path and touched down ~2,600 ft BEFORE the 28L
/// threshold, rolling up the extended centerline and "exiting" onto taxiway B at the
/// threshold (the user's reported symptom).
///
/// The fix teaches <c>ClonePatternPhase</c> to resume <c>FinalApproachPhase</c> after a
/// 360 (mirroring the existing S-turn-for-spacing resume), so the aircraft re-captures
/// the glideslope and touches down on the runway.
///
/// Recording: S2-OAK-3 (2) | VFR Sequencing. Timeline: EF 28L straight-in t=834,
/// CLAND t=849, L360 t=1048, touchdown ~t=1257 (pre-fix, ~2,629 ft short).
/// </summary>
public class OakLandingShortAfter360Tests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-landing-short-after-360-recording.zip";
    private const string Callsign = "N2BP";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

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
    public void L360OnFinal_TouchesDownOnRunway_NotShortOfThreshold()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Start just before the L360 (t=1048); N2BP is already established on final.
        engine.Replay(recording, 1000);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.NotNull(ac.Phases.AssignedRunway);

        var runway = ac.Phases.AssignedRunway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

        bool reachedGround = false;
        double touchdownAlongFt = 0;
        string? exitTaxiway = null;

        for (int t = 1; t <= 340; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            double alongFt = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, runway.TrueHeading) * 6076.12;

            if (ac.IsOnGround && !reachedGround)
            {
                reachedGround = true;
                touchdownAlongFt = alongFt;
                output.WriteLine($"t={1000 + t}: TOUCHDOWN at {alongFt:F0} ft relative to 28L threshold (ias={ac.IndicatedAirspeed:F0})");
            }

            if (reachedGround && exitTaxiway is null && ac.Ground.CurrentTaxiway is not null)
            {
                exitTaxiway = ac.Ground.CurrentTaxiway;
                output.WriteLine($"t={1000 + t}: exited onto taxiway {exitTaxiway} at {alongFt:F0} ft");
            }

            if ((ac.Phases?.CurrentPhase is RunwayExitPhase or HoldingAfterExitPhase) && (exitTaxiway is not null))
            {
                break;
            }
        }

        Assert.True(reachedGround, "N2BP never touched down within the replay window");

        // Root cause: after a 360 on final the aircraft must re-fly the approach and touch
        // down ON the runway, not ~2,600 ft short of the threshold (the pre-fix behavior).
        Assert.True(
            touchdownAlongFt >= 0,
            $"N2BP touched down {touchdownAlongFt:F0} ft relative to the 28L threshold — it landed short of "
                + "the runway in the undershoot area (pre-fix it touched down ~2,629 ft short). A 360 on final "
                + "must resume the approach and land on the runway."
        );

        // Reported symptom: landing short let the rollout 'exit' onto B, which crosses the
        // runway at the 28L threshold. A normal touchdown rolls past B and exits further down.
        Assert.True(
            exitTaxiway != "B",
            $"N2BP exited onto taxiway B (at the 28L threshold) — only reachable because it landed short. "
                + $"Expected a normal mid-runway exit. (touchdown was {touchdownAlongFt:F0} ft past threshold)"
        );
    }
}
