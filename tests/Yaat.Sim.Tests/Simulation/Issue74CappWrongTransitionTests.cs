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
/// E2E tests for GitHub issue #74: CAPP selects wrong approach transition.
///
/// Recording: S3-NCTB-6 (A) SFO19 — UAL238 on ALWYS3 STAR → KSFO runway 19L.
/// The STAR terminates at BERKS, which is also the first common leg of the I19L
/// approach. The CCR transition ends at BERKS (boundary fix). The old code matched
/// BERKS as a transition leg, selecting the CCR transition and routing the aircraft
/// to CCR (miles off course). The fix excludes common leg fixes from transition matching.
/// </summary>
public class Issue74CappWrongTransitionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue74-capp-wrong-transition-recording.json";

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
    /// Core regression test: UAL238 at t=400 still has BERKS in its NavigationRoute
    /// (from the ALWYS3 STAR). BERKS is the boundary fix between the CCR transition
    /// and the I19L common legs. SelectBestTransition must NOT select the CCR transition.
    /// </summary>
    [Fact]
    public void SelectBestTransition_UAL238_DoesNotMatchCcrViaBerks()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var navDb = TestVnasData.NavigationDb!;

        engine.Replay(recording, 400);

        var aircraft = engine.FindAircraft("UAL238");
        Assert.NotNull(aircraft);

        output.WriteLine($"Route: {aircraft.Route}");
        output.WriteLine($"NavRoute: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(n => n.Name))}");

        var resolved = ApproachCommandHandler.ResolveApproach(null, null, aircraft);
        Assert.True(resolved.Success, $"Should resolve approach. Got: {resolved.Error}");

        var procedure = resolved.Procedure!;
        output.WriteLine($"Approach: {procedure.ApproachId} transitions: {string.Join(", ", procedure.Transitions.Keys)}");
        foreach (var (name, transition) in procedure.Transitions)
        {
            var legNames = transition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier);
            output.WriteLine($"  Transition {name}: {string.Join(" → ", legNames)}");
        }

        var commonFixNames = procedure.CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier);
        output.WriteLine($"  Common legs: {string.Join(" → ", commonFixNames)}");

        var selected = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);
        output.WriteLine($"Selected transition: {selected?.Name ?? "(none)"}");

        // BERKS is a common leg fix — matching on it is a false positive.
        // No transition should be selected because no transition-specific fix is in the route.
        Assert.Null(selected);
    }

    /// <summary>
    /// Full CAPP E2E at t=688 (matching the recording). The approach clearance should
    /// be I19L on runway 19L, and no CCR transition fixes should appear in the approach
    /// navigation or targets.
    /// </summary>
    [Fact]
    public void Capp_UAL238_CorrectApproachWithoutCcrTransition()
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

        output.WriteLine($"Before CAPP: lat={aircraft.Latitude:F4} lon={aircraft.Longitude:F4} hdg={aircraft.Heading:F1}");

        var result = engine.SendCommand("UAL238", "CAPP");
        output.WriteLine($"CAPP result: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success, $"CAPP should succeed. Got: {result.Message}");
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("19L", aircraft.Phases.ActiveApproach.RunwayId);

        foreach (var phase in aircraft.Phases.Phases)
        {
            output.WriteLine($"Phase: {phase.Name} ({phase.GetType().Name})");
        }

        var navPhase = aircraft.Phases.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault();
        if (navPhase is not null)
        {
            var fixNames = navPhase.Fixes.Select(f => f.Name).ToList();
            output.WriteLine($"Approach fixes: {string.Join(" → ", fixNames)}");
            Assert.DoesNotContain("CCR", fixNames);
            Assert.DoesNotContain("UPEND", fixNames);
        }

        var navTargets = aircraft.Targets.NavigationRoute.Select(n => n.Name).ToList();
        output.WriteLine($"Nav targets: {string.Join(" → ", navTargets)}");
        Assert.DoesNotContain("CCR", navTargets);
    }
}
