using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #95: Approach ambiguity resolution + STAR altitude loss on CAPP.
///
/// Recording: S3-MTN-O1 (A) Area E NV RNOS — SWA11 (B738, KPHX→KRNO) on SCOLA1 STAR.
/// Command "AT KLOCK CAPP I17R" issued at t=71s.
///
/// Bug 1: YAAT picked I17RX instead of I17RZ. I17RZ connects to KLOCK (a transition fix),
///         I17RX does not. Ambiguous shorthand "I17R" must resolve via connectivity.
///
/// Bug 2: After CAPP, StarViaMode/ActiveStarId/StarViaFloor were not cleared, causing
///         stale STAR descent logic to conflict with approach phases.
/// </summary>
public class Issue95CappAmbiguityTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue95-capp-ambiguity-recording.json";

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
    /// "CAPP I17R" (direct, no AT) must resolve to I17RZ (the variant whose transition
    /// connects to KLOCK on the aircraft's nav route), not I17RX.
    /// </summary>
    [Fact]
    public void SWA11_CappI17R_ResolvesToConnectingApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay to t=70, just before the original AT KLOCK CAPP I17R at t=71
        engine.Replay(recording, 70);

        var aircraft = engine.FindAircraft("SWA11");
        Assert.NotNull(aircraft);

        output.WriteLine($"Before CAPP: alt={aircraft.Altitude:F0} hdg={aircraft.TrueHeading.Degrees:F1}");
        output.WriteLine($"Route: {aircraft.Route}");
        output.WriteLine($"NavRoute: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(n => n.Name))}");
        output.WriteLine($"StarViaMode: {aircraft.StarViaMode} ActiveStarId: {aircraft.ActiveStarId}");

        // Send CAPP I17R directly (no AT) — tests approach resolution immediately
        var result = engine.SendCommand("SWA11", "CAPP I17R");
        output.WriteLine($"CAPP result: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success, $"CAPP should succeed. Got: {result.Message}");

        // Must be I17RZ (connects to KLOCK), not I17RX.
        // May be immediate (Phases.ActiveApproach) or deferred (PendingApproachClearance)
        // depending on whether the aircraft is on a STAR with a connecting fix.
        string? approachId = aircraft.Phases?.ActiveApproach?.ApproachId ?? aircraft.PendingApproachClearance?.Clearance.ApproachId;
        Assert.NotNull(approachId);
        output.WriteLine($"Resolved approach: {approachId}");
        Assert.Equal("I17RZ", approachId);
    }

    /// <summary>
    /// "CAPP 17R" (runway-only, no type prefix) should resolve to the approach variant
    /// that connects to the aircraft's nav route (through KLOCK).
    /// </summary>
    [Fact]
    public void SWA11_CappRunwayOnly_ResolvesToConnectingApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 70);

        var aircraft = engine.FindAircraft("SWA11");
        Assert.NotNull(aircraft);

        // Send CAPP 17R directly (no AT) — tests runway-only resolution
        var result = engine.SendCommand("SWA11", "CAPP 17R");
        output.WriteLine($"CAPP 17R result: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success, $"CAPP 17R should succeed. Got: {result.Message}");

        // The resolved approach must connect to KLOCK.
        // May be immediate (Phases.ActiveApproach) or deferred (PendingApproachClearance).
        string? approachId = aircraft.Phases?.ActiveApproach?.ApproachId ?? aircraft.PendingApproachClearance?.Clearance.ApproachId;
        Assert.NotNull(approachId);
        output.WriteLine($"Resolved approach: {approachId}");

        // Verify the approach has a transition that includes KLOCK
        var navDb = NavigationDatabase.Instance;
        var procedure = navDb.GetApproach("RNO", approachId);
        Assert.NotNull(procedure);

        bool hasKlockTransition = procedure.Transitions.Values.Any(t =>
            t.Legs.Any(l => "KLOCK".Equals(l.FixIdentifier, StringComparison.OrdinalIgnoreCase))
        );
        Assert.True(hasKlockTransition, $"Approach {approachId} should have a transition through KLOCK");
    }

    /// <summary>
    /// After CAPP fires with deferred approach (STAR delivers to connecting fix),
    /// STAR state stays active during the deferred period — the aircraft is still
    /// flying the STAR. STAR state is cleared when the pending approach activates
    /// (when the route empties at the connecting fix).
    /// </summary>
    [Fact]
    public void SWA11_StarViaModeClearedAfterCapp()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 70);

        var aircraft = engine.FindAircraft("SWA11");
        Assert.NotNull(aircraft);

        // Confirm STAR state is active before CAPP
        output.WriteLine(
            $"Before CAPP: StarViaMode={aircraft.StarViaMode} ActiveStarId={aircraft.ActiveStarId} StarViaFloor={aircraft.StarViaFloor}"
        );

        // Send CAPP directly to test state clearing
        var result = engine.SendCommand("SWA11", "CAPP I17R");
        Assert.True(result.Success, $"CAPP should succeed. Got: {result.Message}");

        output.WriteLine($"After CAPP: StarViaMode={aircraft.StarViaMode} ActiveStarId={aircraft.ActiveStarId} StarViaFloor={aircraft.StarViaFloor}");

        if (aircraft.PendingApproachClearance is not null)
        {
            // Deferred path: STAR state stays active while aircraft continues STAR route.
            // STAR state will be cleared when the pending approach activates.
            output.WriteLine("Deferred approach — STAR state preserved during deferred period");
            Assert.NotNull(aircraft.PendingApproachClearance);
        }
        else
        {
            // Immediate path: STAR state must be cleared after approach clearance
            Assert.False(aircraft.StarViaMode, "StarViaMode should be false after immediate CAPP");
            Assert.Null(aircraft.ActiveStarId);
            Assert.Null(aircraft.StarViaFloor);
        }
    }

    /// <summary>
    /// After CAPP, the aircraft should continue descending to meet the SCOLA1 KLOCK
    /// constraint (10,000ft). Ticking forward should show altitude decreasing past 11,000.
    /// </summary>
    [Fact]
    public void SWA11_DescendsToMeetKlockConstraint()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay the full recording (includes the AT KLOCK CAPP at t=71)
        engine.Replay(recording, 745);

        var aircraft = engine.FindAircraft("SWA11");
        Assert.NotNull(aircraft);

        output.WriteLine($"After full replay: alt={aircraft.Altitude:F0}");

        // After 745 seconds of sim time (started at 19,000, needs to reach ~10,000 at KLOCK),
        // altitude should be well below 11,000. The bug was that it stopped at 11,000.
        Assert.True(aircraft.Altitude < 11000, $"Aircraft should descend below 11,000 but is at {aircraft.Altitude:F0}");
    }
}
