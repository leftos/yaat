using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #184 (ZHU S3-T1-L5 I90_A_APP, IAH ILS/RNAV 08L/08R).
///
/// Five reported bugs on arrivals/approaches:
///  1. JFAC/JLOC after an intercept heading joins the LOC "a bit off".
///  2. Some aircraft slow to near 0 ~5 mi out (CIFP continuation-record parsed MATON@2kt).
///  3. Aircraft descends when given JLOC/JFAC without a CAPP (no glideslope-clearance gate).
///  4. STAR crossing speed (CASST 210) not held — reverts to 250 default cruise.
///  5. Approach crossing speed (GUSHR 210 for ILS 08L) not held past the fix.
///
/// Key fixtures in the recording:
///  - UAL4525: JFAC I08R at t=911, never CAPP'd — old code descended 5000ft -> landed (bug #3).
///  - UAL1127: GUSHR arrival — old code re-accelerated to 250 after sequencing GUSHR (bug #5).
///  - UCA8898: CASST arrival — old code accelerated to 250 instead of holding 210 (bug #4).
///  - UCA1538: WDLNS arrival — old code drove target speed to 2kt, ~25kt at 3400ft (bug #2).
/// </summary>
public class Issue184VectorsStarAppSpeedsTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue184-i90-vectors-star-app-speeds-recording.zip";

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
    public void Ual4525_JfacWithoutCapp_HoldsAltitude_DoesNotDescendOrLand()
    {
        // Bug #3: UAL4525 got `JFAC I08R` at t=911 and was never cleared for the approach.
        // It must hold its assigned 5000ft and track the localizer laterally — never descend
        // on the glideslope or land without CAPP.
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1150);

        var aircraft = engine.FindAircraft("UAL4525");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"UAL4525 @1150: alt={aircraft.Altitude:F0} vs={aircraft.VerticalSpeed:F0} ias={aircraft.IndicatedAirspeed:F0} onGround={aircraft.IsOnGround}"
        );

        Assert.False(aircraft.IsOnGround, "UAL4525 must not have landed on a JFAC clearance alone");
        Assert.True(aircraft.Altitude > 4500, $"UAL4525 must hold ~5000ft (assigned) on a lateral JFAC, but descended to {aircraft.Altitude:F0}ft");

        // Tracking the localizer laterally: cross-track from the I08R centerline stays small.
        var clearance = aircraft.Phases?.ActiveApproach;
        var runway = aircraft.Phases?.AssignedRunway;
        if (clearance is not null && runway is not null)
        {
            double xte = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Position,
                    new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                    clearance.FinalApproachCourse
                )
            );
            output.WriteLine($"UAL4525 cross-track from I08R LOC = {xte:F2} nm");
            Assert.True(xte < 1.0, $"UAL4525 should track the I08R localizer (cross-track {xte:F2}nm)");
        }
    }

    [Fact]
    public void Ual1127_Gushr_MaintainsCrossingSpeed_NoReaccelTo250()
    {
        // Bug #5: after sequencing GUSHR (t~610) the aircraft must maintain the 210kt crossing
        // restriction down the approach, not re-accelerate to 250 default cruise.
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 720);

        var aircraft = engine.FindAircraft("UAL1127");
        Assert.NotNull(aircraft);

        output.WriteLine($"UAL1127 @720: ias={aircraft.IndicatedAirspeed:F0} ceiling={aircraft.Targets.SpeedCeiling}");
        Assert.True(
            aircraft.IndicatedAirspeed <= 215,
            $"UAL1127 should hold the 210kt crossing speed after GUSHR, but was {aircraft.IndicatedAirspeed:F0}kt"
        );
    }

    [Fact]
    public void Uca8898_Casst_SlowsToAndMaintainsCrossingRestriction()
    {
        // Bug #4: a CASST arrival crosses ZOEEE at 240, then must slow to the CASST 210
        // crossing restriction and MAINTAIN 210 past CASST — even though `DEPART CASST 267`
        // vectors it off the STAR. The CFIX 210 is an ATC-assigned speed (7110.65 5-7-1.h.4),
        // which a bare vector does not cancel; it persists until an approach/via clearance.
        // (Two underlying fixes: `DEPART` preserves the CFIX restriction it lands on, and the
        // crossed speed is published as a ceiling.)
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1000);

        var aircraft = engine.FindAircraft("UCA8898");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"UCA8898 @1000: ias={aircraft.IndicatedAirspeed:F0} ceiling={aircraft.Targets.SpeedCeiling} tgt={aircraft.Targets.TargetSpeed}"
        );
        Assert.True(
            aircraft.IndicatedAirspeed <= 215,
            $"UCA8898 should hold the CASST 210 crossing restriction past the fix, but was {aircraft.IndicatedAirspeed:F0}kt"
        );
    }

    [Fact]
    public void Uca1538_OnApproach_DoesNotSlowToNearZero()
    {
        // Bug #2: the MATON FAF carried a phantom 2kt restriction (CIFP continuation record),
        // driving the target speed to 2kt and nearly stopping the aircraft at ~3400ft.
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 510);

        var aircraft = engine.FindAircraft("UCA1538");
        Assert.NotNull(aircraft);

        output.WriteLine($"UCA1538 @510: ias={aircraft.IndicatedAirspeed:F0} alt={aircraft.Altitude:F0} tgt={aircraft.Targets.TargetSpeed}");
        Assert.True(aircraft.IndicatedAirspeed > 120, $"UCA1538 must not slow to near-zero on approach, but was {aircraft.IndicatedAirspeed:F0}kt");
    }
}
