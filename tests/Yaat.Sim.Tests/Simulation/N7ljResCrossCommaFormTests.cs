using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the parallel-block form <c>RES, CROSS &lt;rwy&gt;</c>.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts — N7LJ (LJ45) preset
/// <c>TAXI D C B W W1 30 HS 28R</c>. The aircraft taxis through two parallel
/// pairs (28R/10L explicit, 28L/10R implicit) on its way to runway 30.
///
/// Bug (user-reported from
/// S2-OAK-4_VFR_Transitions_Radar_Concepts.yaat-bug-report-bundle.zip, 2026-05-24):
/// while N7LJ was holding short of 28R on B, the controller tried
/// <c>RES, CROSS 28L</c> (comma form, two parallel commands in one block) to
/// authorize both runway crossings at once. The CROSS half failed with
/// "Not holding short of 28L" because <c>TryCrossRunway</c> demanded the
/// requested runway match the *current* hold target. The user's only
/// recourse was issuing <c>CROSS 28L</c> separately after the aircraft was
/// already moving across 28R.
///
/// Expected: the comma form should behave like the existing combined
/// <c>RES CROSS 28L</c> overload — RES clears the current 28R hold, and
/// CROSS pre-clears the upcoming 28L crossing in the taxi route so the
/// aircraft transitions straight into <see cref="CrossingRunwayPhase"/> at
/// 28L without stopping.
/// </summary>
public class N7ljResCrossCommaFormTests(ITestOutputHelper output)
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
    public void ResCommaCross28L_ClearsCurrentHoldShort_AndPreClearsUpcoming28L()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // t=1300s — N7LJ has been in HoldingShortPhase at 28R/10L since ~t=1180
        // (preset's `HS 28R` makes this an ExplicitHoldShort). The route also
        // contains an implicit RunwayCrossing hold-short for 28L/10R further on,
        // plus a DestinationRunway hold-short for 30.
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
        Assert.False(upcoming28L.IsCleared, "Pre-condition: 28L crossing should be uncleared before RES, CROSS");

        var result = engine.SendCommand("N7LJ", "RES, CROSS 28L");
        output.WriteLine($"RES, CROSS 28L: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"RES, CROSS 28L should succeed, got: {result.Message}");

        // The current 28R hold must be cleared (aircraft no longer stuck).
        // After 5 ticks the aircraft should have moved past HoldingShortPhase.
        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }
        ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);
        Assert.IsNotType<HoldingShortPhase>(ac.Phases?.CurrentPhase);

        // The upcoming 28L crossing must now be flagged cleared so the aircraft
        // will transition straight into a CrossingRunwayPhase at that node
        // instead of installing a fresh HoldingShortPhase.
        Assert.True(upcoming28L.IsCleared, "RES, CROSS 28L should have pre-cleared the upcoming 28L crossing");
    }
}
