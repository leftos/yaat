using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for ground BEHIND/GIVEWAY firing immediately when the target callsign
/// can't be resolved by the server's exact-match FindAircraft.
///
/// Recording: S2-OAK-3 (2) | VFR Sequencing — at t=537 the user issues
/// `GIVEWAY 152SP TAXI C D @NEW1` to N569SX (which is in HoldingAfterExit on 28R).
/// The intended target is N152SP (taxiing east on C toward N569SX). The literal
/// "152SP" reaches the server, exact-match FindAircraft returns null, and the
/// "target gone" shortcut in IsGiveWayMet immediately fires the deferred TAXI.
/// N569SX starts taxiing into a head-on conflict with N152SP.
///
/// Expected after fix: TryDeferGiveWay rejects the BEHIND with Success=false when
/// the target callsign is unresolvable. N569SX stays in HoldingAfterExit at ias=0.
/// </summary>
public class BehindGroundTaxiE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/behind-ground-taxi-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N569SX_HoldsAfterUnresolvedBehindTarget()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // BEHIND command is at t=537. Replay 8 seconds past it so any deferred
        // dispatch has had a tick to evaluate. Track shows the buggy code puts
        // N569SX into TaxiingPhase by t=540.
        engine.Replay(recording, 545);

        var ac = engine.FindAircraft("N569SX");
        Assert.NotNull(ac);

        var phaseName = ac.Phases?.CurrentPhase?.GetType().Name;
        output.WriteLine($"t=545: N569SX phase={phaseName} ias={ac.IndicatedAirspeed:F1}");

        Assert.NotEqual(nameof(TaxiingPhase), phaseName);
        Assert.True(ac.IndicatedAirspeed < 0.5, $"N569SX should be stationary after unresolved BEHIND but ias={ac.IndicatedAirspeed:F1}");
    }
}
