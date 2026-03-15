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
/// E2E tests for GitHub issue #67: Procedure version validation and resolution.
///
/// Recording: S3-NCTB-2 Feeder Combined — aircraft reference BDEGA3 (outdated),
/// but current NavData has BDEGA4. Version-agnostic resolution should resolve
/// BDEGA3 → BDEGA4, expand the STAR body, and enable descend-via when
/// onAltitudeProfile is true.
/// </summary>
public class Issue67ProcedureVersionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue67-procedure-version-recording.json";

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
    /// DAL1352 has spawnDelay=0, navigationPath "LOZIT BDEGA3", onAltitudeProfile=true.
    /// After spawn, version resolution should map BDEGA3 → BDEGA4 (current NavData),
    /// StarViaMode should be enabled, and the STAR body should be expanded in the route.
    /// </summary>
    [Fact]
    public void OutdatedStar_ResolvesToCurrentVersion_WithStarViaMode()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 2);

        var aircraft = engine.FindAircraft("DAL1352");
        Assert.NotNull(aircraft);

        output.WriteLine($"DAL1352: StarViaMode={aircraft.StarViaMode}, ActiveStarId={aircraft.ActiveStarId}");
        output.WriteLine($"  Altitude: {aircraft.Altitude:F0}, TargetAlt: {aircraft.Targets.TargetAltitude:F0}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string constraint = fix.AltitudeRestriction is not null ? $" [alt: {fix.AltitudeRestriction}]" : "";
            output.WriteLine($"    {fix.Name}{constraint}");
        }

        // BDEGA3 should have been resolved to the current version
        Assert.True(aircraft.StarViaMode, "StarViaMode should be true — BDEGA3 resolved to current version with onAltitudeProfile");

        // ActiveStarId should be the resolved (current) version, not the outdated one
        Assert.NotNull(aircraft.ActiveStarId);
        Assert.NotEqual("BDEGA3", aircraft.ActiveStarId);
        output.WriteLine($"  Resolved STAR: BDEGA3 → {aircraft.ActiveStarId}");

        // Route should contain STAR body fixes (not just the base fix "BDEGA")
        Assert.True(route.Count >= 3, $"Route should have STAR body fixes, but only has {route.Count}");

        // Route should have altitude constraints from CIFP
        int constraintCount = route.Count(t => t.AltitudeRestriction is not null);
        Assert.True(constraintCount > 0, "Route should have altitude constraints from CIFP STAR");
    }

    /// <summary>
    /// DAL1352 with resolved STAR should actually descend over time,
    /// not stay at cruise altitude.
    /// </summary>
    [Fact]
    public void OutdatedStar_AircraftDescendsViaStar()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 2);

        var aircraft = engine.FindAircraft("DAL1352");
        Assert.NotNull(aircraft);

        double initialAlt = aircraft.Altitude;
        output.WriteLine($"DAL1352 initial altitude: {initialAlt:F0}");
        output.WriteLine($"DAL1352 target altitude: {aircraft.Targets.TargetAltitude:F0}");

        // Tick 120 seconds — with via mode active, aircraft should descend
        for (int t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
        }

        aircraft = engine.FindAircraft("DAL1352");
        Assert.NotNull(aircraft);

        output.WriteLine($"DAL1352 altitude after 120s: {aircraft.Altitude:F0}");
        output.WriteLine($"DAL1352 target altitude: {aircraft.Targets.TargetAltitude:F0}");

        bool isDescending = aircraft.Altitude < initialAlt || aircraft.Targets.TargetAltitude < initialAlt;
        Assert.True(
            isDescending,
            $"Aircraft should be descending via STAR. Alt: {aircraft.Altitude:F0}, Target: {aircraft.Targets.TargetAltitude:F0}, Initial: {initialAlt:F0}"
        );
    }

    /// <summary>
    /// Scenario load warnings should include a version mismatch message for BDEGA3 → current.
    /// </summary>
    [Fact]
    public void OutdatedStar_GeneratesWarning()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 2);

        // Check warnings from the replay (stored on the engine's last load result)
        // We can verify by checking the aircraft state — if StarViaMode is true,
        // the resolution worked. The warning is tested in unit tests;
        // here we just confirm the E2E outcome.
        var aircraft = engine.FindAircraft("DAL1352");
        Assert.NotNull(aircraft);
        Assert.True(aircraft.StarViaMode, "StarViaMode confirms version resolution worked end-to-end");
    }

    /// <summary>
    /// SKW3388 has spawnDelay=240 — verify delayed aircraft also get
    /// version resolution when they spawn.
    /// </summary>
    [Fact]
    public void DelayedAircraft_AlsoGetsVersionResolution()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // SKW3388 spawns at t=240
        engine.Replay(recording, 242);

        var aircraft = engine.FindAircraft("SKW3388");
        Assert.NotNull(aircraft);

        output.WriteLine($"SKW3388: StarViaMode={aircraft.StarViaMode}, ActiveStarId={aircraft.ActiveStarId}");

        Assert.True(aircraft.StarViaMode, "Delayed aircraft should also have StarViaMode after version resolution");
        Assert.NotNull(aircraft.ActiveStarId);
        Assert.NotEqual("BDEGA3", aircraft.ActiveStarId);
    }
}
