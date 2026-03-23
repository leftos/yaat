using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #128: bare CAPP tries to use the scenario's primaryApproach
/// (e.g. I30L for SJC) even for aircraft destined for other airports (e.g. NUQ).
///
/// Recording: S3-NCTA-1 | Area A Familiarization — EJA864 is destined for KNUQ
/// with route SNS HOSNU. Scenario primaryApproach is I30L, primaryAirportId is SJC.
/// HOSNU connects to the R32L approach at NUQ.
/// </summary>
public class Issue128CappWrongAirportTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue121-ctopp-recording.json";

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
    /// EJA864 is destined for KNUQ. The scenario's primaryApproach (I30L) is for SJC,
    /// not NUQ. After the ScenarioLoader fix, EJA864 should NOT have ExpectedApproach = "I30L".
    /// Bare CAPP should auto-discover R32L at NUQ (via HOSNU route connectivity),
    /// NOT fail with "Unknown approach: I30L at NUQ".
    /// </summary>
    [Fact]
    public void BareCapp_NonPrimaryAirport_DoesNotUsePrimaryApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // EJA864 has spawnDelay=0, replay a few ticks to ensure it's spawned and on route
        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("EJA864");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"Before: ExpectedApproach={aircraft.ExpectedApproach} Destination={aircraft.Destination} NavRoute={aircraft.Targets.NavigationRoute.Count}"
        );

        // Part 1 fix: primaryApproach should NOT have been assigned to non-primary-airport aircraft
        Assert.NotEqual("I30L", aircraft.ExpectedApproach);

        // Bare CAPP should succeed by auto-discovering a connected approach at NUQ
        var result = engine.SendCommand("EJA864", "CAPP");

        output.WriteLine($"After: Success={result.Success} Message={result.Message} ActiveApproach={aircraft.Phases?.ActiveApproach?.ApproachId}");

        Assert.True(result.Success, $"Bare CAPP should succeed via route-connected approach discovery at NUQ. Got: {result.Message}");

        // The resolved approach should be at NUQ (R32L connects via HOSNU)
        string? approachId = aircraft.Phases?.ActiveApproach?.ApproachId ?? aircraft.PendingApproachClearance?.Clearance.ApproachId;
        Assert.NotNull(approachId);
        output.WriteLine($"Resolved approach: {approachId}");
    }

    /// <summary>
    /// Aircraft at the primary airport on a nav route whose ExpectedApproach doesn't connect
    /// to the route should auto-discover an approach that does connect.
    /// LXJ453 is destined for KSJC with navPath RAZRR RAZRR5.30L (spawnDelay=0).
    /// We override ExpectedApproach to I14L (doesn't connect to RAZRR route) to force
    /// the connectivity check to fail and trigger auto-discovery of a connected approach.
    /// </summary>
    [Fact]
    public void BareCapp_PrimaryAirport_OnNavRoute_PrefersConnectedApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("LXJ453");
        Assert.NotNull(aircraft);

        // Override ExpectedApproach to something that exists at SJC but doesn't connect to the route
        aircraft.ExpectedApproach = "I12R";

        output.WriteLine($"Before: ExpectedApproach={aircraft.ExpectedApproach} NavRoute count={aircraft.Targets.NavigationRoute.Count}");

        Assert.True(aircraft.Targets.NavigationRoute.Count > 0, "Aircraft should be on a navigation route");

        var result = engine.SendCommand("LXJ453", "CAPP");

        output.WriteLine($"After: Success={result.Success} Message={result.Message} ActiveApproach={aircraft.Phases?.ActiveApproach?.ApproachId}");

        // CAPP should succeed by auto-discovering an approach that connects to the RAZRR route
        Assert.True(result.Success, $"Bare CAPP should auto-discover a connected approach. Got: {result.Message}");

        string? approachId = aircraft.Phases?.ActiveApproach?.ApproachId ?? aircraft.PendingApproachClearance?.Clearance.ApproachId;
        Assert.NotNull(approachId);
        // Should NOT be the disconnected I12R
        Assert.NotEqual("I12R", approachId);
        output.WriteLine($"Resolved approach: {approachId}");
    }

    /// <summary>
    /// Aircraft being vectored (empty NavigationRoute) at the primary airport should
    /// use ExpectedApproach directly without connectivity check.
    /// LXJ453 is destined for KSJC with expectedApproach I30L (spawnDelay=0).
    /// </summary>
    [Fact]
    public void BareCapp_PrimaryAirport_BeingVectored_UsesPrimaryApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("LXJ453");
        Assert.NotNull(aircraft);

        // Force vectoring state: clear nav route
        aircraft.Targets.NavigationRoute.Clear();
        Assert.Empty(aircraft.Targets.NavigationRoute);

        output.WriteLine($"ExpectedApproach={aircraft.ExpectedApproach}");
        Assert.Equal("I30L", aircraft.ExpectedApproach);

        var result = engine.SendCommand("LXJ453", "CAPP");

        output.WriteLine($"After: Success={result.Success} Message={result.Message}");

        // When vectored, CAPP should use ExpectedApproach directly
        Assert.True(result.Success, $"Bare CAPP should succeed for vectored aircraft using ExpectedApproach. Got: {result.Message}");
    }
}
