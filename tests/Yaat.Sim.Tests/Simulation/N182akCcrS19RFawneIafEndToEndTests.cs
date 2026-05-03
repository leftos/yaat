using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E recording replay for N182AK on the SOFIA TWO VOR/DME RWY 19R approach at KCCR.
///
/// At t=1475 the controller issued <c>DCT FAWNE</c>; by t=1490 the DCT had completed
/// (NavigationRoute emptied). At t=1493 the controller issued <c>CAPP S19R</c>. The
/// aircraft was 0.33 nm SSE of FAWNE heading ~28° true at 6000 ft. The published
/// procedure expects the approach to start at FAWNE on heading 191° magnetic, fly
/// FAWNE → HUKVI → CCR (FAF) on 191°, then offset FAC 172° to RW19R.
///
/// Bug: <see cref="ApproachCommandHandler.SelectBestTransition"/>'s position-based
/// fallback picks REJOY (a transition IAF 9.2 nm east) because it only considers
/// transition IAFs and ignores the common-leg IAF (FAWNE). The aircraft turns ~64°
/// right and starts flying back to REJOY.
///
/// Recording: tests/Yaat.Sim.Tests/TestData/n182ak-ccr-s19r-fawne-iaf-recording.yaat-bug-report-bundle.zip
/// </summary>
public class N182akCcrS19RFawneIafEndToEndTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n182ak-ccr-s19r-fawne-iaf-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("ApproachCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N182ak_CappS19RFromFawne_DoesNotFlyEastBackToRejoy()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay through DCT FAWNE (t=1475) and CAPP S19R (t=1493). Start ticking from t=1494.
        // Recording predates the persisted ValidateDctFixes toggle, so override (matches the
        // SWA5131 E2E test pattern). DCT FAWNE only resolves cleanly when validation is off.
        engine.ReplayWithScenarioOverride(recording, 1494, scenario => scenario.ValidateDctFixes = false);

        var aircraft = engine.FindAircraft("N182AK");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"t=1494 pos=({aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}) "
                + $"hdg={aircraft.TrueHeading.Degrees:F1} alt={aircraft.Altitude:F0}"
        );

        var navPhase = aircraft.Phases?.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault();
        if (navPhase is not null)
        {
            output.WriteLine($"Approach fixes after CAPP: {string.Join(" → ", navPhase.Fixes.Select(f => f.Name))}");
            Assert.DoesNotContain(navPhase.Fixes, f => f.Name == "REJOY");
        }

        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, n => n.Name == "REJOY");

        double startLat = aircraft.Position.Lat;
        double startLon = aircraft.Position.Lon;

        // Tick 120 seconds — long enough for the aircraft to complete its turn onto the
        // 191° magnetic feeder course (~178° true) and start tracking south toward HUKVI.
        // Bug behavior: aircraft would turn ~64° right and fly EAST toward REJOY (9.2 nm
        // east of FAWNE), ending up at higher longitude with heading near 90° true.
        for (int t = 1495; t <= 1615; t++)
        {
            engine.ReplayOneSecond();

            aircraft = engine.FindAircraft("N182AK");
            if (aircraft is null)
            {
                break;
            }

            if (t % 10 == 0)
            {
                string nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"t={t} pos=({aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}) "
                        + $"hdg={aircraft.TrueHeading.Degrees:F0} alt={aircraft.Altitude:F0} next={nextFix}"
                );
            }
        }

        Assert.NotNull(aircraft);
        output.WriteLine($"Final: pos=({aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}) hdg={aircraft.TrueHeading.Degrees:F0}");

        // After the turn, aircraft should be heading south on the published feeder. The
        // FAWNE→HUKVI true bearing is ~208° (191° magnetic at this declination). Allow a
        // generous window because the heading evolves toward the FAC during the leg.
        double finalHdg = aircraft.TrueHeading.Degrees;
        Assert.True(
            finalHdg is >= 160 and <= 240,
            $"Aircraft final heading {finalHdg:F0}° is not in the south-toward-HUKVI range [160°, 240°] — feeder not being flown."
        );

        // Aircraft must NOT have ended up east of its starting longitude. REJOY is at
        // -121.77 (well east); a flight back to REJOY would push longitude up.
        Assert.True(
            aircraft.Position.Lon <= startLon + 0.01,
            $"Aircraft drifted east (start lon {startLon:F4}, end lon {aircraft.Position.Lon:F4}) — flying back to REJOY (bug behavior)."
        );

        // Aircraft must have moved south (toward HUKVI/CCR) — not stayed at FAWNE or moved north.
        Assert.True(
            aircraft.Position.Lat < startLat - 0.005,
            $"Aircraft did not progress south (start lat {startLat:F4}, end lat {aircraft.Position.Lat:F4}) — feeder not being flown."
        );
    }
}
