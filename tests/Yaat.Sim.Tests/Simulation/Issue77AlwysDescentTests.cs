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
/// E2E tests for GitHub issue #77: Aircraft on the ALWYS3 arrival at KSFO
/// remain high and do not cross BERKS at 5000ft.
///
/// Recording: S3-NCTB-6 (A) | SFO19 — multiple aircraft on ALWYS3 with
/// onAltitudeProfile=true. BERKS has an altitude constraint (cross at 5000)
/// in the RW19B runway transition, but aircraft stay too high.
/// </summary>
public class Issue77AlwysDescentTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue77-alwys-descent-recording.json";

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
    /// SKW5456 spawns at t=660 on ALWYS3 with onAltitudeProfile=true at FL290.
    /// The route should include runway transition fixes (HEFLY, ARRTU, ADDMM,
    /// COGGR, BERKS) with altitude constraints — including BERKS at 5000.
    /// </summary>
    [Fact]
    public void Alwys3_RouteHasBerksConstraint()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // SKW5456 spawns at t=660
        engine.Replay(recording, 662);

        var aircraft = engine.FindAircraft("SKW5456");
        Assert.NotNull(aircraft);

        output.WriteLine($"SKW5456: StarViaMode={aircraft.StarViaMode}, ActiveStarId={aircraft.ActiveStarId}");
        output.WriteLine($"  Altitude: {aircraft.Altitude:F0}, TargetAlt: {aircraft.Targets.TargetAltitude}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string constraint = fix.AltitudeRestriction is not null ? $" [alt: {fix.AltitudeRestriction}]" : "";
            string speed = fix.SpeedRestriction is not null ? $" [spd: {fix.SpeedRestriction}]" : "";
            output.WriteLine($"    {fix.Name}{constraint}{speed}");
        }

        // BERKS must be in the route with an altitude constraint
        var berks = route.FirstOrDefault(t => t.Name.Equals("BERKS", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(berks);
        Assert.NotNull(berks.AltitudeRestriction);
        output.WriteLine($"  BERKS constraint: {berks.AltitudeRestriction}");
    }

    /// <summary>
    /// SKW5456 on ALWYS3 must descend to cross ARRTU at 10000ft.
    /// ARRTU has an "At 10000" constraint in the ALWYS3 RW19B transition.
    /// </summary>
    [Fact]
    public void Alwys3_CrossesArrtuAt10000()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 662);

        var aircraft = engine.FindAircraft("SKW5456");
        Assert.NotNull(aircraft);

        output.WriteLine($"SKW5456 at spawn: alt={aircraft.Altitude:F0} StarViaMode={aircraft.StarViaMode}");
        output.WriteLine($"  Route: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(f => f.Name))}");

        double altAtArrtu = -1;
        bool arrtuSequenced = false;
        for (int t = 1; t <= 2400; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SKW5456");
            if (aircraft is null)
            {
                break;
            }

            bool hasArrtu = aircraft.Targets.NavigationRoute.Any(f => f.Name.Equals("ARRTU", StringComparison.OrdinalIgnoreCase));
            if (!hasArrtu && !arrtuSequenced)
            {
                arrtuSequenced = true;
                altAtArrtu = aircraft.Altitude;
                output.WriteLine($"  ARRTU sequenced at t={t}: alt={altAtArrtu:F0}");
            }

            if (t % 60 == 0 && !arrtuSequenced)
            {
                var nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"  t={t, 4} alt={aircraft.Altitude, 7:F0} tgtAlt={aircraft.Targets.TargetAltitude?.ToString("F0") ?? "null", 7} VS={aircraft.VerticalSpeed, 6:F0} next={nextFix}"
                );
            }

            if (arrtuSequenced)
            {
                break;
            }
        }

        Assert.True(arrtuSequenced, "ARRTU was never sequenced");
        Assert.True(altAtArrtu is >= 9850 and <= 10150, $"Aircraft should cross ARRTU at ~10000ft (±150 tolerance), but was at {altAtArrtu:F0}ft");
    }

    /// <summary>
    /// SKW5456 on ALWYS3 must descend to cross BERKS at or below 5000ft.
    /// The aircraft starts at FL290 and has onAltitudeProfile=true (auto-DVIA).
    /// Tick until BERKS is sequenced and verify altitude.
    /// </summary>
    [Fact]
    public void Alwys3_CrossesBerksAtOrBelow5000()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // SKW5456 spawns at t=660, fast-forward to just after spawn
        engine.Replay(recording, 662);

        var aircraft = engine.FindAircraft("SKW5456");
        Assert.NotNull(aircraft);

        output.WriteLine($"SKW5456 at spawn: alt={aircraft.Altitude:F0} StarViaMode={aircraft.StarViaMode}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route: {string.Join(" → ", route.Select(f => f.Name))}");

        // Find BERKS position in the route
        var berks = route.FirstOrDefault(t => t.Name.Equals("BERKS", StringComparison.OrdinalIgnoreCase));
        if (berks is null)
        {
            Assert.Fail("BERKS not found in route — runway transition missing");
            return;
        }

        // Tick until BERKS is sequenced (removed from route) or max 2400s (40 min)
        double altAtBerks = -1;
        bool berksSequenced = false;
        for (int t = 1; t <= 2400; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SKW5456");
            if (aircraft is null)
            {
                output.WriteLine($"  Aircraft deleted at t={t}");
                break;
            }

            // Check if BERKS just got sequenced (was in route, now isn't)
            bool hasBerks = aircraft.Targets.NavigationRoute.Any(f => f.Name.Equals("BERKS", StringComparison.OrdinalIgnoreCase));
            if (!hasBerks && !berksSequenced)
            {
                berksSequenced = true;
                altAtBerks = aircraft.Altitude;
                output.WriteLine($"  BERKS sequenced at t={t}: alt={altAtBerks:F0}");
            }

            if (t % 60 == 0)
            {
                var nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"  t={t, 4} alt={aircraft.Altitude, 7:F0} tgtAlt={aircraft.Targets.TargetAltitude?.ToString("F0") ?? "null", 7} VS={aircraft.VerticalSpeed, 6:F0} next={nextFix}"
                );
            }

            if (berksSequenced)
            {
                break;
            }
        }

        Assert.True(berksSequenced, "BERKS was never sequenced — aircraft may not have the fix in route");
        Assert.True(altAtBerks is >= 4850 and <= 5150, $"Aircraft should cross BERKS at ~5000ft (±150 tolerance), but was at {altAtBerks:F0}ft");
    }
}
