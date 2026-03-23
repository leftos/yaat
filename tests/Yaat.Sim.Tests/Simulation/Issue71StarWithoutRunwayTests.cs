using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issues #67 and #71: Aircraft with a STAR but no runway
/// specified in the navigation path should still get DVIA constraints applied.
///
/// Issue #67 recording: S3-NCTB-2 Feeder Combined — all aircraft have
/// navigationPath like "LOZIT BDEGA3" (no runway suffix) with onAltitudeProfile=true.
/// Issue #71 reports the same root cause: ALWYS3 without a runway won't DVIA.
///
/// Root cause: ApplyAltitudeProfile and ExpandStarBody skip runway-transition
/// CIFP legs when no runway designator is in the token.
/// </summary>
public class Issue71StarWithoutRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue67-dvia-recording.json";

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
    /// DAL1352 has navigationPath "LOZIT BDEGA3" (no runway suffix),
    /// onAltitudeProfile=true. StarViaMode should be enabled and the route
    /// should contain runway-transition fixes with altitude constraints.
    /// </summary>
    [Fact]
    public void StarWithoutRunway_StillHasConstraints()
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
        output.WriteLine($"  Altitude: {aircraft.Altitude:F0}, TargetAlt: {aircraft.Targets.TargetAltitude}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string constraint = fix.AltitudeRestriction is not null ? $" [alt: {fix.AltitudeRestriction}]" : "";
            string speed = fix.SpeedRestriction is not null ? $" [spd: {fix.SpeedRestriction}]" : "";
            output.WriteLine($"    {fix.Name}{constraint}{speed}");
        }

        Assert.True(aircraft.StarViaMode, "StarViaMode should be true even without runway in nav path");

        // Route should have altitude constraints from CIFP — including runway transition legs
        int constraintCount = route.Count(t => t.AltitudeRestriction is not null);
        output.WriteLine($"  Altitude constraints: {constraintCount}");
        Assert.True(
            constraintCount >= 2,
            $"Route should have multiple altitude constraints from CIFP (runway transition included), but has {constraintCount}"
        );
    }

    /// <summary>
    /// AAL680 has spawnDelay=120, same STAR pattern "LOZIT BDEGA3" without runway.
    /// Verify a different aircraft also gets constraints without a runway suffix.
    /// </summary>
    [Fact]
    public void DelayedAircraft_StarWithoutRunway_StillHasConstraints()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // AAL680 spawns at t=120
        engine.Replay(recording, 122);

        var aircraft = engine.FindAircraft("AAL680");
        Assert.NotNull(aircraft);

        output.WriteLine($"AAL680: StarViaMode={aircraft.StarViaMode}, ActiveStarId={aircraft.ActiveStarId}");

        var route = aircraft.Targets.NavigationRoute;
        output.WriteLine($"  Route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string constraint = fix.AltitudeRestriction is not null ? $" [alt: {fix.AltitudeRestriction}]" : "";
            output.WriteLine($"    {fix.Name}{constraint}");
        }

        Assert.True(aircraft.StarViaMode, "StarViaMode should be enabled for delayed aircraft without runway suffix");

        int constraintCount = route.Count(t => t.AltitudeRestriction is not null);
        Assert.True(constraintCount >= 2, $"Delayed aircraft should also have runway-transition constraints, but has {constraintCount}");
    }
}
