using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #181 ("Auto Taxi not working"): ZHU S2-L3/4 (AUS_E_TWR).
/// Only N418ES auto-taxied; every other departure stayed parked.
///
/// Root cause: scenario presets all have timeOffset 0, so at spawn they are
/// dispatched one-by-one. A leading-WAIT preset ("WAIT 120 RWY 18L TAXI N B") is
/// parked in DeferredDispatches; the next CONDITIONAL preset ("ONHO CM 120", then
/// "AT 6000 DCT MUNCH") ran DeferredDispatches.Clear() in DispatchCompoundCore,
/// wiping the deferred taxi. N418ES is the only departure whose presets are all
/// WAIT-led, so nothing cleared its deferral.
///
/// N4985B taxis at WAIT 60 (RWY 18L TAXI P B; ONHO CM 065; AT 4000 DCT KBAZ),
/// N69WS at WAIT 120 (RWY 18L TAXI N B; ONHO CM 120; AT 6000 DCT MUNCH). Both have
/// an ONHO conditional, so both were affected. After the fix they must leave parking.
/// </summary>
public class Issue181AutoTaxiTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue181-auto-taxi-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replaying past their WAIT-to-taxi triggers, the departures that have an ONHO/AT
    /// conditional preset must have left AtParkingPhase. Pre-fix they stay parked
    /// because the conditional preset wiped their deferred taxi clearance.
    /// </summary>
    [Theory]
    [InlineData("N4985B", 60)]
    [InlineData("N69WS", 120)]
    public void DepartureWithConditionalPresets_LeavesParking(string callsign, int waitSeconds)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay comfortably past the WAIT-to-taxi trigger.
        int target = waitSeconds + 45;
        engine.Replay(recording, target);

        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        output.WriteLine(
            $"t={target}: {callsign} phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} "
                + $"gs={ac.GroundSpeed:F1} onGround={ac.IsOnGround} taxiRoute={(ac.Ground.AssignedTaxiRoute is null ? "null" : "set")}"
        );

        Assert.False(
            ac.Phases?.CurrentPhase is AtParkingPhase,
            $"{callsign} should have started taxiing by t={target} (WAIT {waitSeconds}) but is still AtParking — "
                + "the conditional preset (ONHO/AT) wiped its deferred taxi clearance."
        );
    }
}
