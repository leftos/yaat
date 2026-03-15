using System.Text;
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
/// E2E replay tests for GitHub issue #58: JSTAR intermediate fix joining and
/// scenario NavigationPath parsing.
///
/// Two recordings:
/// 1. issue58-jstar-intermediate-fix-recording.json — JARR EMZOH4 SKIZM issued manually
/// 2. issue58-star-180-recording.json — scenario aircraft with pre-assigned STARs in NavigationPath
///    (e.g., "SKIZM EMZOH4.30") that cause 180° turns because PopulateNavigationRoute
///    strips the STAR name to a fix and creates a backwards route.
/// </summary>
public class Issue58JstarIntermediateFixTests(ITestOutputHelper output)
{
    private static SessionRecording LoadRecording(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private SimulationEngine BuildEngine()
    {
        var navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// JARR EMZOH4 SKIZM issued at t=0: SKIZM is on the STAR's common legs (not a transition).
    /// The aircraft should join from SKIZM onward, not fly back to EMZOH.
    /// </summary>
    [Fact]
    public void Jstar_IntermediateFix_NoHeadingReversal()
    {
        var recording = LoadRecording("TestData/issue58-jstar-intermediate-fix-recording.json");
        var engine = BuildEngine();

        engine.Replay(recording, 1);

        var aircraft = engine.FindAircraft("KFB7");
        Assert.NotNull(aircraft);

        LogAircraftState(aircraft, "KFB7 after JARR EMZOH4 SKIZM (t=1)");
        AssertFirstFixAhead(aircraft);
        AssertNoHeadingReversal(engine, "KFB7", aircraft.Heading, 30);
    }

    /// <summary>
    /// Scenario aircraft SWA797 has NavigationPath "SKIZM EMZOH4.30".
    /// Current bug: PopulateNavigationRoute strips "EMZOH4.30" → "EMZOH", creating route
    /// [SKIZM, EMZOH]. On the STAR the order is EMZOH→MYJAW→SKIZM (inbound), so after
    /// reaching SKIZM the aircraft turns 180° to go back to EMZOH.
    /// </summary>
    [Fact]
    public void ScenarioNavPath_StarToken_NoBackwardsRoute()
    {
        var recording = LoadRecording("TestData/issue58-star-180-recording.json");
        var engine = BuildEngine();

        // SWA797 spawns after t=1; replay to t=120 so it exists and has been flying
        engine.Replay(recording, 120);

        var aircraft = engine.FindAircraft("SWA797");
        Assert.NotNull(aircraft);

        LogAircraftState(aircraft, "SWA797 after scenario load (NavigationPath: SKIZM EMZOH4.30)");

        // The route should NOT contain EMZOH as a fix after SKIZM.
        // If the STAR token was correctly handled, the route should end at SKIZM
        // (or continue with runway transition fixes), not go backwards to EMZOH.
        var route = aircraft.Targets.NavigationRoute;
        int skizmIdx = -1;
        int emzohIdx = -1;
        for (int i = 0; i < route.Count; i++)
        {
            if (route[i].Name == "SKIZM" && skizmIdx == -1)
            {
                skizmIdx = i;
            }

            if (route[i].Name == "EMZOH" && emzohIdx == -1)
            {
                emzohIdx = i;
            }
        }

        output.WriteLine($"SKIZM at index {skizmIdx}, EMZOH at index {emzohIdx}");

        // EMZOH should not appear after SKIZM (that would be backwards)
        Assert.False(
            skizmIdx >= 0 && emzohIdx > skizmIdx,
            $"Route has EMZOH (idx {emzohIdx}) after SKIZM (idx {skizmIdx}) — backwards STAR route. "
                + $"Route: {string.Join(" ", route.Select(t => t.Name))}"
        );

        // Also tick for 60 seconds to verify no 180 turn as aircraft progresses
        AssertNoHeadingReversal(engine, "SWA797", aircraft.Heading, 60);
    }

    private void LogAircraftState(AircraftState aircraft, string header)
    {
        var navRoute = aircraft.Targets.NavigationRoute;
        var log = new StringBuilder();
        log.AppendLine($"=== {header} ===");
        log.AppendLine($"Position: {aircraft.Latitude:F4}, {aircraft.Longitude:F4}");
        log.AppendLine($"Heading: {aircraft.Heading:F1}");
        log.AppendLine($"Altitude: {aircraft.Altitude:F0}");
        log.AppendLine($"Nav route ({navRoute.Count} fixes):");
        foreach (var fix in navRoute)
        {
            log.AppendLine($"  {fix.Name} ({fix.Latitude:F4}, {fix.Longitude:F4})");
        }

        if (navRoute.Count > 0)
        {
            double bearingToFirst = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, navRoute[0].Latitude, navRoute[0].Longitude);
            double angleDiff = NormalizeAngleDiff(bearingToFirst - aircraft.Heading);
            log.AppendLine($"Bearing to first fix ({navRoute[0].Name}): {bearingToFirst:F1}°, {angleDiff:F1}° off nose");
        }

        output.WriteLine(log.ToString());
    }

    private void AssertFirstFixAhead(AircraftState aircraft)
    {
        var navRoute = aircraft.Targets.NavigationRoute;
        if (navRoute.Count == 0)
        {
            return;
        }

        double bearingToFirst = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, navRoute[0].Latitude, navRoute[0].Longitude);
        double angleDiff = NormalizeAngleDiff(bearingToFirst - aircraft.Heading);
        Assert.True(angleDiff < 90, $"First nav fix {navRoute[0].Name} is {angleDiff:F0}° off nose — behind the aircraft, will cause 180° reversal.");
    }

    private void AssertNoHeadingReversal(SimulationEngine engine, string callsign, double initialHeading, int seconds)
    {
        var log = new StringBuilder();
        log.AppendLine("Tick | Heading | Lat | Lon | NextFix");

        double prevHeading = initialHeading;
        double maxDelta = 0;

        for (int t = 1; t <= seconds; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            string nextFix = ac.Targets.NavigationRoute.Count > 0 ? ac.Targets.NavigationRoute[0].Name : "(none)";
            log.AppendLine($"  {t, 3} | {ac.Heading, 7:F1} | {ac.Latitude:F4} | {ac.Longitude:F4} | {nextFix}");

            double delta = NormalizeAngleDiff(ac.Heading - prevHeading);
            if (delta > maxDelta)
            {
                maxDelta = delta;
            }

            prevHeading = ac.Heading;
        }

        log.AppendLine($"Max single-tick heading change: {maxDelta:F1}°");
        output.WriteLine(log.ToString());

        var aircraft = engine.FindAircraft(callsign);
        Assert.NotNull(aircraft);

        double totalChange = NormalizeAngleDiff(aircraft.Heading - initialHeading);
        Assert.True(totalChange < 120, $"{callsign} made a {totalChange:F0}° turn — likely a 180° reversal. See test output.");
    }

    private static double NormalizeAngleDiff(double diff)
    {
        diff = ((diff % 360) + 360) % 360;
        return diff > 180 ? 360 - diff : diff;
    }
}
