using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for unconditional ADCT (Append Direct To) tearing down InitialClimb.
///
/// Scenario S2-OAK-4 | VFR Transitions/Radar Concepts. N172SP departs OAK after
/// "CTO TRDCT OAK30NUM 014" at t=320, reaches InitialClimb at t=395, and at
/// t=427 the user issues "ADCT VPMID" to extend the route. The expected
/// behavior is that VPMID is appended after OAK30NUM and the InitialClimb
/// phase keeps climbing toward the CTO-assigned 1400 ft. Instead,
/// InitialClimbPhase.CanAcceptCommand returns ClearsPhase for AppendDirectTo
/// (the whitelist only honors altitude/speed verbs), CommandDispatcher tears
/// the phase chain down, and emits
/// "N172SP InitialClimb cancelled by ADCT VPMID" at t=430.
///
/// Bundle: adct-cancels-initial-climb-recording.yaat-bug-report-bundle.zip
/// </summary>
public class AdctDuringInitialClimbTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/0ee0513aa9f0.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replays past the user's "ADCT VPMID" at t=427 and asserts that the
    /// InitialClimb phase is preserved, the route now contains both OAK30NUM
    /// and VPMID, and no "cancelled by" warning fired.
    /// </summary>
    [Fact]
    public void AdctVpmid_DoesNotCancelInitialClimb()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay through the dispatch of "ADCT VPMID" (recorded at t=427).
        // A few extra seconds give the warning a chance to drain into PendingWarnings.
        engine.Replay(recording, 435);

        var aircraft = engine.FindAircraft("N172SP");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"phase={aircraft.Phases?.CurrentPhase?.Name ?? "(null)"} "
                + $"alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F0} "
                + $"route=[{string.Join(", ", aircraft.Targets.NavigationRoute.Select(t => t.Name))}] "
                + $"warnings={aircraft.PendingWarnings.Count}"
        );

        foreach (var w in aircraft.PendingWarnings)
        {
            output.WriteLine($"  WRN: {w}");
        }

        // The InitialClimb chain must NOT have been torn down by the unconditional ADCT.
        // The aircraft is still climbing toward its assigned 1400 ft at t=435, well before
        // InitialClimb's natural completion, so the phase chain should still be active.
        Assert.NotNull(aircraft.Phases);
        Assert.NotNull(aircraft.Phases.CurrentPhase);
        Assert.IsType<InitialClimbPhase>(aircraft.Phases.CurrentPhase);

        // No phase-cancellation warning tied to ADCT VPMID.
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("cancelled by") && w.Contains("ADCT VPMID"));

        // Route was extended in order: OAK30NUM then VPMID.
        var routeNames = aircraft.Targets.NavigationRoute.Select(t => t.Name).ToList();
        int oakIdx = routeNames.FindIndex(n => string.Equals(n, "OAK30NUM", StringComparison.OrdinalIgnoreCase));
        int vpmidIdx = routeNames.FindIndex(n => string.Equals(n, "VPMID", StringComparison.OrdinalIgnoreCase));
        Assert.True(oakIdx >= 0, $"OAK30NUM missing from route [{string.Join(", ", routeNames)}]");
        Assert.True(vpmidIdx >= 0, $"VPMID missing from route [{string.Join(", ", routeNames)}]");
        Assert.True(oakIdx < vpmidIdx, $"VPMID should come after OAK30NUM, got [{string.Join(", ", routeNames)}]");
    }
}
