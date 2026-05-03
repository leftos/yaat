using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E recording replay for SWA5131 on the SOFIA TWO VOR/DME RWY 19R approach at KCCR.
///
/// At t=1601 the controller issued CAPP S19R; SWA5131 is south of CCR (37.88, -122.28)
/// heading ~041° at ~6000 ft. Published procedure requires a course reversal at CCR:
/// outbound 011 (FAC reciprocal), climb to ≥2600, 45°/180° procedure turn, intercept
/// inbound on FAC 191° magnetic, continue to RW19R.
///
/// Bug: today the aircraft routes to FAWNE (NE of CCR) and bypasses the course reversal
/// because PI legs are silently dropped in BuildFixesFromLegs.
///
/// Recording: tests/Yaat.Sim.Tests/TestData/swa5131-ccr-s19r-pt-recording.yaat-bug-report-bundle.zip
/// </summary>
public class IssueSwa5131PtKccrS19REndToEndTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/swa5131-ccr-s19r-pt-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("ApproachCommandHandler", LogLevel.Debug)
            .EnableCategory("ProcedureTurnPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Swa5131_PtAtCcr_FlysOutboundClimbsAndInterceptsInbound()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var navDb = TestVnasData.NavigationDb!;
        var ccrPos = navDb.GetFixPosition("CCR");
        Assert.NotNull(ccrPos);
        double ccrLat = ccrPos.Value.Lat;
        double ccrLon = ccrPos.Value.Lon;

        // Replay through the CAPP S19R command at t=1601 (and the DCT CCR before it at t=1589).
        // Start ticking from t=1602.
        // The original session had DCT-fix validation OFF, but recordings made before the
        // ValidateDctFixes setting was persisted (see SimControlService.SetValidateDctFixes)
        // do not carry that toggle, so we override it explicitly here.
        engine.ReplayWithScenarioOverride(recording, 1602, scenario => scenario.ValidateDctFixes = false);

        var aircraft = engine.FindAircraft("SWA5131");
        Assert.NotNull(aircraft);

        // Confirm CAPP wired the PT phase rather than the implied-PTAC InterceptCoursePhase.
        Assert.NotNull(aircraft.Phases);
        Assert.Contains(aircraft.Phases.Phases, p => p is Yaat.Sim.Phases.Approach.ProcedureTurnPhase);

        bool crossedCcr = false;
        int tCrossedCcr = -1;
        bool wentOutbound011 = false;
        double minAltAfterCross = double.MaxValue;
        bool establishedInbound = false;
        int tEstablished = -1;

        for (int t = 1603; t <= 2130; t++)
        {
            engine.ReplayOneSecond();

            aircraft = engine.FindAircraft("SWA5131");
            if (aircraft is null)
            {
                break;
            }

            double distFromCcrNm = GeoMath.DistanceNm(aircraft.Position, new LatLon(ccrLat, ccrLon));

            if (!crossedCcr && distFromCcrNm <= 1.5)
            {
                crossedCcr = true;
                tCrossedCcr = t;
                output.WriteLine($"t={t}: crossed CCR (dist={distFromCcrNm:F2} nm)");
            }

            if (crossedCcr)
            {
                if (aircraft.Altitude < minAltAfterCross)
                {
                    minAltAfterCross = aircraft.Altitude;
                }

                double hdg = aircraft.TrueHeading.Degrees;
                double trueOutbound = (191.0 - aircraft.Declination + 180.0) % 360.0; // ≈ outbound radial true
                double diffOutbound = AbsAngleDiff(hdg, trueOutbound);
                if (!wentOutbound011 && diffOutbound <= 20.0)
                {
                    wentOutbound011 = true;
                    output.WriteLine($"t={t}: established outbound (hdg={hdg:F0}, target={trueOutbound:F0})");
                }

                double trueInbound = (191.0 - aircraft.Declination + 360.0) % 360.0; // ≈ inbound FAC true
                double diffInbound = AbsAngleDiff(hdg, trueInbound);
                bool onInboundSide = aircraft.Position.Lat <= ccrLat + 0.02; // at-or-south of CCR
                if (wentOutbound011 && !establishedInbound && diffInbound <= 15.0 && onInboundSide && t > tCrossedCcr + 60)
                {
                    establishedInbound = true;
                    tEstablished = t;
                    output.WriteLine($"t={t}: established inbound (hdg={hdg:F0}, target={trueInbound:F0}, lat={aircraft.Position.Lat:F4})");
                }
            }

            if (t % 10 == 0)
            {
                string activePhase = aircraft.Phases?.Phases.ElementAtOrDefault(aircraft.Phases.CurrentIndex)?.GetType().Name ?? "(none)";
                string nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"t={t} pos=({aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}) "
                        + $"hdg={aircraft.TrueHeading.Degrees:F0} alt={aircraft.Altitude:F0} "
                        + $"vs={aircraft.VerticalSpeed:F0} dist={distFromCcrNm:F2} "
                        + $"phase={activePhase} next={nextFix}"
                );
            }
        }

        Assert.True(crossedCcr, "SWA5131 never crossed within 1.5 nm of CCR");
        Assert.True(wentOutbound011, "SWA5131 never went outbound on the FAC reciprocal (~011° magnetic)");
        Assert.True(minAltAfterCross >= 2500, $"Aircraft descended below 2500 ft during PT (min={minAltAfterCross:F0})");
        Assert.True(establishedInbound, "SWA5131 never established inbound on FAC south of CCR after the procedure turn");
    }

    private static double AbsAngleDiff(double a, double b)
    {
        double d = Math.Abs(((a - b + 540.0) % 360.0) - 180.0);
        return d;
    }
}
