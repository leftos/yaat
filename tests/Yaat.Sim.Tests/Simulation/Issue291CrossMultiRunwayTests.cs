using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #291: <c>CROSS 28R 28L</c> should clear the crossing the aircraft is
/// holding short of and pre-clear the next, in one command.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts — N7LJ (LJ45) preset
/// <c>TAXI D C B W W1 30 HS 28R</c>. The route crosses two parallel pairs
/// (28R/10L explicit, 28L/10R implicit) on the way to runway 30. At t=1300 N7LJ is
/// holding short of 28R/10L (the preset's <c>HS 28R</c> ExplicitHoldShort), with the
/// 28L crossing still uncleared further on. A single <c>CROSS 28R 28L</c> should both
/// clear the current 28R hold (aircraft starts crossing) and pre-clear the upcoming
/// 28L crossing so it flows straight into a CrossingRunwayPhase at 28L without stopping.
///
/// Before the fix, standalone CROSS took a single opaque runway argument, so
/// <c>CROSS 28R 28L</c> was parsed as the runway id "28R 28L" and rejected with
/// "No hold-short for 28R 28L in taxi route".
/// </summary>
public class Issue291CrossMultiRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/606cf53c33a1.zip";

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
    public void Cross28R28L_ClearsCurrentHold_AndPreClearsUpcoming28L()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1300);

        var ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);

        var holdPhase = ac.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(holdPhase);
        Assert.Equal("28R/10L", holdPhase.HoldShort.TargetName);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, holdPhase.HoldShort.Reason);

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        var upcoming28L = route.HoldShortPoints.FirstOrDefault(h =>
            h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28L") && h.Reason == HoldShortReason.RunwayCrossing
        );
        Assert.NotNull(upcoming28L);
        Assert.False(upcoming28L.IsCleared, "Pre-condition: 28L crossing should be uncleared before CROSS 28R 28L");

        var result = engine.SendCommand("N7LJ", "CROSS 28R 28L");
        output.WriteLine($"CROSS 28R 28L: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"CROSS 28R 28L should succeed, got: {result.Message}");

        // The current 28R hold must be cleared: after a few ticks the aircraft has
        // moved past HoldingShortPhase.
        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }
        ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);
        Assert.IsNotType<HoldingShortPhase>(ac.Phases?.CurrentPhase);

        // The upcoming 28L crossing must now be flagged cleared so the aircraft
        // transitions straight into a CrossingRunwayPhase at that node instead of
        // installing a fresh HoldingShortPhase.
        Assert.True(upcoming28L.IsCleared, "CROSS 28R 28L should have pre-cleared the upcoming 28L crossing");
    }
}
