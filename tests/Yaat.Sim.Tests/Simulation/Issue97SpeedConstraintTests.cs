using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #97: Speed constraint look-ahead planning.
///
/// Recording: S3-MTN-O1 (A) Area E NV RNOS — SWA11 (B738, KPHX→KRNO) on SCOLA1 STAR.
/// Command "AT KLOCK CAPP I17R" issued at t=71s.
///
/// Bug: Speed constraints on STAR/approach fixes are applied reactively (only after
/// sequencing past a fix), so the aircraft is still at cruise speed when it reaches
/// a speed-constrained fix. These tests verify that look-ahead planning decelerates
/// the aircraft proactively so it meets constraints at the fix.
/// </summary>
public class Issue97SpeedConstraintTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue95-capp-ambiguity-recording.json";

    /// <summary>Tolerance for IAS at a max-speed fix (kt). Look-ahead should keep IAS within this band.</summary>
    private const double SpeedToleranceKts = 20.0;

    private static SessionRecording? LoadRecording()
    {
        if (!File.Exists(RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

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

    /// <summary>
    /// After the recorded `AT KLOCK CAPP I17R` fires at t=76, SWA11 flies the rest of the
    /// SCOLA1 STAR + I17R approach into KRNO. At each fix that carries a max speed restriction,
    /// IAS at sequencing should be within tolerance of the published value (look-ahead
    /// deceleration). The aircraft should reach the FAF and complete the landing.
    /// </summary>
    [Fact]
    public void SWA11_MeetsSpeedConstraintsAtFixes()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay to t=71. The recording already contains `AT KLOCK CAPP I17R`
        // which fires when KLOCK is sequenced; no need to issue CAPP manually.
        engine.Replay(recording, 71);

        var aircraft = engine.FindAircraft("SWA11");
        Assert.NotNull(aircraft);

        // Snapshot speed constraints on the entire planned route, keyed by fix name. We capture
        // them now and again as new fixes appear (CAPP I17R rebuilds the route mid-flight).
        var constraintsByFix = new Dictionary<string, CifpSpeedRestriction>(StringComparer.OrdinalIgnoreCase);
        CaptureConstraints(aircraft, constraintsByFix);

        using var _ = TickRecorder.Attach(engine, Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "swa11-trajectory.json"), "SWA11");

        var iasAtFix = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? lastNavFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : null;

        // Tick up to 900 sim-seconds from t=72 (approach completes well within this window).
        for (int t = 0; t < 900; t++)
        {
            engine.ReplayOneSecond();
            CaptureConstraints(aircraft, constraintsByFix);

            string? currentFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : null;
            if (lastNavFix is not null && currentFix != lastNavFix)
            {
                iasAtFix[lastNavFix] = aircraft.IndicatedAirspeed;
                output.WriteLine(
                    $"  t={72 + t}: sequenced past {lastNavFix}, IAS={aircraft.IndicatedAirspeed:F0} alt={aircraft.Altitude:F0} "
                        + $"phase={aircraft.Phases?.CurrentPhase?.Name ?? "none"}"
                );
            }

            lastNavFix = currentFix;

            if (aircraft.IsOnGround)
            {
                break;
            }
        }

        Assert.True(aircraft.IsOnGround, $"Aircraft did not land within 900s. Final IAS={aircraft.IndicatedAirspeed:F0} alt={aircraft.Altitude:F0}");
        Assert.True(aircraft.IndicatedAirspeed < 200, $"Final IAS {aircraft.IndicatedAirspeed:F0} too high — approach didn't slow the aircraft down");

        // For each fix that has a max-speed restriction and that we actually sequenced,
        // assert IAS at sequencing was within tolerance.
        var failures = new List<string>();
        foreach (var (fix, ias) in iasAtFix)
        {
            if (!constraintsByFix.TryGetValue(fix, out var restr) || !restr.IsMaximum)
            {
                continue;
            }

            double cap = restr.SpeedKts + SpeedToleranceKts;
            if (ias > cap)
            {
                failures.Add($"{fix}: IAS={ias:F0} exceeded {restr.SpeedKts}kt+{SpeedToleranceKts:F0}kt tolerance");
            }
        }

        Assert.True(failures.Count == 0, "Speed-constrained fixes violated:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Speed look-ahead should start decelerating the aircraft before reaching a STAR fix
    /// with a speed constraint. Replay from t=50 to t=70 and verify IAS strictly decreases
    /// as the aircraft approaches CHIME (the first speed-restricted fix on SCOLA1).
    /// </summary>
    [Fact]
    public void SWA11_StarSpeedConstraintDeceleratesBeforeFix()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 50);

        var aircraft = engine.FindAircraft("SWA11");
        Assert.NotNull(aircraft);

        output.WriteLine($"At t=50: IAS={aircraft.IndicatedAirspeed:F0} alt={aircraft.Altitude:F0}");
        output.WriteLine($"NavRoute: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(n => n.Name))}");

        bool routeHasSpeedConstraint = aircraft.Targets.NavigationRoute.Any(n => n.SpeedRestriction is { IsMaximum: true });
        Assert.True(routeHasSpeedConstraint, "Test precondition: at least one nav-route fix must have a max speed constraint at t=50");

        double initialIas = aircraft.IndicatedAirspeed;
        bool sawDeceleration = false;

        for (int t = 51; t <= 70; t++)
        {
            engine.ReplayOneSecond();
            output.WriteLine($"  t={t}: IAS={aircraft.IndicatedAirspeed:F0} alt={aircraft.Altitude:F0} targetSpd={aircraft.Targets.TargetSpeed}");
            if (aircraft.IndicatedAirspeed < initialIas - 5)
            {
                sawDeceleration = true;
            }
        }

        Assert.True(
            sawDeceleration,
            $"Aircraft did not decelerate at all between t=51 and t=70 despite a downstream max-speed restriction (initial IAS {initialIas:F0})"
        );
    }

    /// <summary>
    /// Capture every nav-route fix's max speed restriction. CAPP I17R rebuilds the route
    /// mid-flight, so we accumulate constraints across calls — the I17R approach fixes
    /// only become visible after KLOCK is sequenced.
    /// </summary>
    private static void CaptureConstraints(AircraftState aircraft, Dictionary<string, CifpSpeedRestriction> constraintsByFix)
    {
        foreach (var nav in aircraft.Targets.NavigationRoute)
        {
            if (nav.SpeedRestriction is { } spd)
            {
                constraintsByFix.TryAdd(nav.Name, spd);
            }
        }
    }
}
