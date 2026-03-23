using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #72: Aircraft on STARs remain way too high.
/// The descent constraint system is reactive — constraints apply only when
/// reaching each fix, not with look-ahead planning.
///
/// Recording: S3-NCTB-6 (A) | SFO19 — UAL238 has navigationPath "LAANE ALWYS3",
/// onAltitudeProfile=true, starts at FL290. Should descend ahead of STAR
/// constraints to meet crossing restrictions, not wait until passing each fix.
/// </summary>
public class Issue72DescentProfileTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue70-route-following-recording.json";

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
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// UAL238 spawns at t=180 at FL290 on the ALWYS STAR with onAltitudeProfile.
    /// The route should have multiple altitude constraints (including from the
    /// runway transition), and the aircraft should continuously descend through
    /// them — not level off at the first constraint and stay there.
    ///
    /// With look-ahead descent planning, the aircraft should begin descending
    /// toward the NEXT constraint before reaching it, not wait until sequencing
    /// past each fix.
    /// </summary>
    [Fact]
    public void DescentVia_ContinuesDescendingThroughMultipleConstraints()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // UAL238 spawns at t=180
        engine.Replay(recording, 182);

        var aircraft = engine.FindAircraft("UAL238");
        Assert.NotNull(aircraft);

        double spawnAlt = aircraft.Altitude;
        output.WriteLine($"UAL238 at spawn: alt={spawnAlt:F0} StarViaMode={aircraft.StarViaMode}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string constraint = fix.AltitudeRestriction is not null ? $" [alt: {fix.AltitudeRestriction}]" : "";
            output.WriteLine($"    {fix.Name}{constraint}");
        }

        // The route needs multiple altitude constraints to test continuous descent.
        // Without runway-transition inference (#71 fix), only LAANE has a constraint.
        int constraintCount = route.Count(t => t.AltitudeRestriction is not null);
        output.WriteLine($"  Total altitude constraints: {constraintCount}");
        Assert.True(
            constraintCount >= 3,
            $"Route should have >= 3 altitude constraints (common + runway transition legs), but has {constraintCount}. Runway-transition inference may not be working."
        );

        // Tick 300s (5 minutes) — aircraft should descend well below the first constraint
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("UAL238");
            if (aircraft is null)
            {
                break;
            }
        }

        Assert.NotNull(aircraft);

        // After 5 minutes at ~280kt, aircraft covers ~23nm per minute = ~115nm.
        // With continuous DVIA, it should have passed through multiple constraints
        // and be well below FL260 (the first constraint). The bug is it stays at FL260.
        output.WriteLine($"  After 300s: alt={aircraft.Altitude:F0} targetAlt={aircraft.Targets.TargetAltitude} (started at {spawnAlt:F0})");
        Assert.True(
            aircraft.Altitude < 20000,
            $"Aircraft should be well below FL200 after 5min of continuous descent, but is at {aircraft.Altitude:F0}"
        );
    }

    /// <summary>
    /// Log full descent profile of UAL238 over 5 minutes of flight to visualize
    /// whether the aircraft meets crossing restrictions at each fix.
    /// </summary>
    [Fact]
    public void DescentVia_FullDescentProfile()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // UAL238 spawns at t=180
        engine.Replay(recording, 182);

        var aircraft = engine.FindAircraft("UAL238");
        Assert.NotNull(aircraft);

        output.WriteLine($"UAL238 descent profile (spawned at {aircraft.Altitude:F0} ft):");
        output.WriteLine($"{"t", 5} {"Alt", 7} {"TgtAlt", 7} {"VS", 6} {"NextFix", -8} {"RouteFixes", 0}");

        // Tick 300 seconds (5 minutes) to observe descent through STAR fixes
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("UAL238");
            if (aircraft is null)
            {
                output.WriteLine($"  Aircraft deleted at t={t}");
                break;
            }

            if (t % 10 == 0)
            {
                var routeNames = string.Join("→", aircraft.Targets.NavigationRoute.Select(f => f.Name));
                var nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"{t, 5} {aircraft.Altitude, 7:F0} {aircraft.Targets.TargetAltitude?.ToString("F0") ?? "null", 7} {aircraft.VerticalSpeed, 6:F0} {nextFix, -8} {routeNames}"
                );
            }
        }

        // This test primarily provides diagnostic output; the real assertion
        // is in DescentVia_BeginsDescentBeforeConstrainedFix above.
        Assert.NotNull(aircraft);
    }
}
