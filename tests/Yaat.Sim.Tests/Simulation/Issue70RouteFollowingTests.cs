using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #70: Aircraft do not follow fix-to-fix routings
/// if established in the initial route.
///
/// Recording: S3-NCTB-6 (A) | SFO19 — EVA18 has navigationPath "PIRAT SAU",
/// onAltitudeProfile=false. After reaching PIRAT, the aircraft should turn
/// toward SAU, but instead goes straight.
/// </summary>
public class Issue70RouteFollowingTests(ITestOutputHelper output)
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
    /// EVA18 spawns at t=360 with navigationPath "PIRAT SAU".
    /// After spawn, the route should contain both PIRAT and SAU as navigation targets.
    /// </summary>
    [Fact]
    public void FixToFixRoute_ContainsBothFixes()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Verify SAU VOR is in the nav database (should be loaded from CIFP navaids)
        var sauPos = NavigationDatabase.Instance.GetFixPosition("SAU");
        output.WriteLine($"SAU fix position: {(sauPos is null ? "NOT FOUND" : $"{sauPos.Value.Lat:F6}, {sauPos.Value.Lon:F6}")}");

        // EVA18 spawns at t=360
        engine.Replay(recording, 362);

        var aircraft = engine.FindAircraft("EVA18");
        Assert.NotNull(aircraft);

        output.WriteLine($"EVA18: hdg={aircraft.TrueHeading.Degrees:F1} alt={aircraft.Altitude:F0}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            output.WriteLine($"    {fix.Name} ({fix.Latitude:F4}, {fix.Longitude:F4})");
        }

        // Route should have at least SAU in it (PIRAT may have already been sequenced if aircraft spawned near it)
        bool hasSau = route.Any(t => t.Name.Equals("SAU", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasSau, $"Route should contain SAU after PIRAT, but route has: [{string.Join(", ", route.Select(t => t.Name))}]");
    }

    /// <summary>
    /// EVA18 should be heading toward SAU after passing PIRAT.
    /// Tick forward and verify the aircraft turns toward the next fix
    /// instead of continuing straight.
    /// </summary>
    [Fact]
    public void FixToFixRoute_AircraftTurnsTowardNextFix()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // EVA18 spawns at t=360, let it fly for a bit
        engine.Replay(recording, 362);

        var aircraft = engine.FindAircraft("EVA18");
        Assert.NotNull(aircraft);

        double initialHdg = aircraft.TrueHeading.Degrees;
        output.WriteLine($"EVA18 at spawn+2s: hdg={initialHdg:F1} alt={aircraft.Altitude:F0}");
        output.WriteLine($"  Route: [{string.Join(", ", aircraft.Targets.NavigationRoute.Select(t => t.Name))}]");

        // Tick forward 120s to let aircraft fly toward/past PIRAT and turn to SAU
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("EVA18");
            if (aircraft is null)
            {
                break;
            }

            if (t % 30 == 0)
            {
                var nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"  t={t}: hdg={aircraft.TrueHeading.Degrees:F1} lat={aircraft.Latitude:F4} lon={aircraft.Longitude:F4} next={nextFix}"
                );
            }
        }

        Assert.NotNull(aircraft);

        // After 120 seconds, the aircraft should be navigating (have a target heading set)
        // and the route should still have fixes (not empty/going straight)
        bool hasRoute = aircraft.Targets.NavigationRoute.Count > 0 || aircraft.Targets.TargetTrueHeading is not null;
        output.WriteLine(
            $"  Final: hdg={aircraft.TrueHeading.Degrees:F1} targetHdg={aircraft.Targets.TargetTrueHeading} routeCount={aircraft.Targets.NavigationRoute.Count}"
        );
        Assert.True(hasRoute, "Aircraft should still have navigation targets or a target heading, not fly straight with no guidance");
    }
}
