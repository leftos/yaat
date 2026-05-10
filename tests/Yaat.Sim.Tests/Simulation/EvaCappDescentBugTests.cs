using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for two CAPP descent profile bugs reproduced in
/// `eva-capp-descent-recording` (scenario AS3-NCTB-6 (B) | SFO10).
///
/// EVA18 was issued CAPP twice during the session:
///
///   Bug A — first CAPP at t=894: aircraft was at 4,000 ft (assigned), the
///   cleared approach activated `ApproachNavigationPhase`, but the aircraft
///   stayed level at 4,000 instead of descending continuously toward the
///   glideslope / FAF altitude. AtOrAbove constraints in the approach fix
///   sequence never lower TargetAltitude, so it stays at the highest
///   constraint until reaching a fix with an At/AtOrBelow restriction.
///
///   Bug B — second CAPP at t=1008: aircraft was at 4,000 ft and below the
///   3° glideslope at its slant range. `FinalApproachPhase.OnTick` set
///   TargetAltitude = gsAltitude unconditionally — so the aircraft climbed
///   UP toward the glideslope. Real procedure: hold the assigned altitude
///   until the GS descends to meet from above; never climb to capture.
/// </summary>
public class EvaCappDescentBugTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/eva-capp-descent-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "EVA18";
    private const double AssignedAltitudeFt = 4000.0;

    // First CAPP issued at t=894. By t=910 the aircraft has settled at the
    // assigned 4,000 ft; starting the test there gives a clean precondition
    // (alt ≈ assigned, vs ≈ 0) to assert "should be descending continuously".
    private const int FirstCappElapsedS = 910;

    // Second CAPP issued at t=1008. The recording ends at t=1032 — only 24 s
    // of post-CAPP data — so the aircraft never reaches a state where Bug B's
    // climb-up-to-GS condition fires within the recording window. The Bug B
    // assertion lives in FinalApproachDescentTests.BelowGlideslope_DoesNotCommandClimb,
    // which constructs the precondition synthetically.
    private const int SecondCappElapsedS = 1010;

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
            .EnableCategory("FinalApproachPhase", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("ApproachNavigationPhase", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("InterceptCoursePhase", Microsoft.Extensions.Logging.LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    private static string PhaseSummary(AircraftState ac)
    {
        if (ac.Phases is null || ac.Phases.Phases.Count == 0)
        {
            return "(none)";
        }

        return string.Join(",", ac.Phases.Phases.Select(p => p.Name));
    }

    private void LogState(int t, AircraftState ac)
    {
        var route = ac.Targets.NavigationRoute;
        string nextFix = route.Count > 0 ? route[0].Name : "(none)";
        output.WriteLine(
            $"t={t, 4} alt={ac.Altitude, 6:F0} vs={ac.VerticalSpeed, 6:F0} "
                + $"tgtAlt={ac.Targets.TargetAltitude?.ToString("F0") ?? "null", 6} "
                + $"asgAlt={ac.Targets.AssignedAltitude?.ToString("F0") ?? "null", 6} "
                + $"phase=[{PhaseSummary(ac)}] next={nextFix}"
        );
    }

    /// <summary>
    /// Bug A: After the first CAPP the aircraft is at 4,000 ft (assigned)
    /// well above the glideslope / FAF altitude. It must descend continuously
    /// along the GS profile, not stay level until reaching a fix with an
    /// explicit At/AtOrBelow restriction.
    ///
    /// Tolerance: the aircraft should descend below assigned − 100 ft within
    /// the test window. We do NOT replay subsequent recorded actions
    /// (`TickOneSecond` only) so the t=950 cancel is suppressed.
    /// </summary>
    [Fact]
    public void FirstCapp_DescendsBelowAssignedAltitude()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, FirstCappElapsedS);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        double startAlt = aircraft.Altitude;
        double assigned = aircraft.Targets.AssignedAltitude ?? AssignedAltitudeFt;
        output.WriteLine($"start: alt={startAlt:F0} assigned={assigned:F0} phases=[{PhaseSummary(aircraft)}]");

        Assert.True(
            Math.Abs(startAlt - assigned) <= 100,
            $"precondition: aircraft should start at assigned, was {startAlt:F0} (assigned {assigned:F0})"
        );

        // Tight window: a continuous-descent approach should already be losing altitude
        // within a minute of CAPP, well before the aircraft reaches the FAF. The pre-fix
        // behavior holds 4,000 ft for ~95 s until APODE's AtOrBelow constraint forces a
        // descent — that is exactly the bug the user is reporting.
        const int windowS = 60;
        const double descentRequiredFt = 100.0;

        double minAlt = startAlt;
        int minAltT = 0;
        for (int t = 1; t <= windowS; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                output.WriteLine($"t={t}: aircraft deleted");
                break;
            }

            if (aircraft.Altitude < minAlt)
            {
                minAlt = aircraft.Altitude;
                minAltT = t;
            }

            if (t % 10 == 0)
            {
                LogState(t, aircraft);
            }
        }

        output.WriteLine($"min alt: {minAlt:F0} ft at t={minAltT}s (assigned {assigned:F0})");

        Assert.True(
            minAlt < assigned - descentRequiredFt,
            $"Aircraft must descend below {assigned - descentRequiredFt:F0} within {windowS}s of CAPP "
                + $"(continuous descent on cleared approach), but lowest altitude was {minAlt:F0} ft at t={minAltT}s"
        );
    }
}
