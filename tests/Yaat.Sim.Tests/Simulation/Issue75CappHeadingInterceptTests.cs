using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #75: CAPP heading intercept regression.
///
/// Recording: S3-NCTB-6 (A) SFO19 — UAL238 on ALWYS3 STAR → KSFO runway 19L.
///
/// The bug: TryClearedApproach checked TargetHeading to decide whether to use
/// InterceptCoursePhase. But TargetHeading is set by FlightPhysics every tick
/// during route navigation. So a bare CAPP while navigating incorrectly triggered
/// the intercept path instead of approach fix navigation.
///
/// The fix: use AssignedHeading (controller-issued) instead of TargetHeading (physics-derived).
///
/// Timeline:
///   t=688: UAL238 navigating on STAR (TargetHeading set by physics, AssignedHeading null)
///          → CAPP should use ApproachNavigationPhase
///   t=928: FH 240 (explicit heading → AssignedHeading = 240)
///   t=984: CAPP → should use InterceptCoursePhase (aircraft was vectored)
/// </summary>
public class Issue75CappHeadingInterceptTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue75-capp-heading-intercept-recording.json";

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
    /// At t=688 UAL238 is navigating on its STAR. TargetHeading is set by physics
    /// (route following), but no controller heading command has been issued, so
    /// AssignedHeading is null. A bare CAPP must use ApproachNavigationPhase, not
    /// InterceptCoursePhase.
    /// </summary>
    [Fact]
    public void Capp_WhileNavigating_UsesFixNavigation()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 688);

        var aircraft = engine.FindAircraft("UAL238");
        Assert.NotNull(aircraft);

        output.WriteLine($"TargetHeading: {aircraft.Targets.TargetTrueHeading}");
        output.WriteLine($"AssignedHeading: {aircraft.Targets.AssignedMagneticHeading?.Degrees}");
        output.WriteLine($"NavRoute: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(n => n.Name))}");

        // TargetHeading is set by physics, but no controller heading was issued
        Assert.NotNull(aircraft.Targets.TargetTrueHeading);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);

        var result = engine.SendCommand("UAL238", "CAPP");
        output.WriteLine($"CAPP result: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);

        foreach (var phase in aircraft.Phases.Phases)
        {
            output.WriteLine($"Phase: {phase.Name} ({phase.GetType().Name})");
        }

        // Must use fix navigation, not intercept
        Assert.Contains(aircraft.Phases.Phases, p => p is ApproachNavigationPhase);
        Assert.DoesNotContain(aircraft.Phases.Phases, p => p is InterceptCoursePhase);
    }

    /// <summary>
    /// Replay to t=928 (includes FH 240 from the recording). At this point
    /// AssignedHeading is set. Issue a bare CAPP — should use InterceptCoursePhase
    /// since the aircraft was explicitly vectored.
    /// </summary>
    [Fact]
    public void Capp_AfterExplicitHeading_UsesInterceptPhase()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay to t=928 — the recording includes FH 240 at t=928
        engine.Replay(recording, 928);

        var aircraft = engine.FindAircraft("UAL238");
        Assert.NotNull(aircraft);

        output.WriteLine($"TargetHeading: {aircraft.Targets.TargetTrueHeading}");
        output.WriteLine($"AssignedHeading: {aircraft.Targets.AssignedMagneticHeading?.Degrees}");

        // FH 240 was applied by replay — AssignedHeading should be set
        Assert.NotNull(aircraft.Targets.AssignedMagneticHeading);
        Assert.Equal(240.0, aircraft.Targets.AssignedMagneticHeading.Value.Degrees);

        // Issue CAPP — should use intercept since aircraft was explicitly vectored
        var cappResult = engine.SendCommand("UAL238", "CAPP");
        output.WriteLine($"CAPP result: Success={cappResult.Success} Message={cappResult.Message}");

        Assert.True(cappResult.Success);
        Assert.NotNull(aircraft.Phases);

        foreach (var phase in aircraft.Phases.Phases)
        {
            output.WriteLine($"Phase: {phase.Name} ({phase.GetType().Name})");
        }

        Assert.IsType<InterceptCoursePhase>(aircraft.Phases.Phases[0]);
    }
}
