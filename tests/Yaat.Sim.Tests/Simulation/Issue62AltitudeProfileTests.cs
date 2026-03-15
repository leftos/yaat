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
/// E2E tests for GitHub issue #62: onAltitudeProfile scenario flag should
/// automatically enable StarViaMode (descend via STAR) at spawn.
///
/// Recording: S3-NCTC-2 Area C Sequencing — all aircraft have onAltitudeProfile: true
/// and STARs in their NavigationPath (e.g., "SKIZM EMZOH4.30").
/// </summary>
public class Issue62AltitudeProfileTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue62-altitude-profile-recording.json";

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
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// SWA797 has onAltitudeProfile: true and NavigationPath "SKIZM EMZOH4.30".
    /// After spawn, it should have StarViaMode enabled and ActiveStarId set to EMZOH4.
    /// </summary>
    [Fact]
    public void OnAltitudeProfile_EnablesStarViaMode()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // SWA797 has spawnDelay=60, replay to t=62 so it's alive
        engine.Replay(recording, 62);

        var aircraft = engine.FindAircraft("SWA797");
        Assert.NotNull(aircraft);

        output.WriteLine($"SWA797: StarViaMode={aircraft.StarViaMode}, ActiveStarId={aircraft.ActiveStarId}");
        output.WriteLine($"  Altitude: {aircraft.Altitude:F0}, TargetAlt: {aircraft.Targets.TargetAltitude:F0}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string constraint = fix.AltitudeRestriction is not null ? $" [alt: {fix.AltitudeRestriction}]" : "";
            string speed = fix.SpeedRestriction is not null ? $" [spd: {fix.SpeedRestriction}]" : "";
            output.WriteLine($"    {fix.Name}{constraint}{speed}");
        }

        Assert.True(aircraft.StarViaMode, "StarViaMode should be true when onAltitudeProfile is set");
        Assert.Equal("EMZOH4", aircraft.ActiveStarId);
    }

    /// <summary>
    /// Diagnostic: logs SWA797's altitude profile every 10 seconds for 5 minutes
    /// to visually confirm descent via STAR constraints.
    /// </summary>
    [Fact]
    public void OnAltitudeProfile_DescentLog()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 62);

        var aircraft = engine.FindAircraft("SWA797");
        Assert.NotNull(aircraft);

        output.WriteLine("=== SWA797 Descent Profile (onAltitudeProfile=true, StarViaMode=true) ===");
        output.WriteLine($"{"Time", 6} | {"Altitude", 10} | {"TargetAlt", 10} | {"IAS", 5} | {"NextFix", -10} | {"NextFixAlt", -12}");
        output.WriteLine(new string('-', 70));

        for (int t = 0; t <= 300; t++)
        {
            if (t % 10 == 0)
            {
                aircraft = engine.FindAircraft("SWA797");
                Assert.NotNull(aircraft);

                var route = aircraft.Targets.NavigationRoute;
                string nextFix = route.Count > 0 ? route[0].Name : "(none)";
                string nextFixAlt = route.Count > 0 && route[0].AltitudeRestriction is { } ar ? $"{ar.Altitude1Ft:F0}" : "-";

                output.WriteLine(
                    $"{t, 5}s | {aircraft.Altitude, 10:F0} | {aircraft.Targets.TargetAltitude, 10:F0} | {aircraft.IndicatedAirspeed, 5:F0} | {nextFix, -10} | {nextFixAlt, -12}"
                );
            }

            engine.TickOneSecond();
        }
    }

    /// <summary>
    /// Verify that CIFP altitude constraints are present on route targets
    /// when onAltitudeProfile is true.
    /// </summary>
    [Fact]
    public void OnAltitudeProfile_RouteHasAltitudeConstraints()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 62);

        var aircraft = engine.FindAircraft("SWA797");
        Assert.NotNull(aircraft);

        var route = aircraft.Targets.NavigationRoute;
        int constraintCount = route.Count(t => t.AltitudeRestriction is not null);

        output.WriteLine($"SWA797 route has {constraintCount}/{route.Count} fixes with altitude constraints");

        Assert.True(constraintCount > 0, "Route should have altitude constraints from CIFP when onAltitudeProfile is true");
    }

    /// <summary>
    /// Manual DVIA command at t=1152 on SWA2384 should immediately start descent.
    /// </summary>
    [Fact]
    public void ManualDvia_ImmediatelyStartsDescent()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just after the DVIA command at t=1152
        engine.Replay(recording, 1153);

        var aircraft = engine.FindAircraft("SWA2384");
        if (aircraft is null)
        {
            output.WriteLine("SWA2384 not found at t=1153, skipping");
            return;
        }

        output.WriteLine($"SWA2384: StarViaMode={aircraft.StarViaMode}, ActiveStarId={aircraft.ActiveStarId}");
        output.WriteLine($"  Altitude: {aircraft.Altitude:F0}, TargetAlt: {aircraft.Targets.TargetAltitude:F0}");

        Assert.True(aircraft.StarViaMode, "StarViaMode should be true after DVIA command");

        double altAfterDvia = aircraft.Altitude;

        // Tick 60 seconds — aircraft should be descending
        for (int t = 0; t < 60; t++)
        {
            engine.TickOneSecond();
        }

        aircraft = engine.FindAircraft("SWA2384");
        Assert.NotNull(aircraft);

        output.WriteLine($"  After 60s: Altitude={aircraft.Altitude:F0}, TargetAlt={aircraft.Targets.TargetAltitude:F0}");

        bool isDescending = aircraft.Altitude < altAfterDvia || aircraft.Targets.TargetAltitude < altAfterDvia;
        Assert.True(
            isDescending,
            $"Aircraft should be descending after manual DVIA. Alt: {aircraft.Altitude:F0} → Target: {aircraft.Targets.TargetAltitude:F0}"
        );
    }

    /// <summary>
    /// With StarViaMode active, aircraft should be descending toward STAR constraints
    /// (not staying at cruise altitude indefinitely).
    /// </summary>
    [Fact]
    public void OnAltitudeProfile_AircraftDescends()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // SWA797 starts at 22000, replay far enough for descent to begin
        engine.Replay(recording, 62);

        var aircraft = engine.FindAircraft("SWA797");
        Assert.NotNull(aircraft);

        double initialAlt = aircraft.Altitude;
        output.WriteLine($"SWA797 initial altitude: {initialAlt:F0}");

        // Tick 120 seconds — with via mode active, aircraft should start descending
        for (int t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
        }

        aircraft = engine.FindAircraft("SWA797");
        Assert.NotNull(aircraft);

        output.WriteLine($"SWA797 altitude after 120s: {aircraft.Altitude:F0}");
        output.WriteLine($"SWA797 target altitude: {aircraft.Targets.TargetAltitude:F0}");

        // Aircraft should either be descending or have a target altitude below initial
        bool isDescending = aircraft.Altitude < initialAlt || aircraft.Targets.TargetAltitude < initialAlt;
        Assert.True(
            isDescending,
            $"Aircraft should be descending with STAR via mode. Alt: {aircraft.Altitude:F0}, Target: {aircraft.Targets.TargetAltitude:F0}, Initial: {initialAlt:F0}"
        );
    }
}
