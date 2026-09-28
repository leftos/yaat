using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
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
/// Guards two bugs: the aircraft routing to FAWNE and skipping the course reversal because PI
/// legs were dropped, and the PT's inbound stage flying a parallel offset east of the course
/// because it matched the inbound heading without steering onto the course line.
///
/// Recording: tests/Yaat.Sim.Tests/TestData/b143fc615682.zip
/// </summary>
public class IssueSwa5131PtKccrS19REndToEndTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/b143fc615682.zip";

    // ProcedureTurnPhase.InterceptLateralToleranceNm: the band the PT itself treats as established inbound. Measured
    // against the course line through the CCR VOR; FinalApproachPhase tracks a line anchored at the runway, which
    // sits ~0.19 nm west of the VOR here, so the approach phases' 0.15 nm band cannot be applied to this line.
    private const double InboundCrossTrackToleranceNm = 1.0;

    private const double ConvergedCrossTrackNm = 0.35;

    private const int SustainedOnCourseSeconds = 60;

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
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        NavigationDatabase navDb = TestVnasData.NavigationDb!;
        (double Lat, double Lon)? ccrPos = navDb.GetFixPosition("CCR");
        Assert.NotNull(ccrPos);
        double ccrLat = ccrPos.Value.Lat;
        double ccrLon = ccrPos.Value.Lon;

        // Replay through the CAPP S19R command at t=1601 (and the DCT CCR before it at t=1589).
        // Start ticking from t=1602.
        // The original session had DCT-fix validation OFF, but recordings made before the
        // ValidateDctFixes setting was persisted (see SimControlService.SetValidateDctFixes)
        // do not carry that toggle, so we override it explicitly here.
        engine.ReplayWithScenarioOverride(recording, 1602, scenario => scenario.ValidateDctFixes = false);

        AircraftState? aircraft = engine.FindAircraft("SWA5131");
        Assert.NotNull(aircraft);

        // Confirm CAPP wired the PT phase rather than the implied-PTAC InterceptCoursePhase.
        Assert.NotNull(aircraft.Phases);
        ProcedureTurnPhase pt = Assert.Single(aircraft.Phases.Phases.OfType<ProcedureTurnPhase>());

        // The inbound course is the CIFP final approach course the PT was built with, anchored at the CCR VOR.
        var inboundCourse = new TrueHeading(pt.InboundCourseDeg);
        var ptFix = new LatLon(pt.FixLat, pt.FixLon);
        Assert.True(GeoMath.DistanceNm(ptFix, new LatLon(ccrLat, ccrLon)) < 0.05, $"PT anchor ({pt.FixLat:F5},{pt.FixLon:F5}) is not the CCR VOR");
        output.WriteLine(
            $"PT: inbound={pt.InboundCourseDeg:F1}T ptOut={pt.PtOutboundCourseDeg:F1}T aircraftDecl={aircraft.Declination:F2} "
                + $"KCCR magvar={navDb.GetAirportMagneticVariation("KCCR")?.ToString("F2") ?? "null"} "
                + $"CCR magvar={navDb.GetAirportMagneticVariation("CCR")?.ToString("F2") ?? "null"}"
        );

        bool crossedCcr = false;
        int tCrossedCcr = -1;
        bool wentOutbound011 = false;
        double minAltAfterCross = double.MaxValue;
        int tLeftPt = -1;
        string phaseAfterPt = "(none)";
        int secondsOnCourse = 0;
        double minAbsXteAfterTurn = double.MaxValue;
        bool establishedInbound = false;

        // Drive the simulation, not the recording: the instructor's later DCT CCR (t=1839) and bare CAPP (t=1849)
        // reacted to the old, wrong PT geometry and would cancel the procedure turn under replay.
        for (int t = 1603; t <= 2600; t++)
        {
            engine.TickOneSecond();

            aircraft = engine.FindAircraft("SWA5131");
            if (aircraft is null)
            {
                break;
            }

            double distFromCcrNm = GeoMath.DistanceNm(aircraft.Position, new LatLon(ccrLat, ccrLon));
            double xteNm = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, ptFix, inboundCourse);
            Phase? activePhase = aircraft.Phases?.CurrentPhase;

            if (!crossedCcr && (distFromCcrNm <= 1.5))
            {
                crossedCcr = true;
                tCrossedCcr = t;
                output.WriteLine($"t={t}: crossed CCR (dist={distFromCcrNm:F2} nm)");
            }

            if (crossedCcr)
            {
                // The floor holds during the procedure turn; the approach descent that follows it is not a PT altitude.
                if ((activePhase is ProcedureTurnPhase) && (aircraft.Altitude < minAltAfterCross))
                {
                    minAltAfterCross = aircraft.Altitude;
                }

                double hdg = aircraft.TrueHeading.Degrees;
                double trueOutbound = (191.0 - aircraft.Declination + 180.0) % 360.0; // ≈ outbound radial true
                double diffOutbound = AbsAngleDiff(hdg, trueOutbound);
                if (!wentOutbound011 && (diffOutbound <= 20.0))
                {
                    wentOutbound011 = true;
                    output.WriteLine($"t={t}: established outbound (hdg={hdg:F0}, target={trueOutbound:F0})");
                }

                if (wentOutbound011 && (tLeftPt < 0) && (activePhase is not ProcedureTurnPhase) && (t > tCrossedCcr + 60))
                {
                    tLeftPt = t;
                    phaseAfterPt = activePhase?.GetType().Name ?? "(none)";
                    output.WriteLine($"t={t}: left ProcedureTurnPhase into {phaseAfterPt} (xte={xteNm:F2} nm, hdg={hdg:F0})");
                }

                // A real intercept: after the PT hands off, the aircraft holds the inbound course laterally,
                // flown by an approach phase, for a sustained stretch — not a heading that merely matches it.
                if ((tLeftPt > 0) && !establishedInbound)
                {
                    minAbsXteAfterTurn = Math.Min(minAbsXteAfterTurn, Math.Abs(xteNm));
                    bool onCourse = (Math.Abs(xteNm) <= InboundCrossTrackToleranceNm) && IsApproachPhase(activePhase);
                    secondsOnCourse = onCourse ? secondsOnCourse + 1 : 0;
                    if (secondsOnCourse >= SustainedOnCourseSeconds)
                    {
                        establishedInbound = true;
                        output.WriteLine($"t={t}: established inbound (xte={xteNm:F3} nm for {secondsOnCourse}s, hdg={hdg:F0})");
                    }
                }
            }

            if (t % 10 == 0)
            {
                string nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"t={t} pos=({aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}) "
                        + $"hdg={aircraft.TrueHeading.Degrees:F0} alt={aircraft.Altitude:F0} "
                        + $"vs={aircraft.VerticalSpeed:F0} dist={distFromCcrNm:F2} xte={xteNm:F2} "
                        + $"phase={activePhase?.GetType().Name ?? "(none)"} next={nextFix}"
                );
            }

            if (establishedInbound)
            {
                break;
            }
        }

        Assert.True(crossedCcr, "SWA5131 never crossed within 1.5 nm of CCR");
        Assert.True(wentOutbound011, "SWA5131 never went outbound on the FAC reciprocal (~011° magnetic)");
        Assert.True(minAltAfterCross >= 2500, $"Aircraft descended below 2500 ft during PT (min={minAltAfterCross:F0})");
        Assert.True(tLeftPt > 0, "SWA5131 never left ProcedureTurnPhase after going outbound");
        Assert.True(
            establishedInbound,
            $"SWA5131 never held the inbound course within {InboundCrossTrackToleranceNm} nm for {SustainedOnCourseSeconds}s after the PT "
                + $"(left PT at t={tLeftPt} into {phaseAfterPt}, closest {minAbsXteAfterTurn:F2} nm)"
        );

        // The band above is the PT's own hand-off gate, so passing it proves only the hand-off. Converging proves the
        // intercept: the aircraft closes to the course line (the runway-anchored final sits ~0.19 nm from it).
        Assert.True(
            minAbsXteAfterTurn <= ConvergedCrossTrackNm,
            $"SWA5131 never converged on the inbound course after the PT: closest {minAbsXteAfterTurn:F2} nm, expected <= {ConvergedCrossTrackNm} nm"
        );
    }

    private static bool IsApproachPhase(Phase? phase) => phase is ApproachNavigationPhase or InterceptCoursePhase or FinalApproachPhase;

    private static double AbsAngleDiff(double a, double b)
    {
        double d = Math.Abs(((a - b + 540.0) % 360.0) - 180.0);
        return d;
    }
}
