using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
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
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    [Fact]
    public void Cross28R28L_ClearsCurrentHold_AndPreClearsUpcoming28L()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1300);

        AircraftState? ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);

        var holdPhase = ac.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(holdPhase);
        Assert.Equal("28R/10L", holdPhase.HoldShort.TargetName);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, holdPhase.HoldShort.Reason);

        TaxiRoute? route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        HoldShortPoint? upcoming28L = route.HoldShortPoints.FirstOrDefault(h =>
            h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28L") && h.Reason == HoldShortReason.RunwayCrossing
        );
        Assert.NotNull(upcoming28L);
        Assert.False(upcoming28L.IsCleared, "Pre-condition: 28L crossing should be uncleared before CROSS 28R 28L");

        CommandResult result = engine.SendCommand("N7LJ", "CROSS 28R 28L");
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

        // Both crossings are cleared, so the first never brakes for the second's bar: from 28R's far marking to 28L's near bar
        // the aircraft keeps its crossing speed, less half a knot.
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(ac.Ground.Layout);
        GroundNode farBar28R = layout.Nodes[
            route
                .Segments.First(s =>
                    (s.ToNodeId != holdPhase.HoldShort.NodeId)
                    && (layout.Nodes[s.ToNodeId].Type == GroundNodeType.RunwayHoldShort)
                    && (layout.Nodes[s.ToNodeId].RunwayId?.Contains("28R") == true)
                )
                .ToNodeId
        ];
        GroundNode nearBar28L = layout.Nodes[upcoming28L.NodeId];
        GroundNode nearBar28R = layout.Nodes[holdPhase.HoldShort.NodeId];
        var across28R = new TrueHeading(GeoMath.BearingTo(nearBar28R.Position, farBar28R.Position));
        var toward28L = new TrueHeading(GeoMath.BearingTo(farBar28R.Position, nearBar28L.Position));
        double minKts = CategoryPerformance.RunwayCrossingSpeed(AircraftCategorization.Categorize(ac.AircraftType)) - 0.5;
        double lowestKts = double.PositiveInfinity;
        string lowestAt = "none";
        AircraftState? at28L = SubTickDriver.TickSubTicksUntil(
            engine,
            "N7LJ",
            a => GeoMath.AlongTrackDistanceNm(a.Position, nearBar28L.Position, toward28L) > 0.0,
            120,
            a =>
            {
                bool pastFar28R = GeoMath.AlongTrackDistanceNm(a.Position, farBar28R.Position, across28R) > 0.0;
                bool short28L = GeoMath.AlongTrackDistanceNm(a.Position, nearBar28L.Position, toward28L) <= 0.0;
                if (pastFar28R && short28L && (a.GroundSpeed < lowestKts))
                {
                    lowestKts = a.GroundSpeed;
                    double from28LFt = GeoMath.DistanceNm(a.Position, nearBar28L.Position) * GeoMath.FeetPerNm;
                    lowestAt = $"{a.Phases?.CurrentPhase?.Name}, {from28LFt:F0} ft from 28L's bar";
                }
            }
        );
        Assert.NotNull(at28L);
        output.WriteLine($"lowest speed between 28R's far marking and 28L's near bar: {lowestKts:F2} kt ({lowestAt})");
        Assert.True(double.IsFinite(lowestKts), "never sampled between 28R's far marking and 28L's near bar");
        Assert.True(lowestKts >= minKts, $"N7LJ slowed to {lowestKts:F2} kt ({lowestAt}) between the runways, below {minKts:F1} kt");
    }
}
