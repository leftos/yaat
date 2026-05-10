using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Toggling AutoCrossRunway mid-session must update implicit RunwayCrossing hold-shorts
/// on aircraft that are already taxiing — turning it on pre-clears their remaining
/// crossings, turning it off re-arms only the crossings that were cleared by AutoCross
/// (preserving first-crossing-resume and explicit CROSS-keyword clearances).
/// </summary>
public class AutoCrossRunwayToggleTests(ITestOutputHelper output)
{
    private static HoldShortPoint MakeHs(int nodeId, HoldShortReason reason, bool isCleared, bool clearedByAutoCross = false) =>
        new()
        {
            NodeId = nodeId,
            Reason = reason,
            TargetName = reason == HoldShortReason.RunwayCrossing ? "28R" : "28L",
            IsCleared = isCleared,
            ClearedByAutoCross = clearedByAutoCross,
        };

    private static TaxiRoute MakeRoute(params HoldShortPoint[] holdShorts) => new() { Segments = [], HoldShortPoints = [.. holdShorts] };

    [Fact]
    public void Apply_ToggleOn_MarksUnclearedRunwayCrossings_AsClearedByAutoCross()
    {
        var route = MakeRoute(MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: false));

        TaxiRouteAutoCross.Apply(route, autoCross: true);

        var hs = route.HoldShortPoints[0];
        Assert.True(hs.IsCleared);
        Assert.True(hs.ClearedByAutoCross);
    }

    [Fact]
    public void Apply_ToggleOn_DoesNotMarkAlreadyClearedCrossings()
    {
        // Already cleared by some other path (first-crossing or explicit CROSS keyword);
        // ClearedByAutoCross stays false so a future toggle-OFF won't revert it.
        var route = MakeRoute(MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: true));

        TaxiRouteAutoCross.Apply(route, autoCross: true);

        var hs = route.HoldShortPoints[0];
        Assert.True(hs.IsCleared);
        Assert.False(hs.ClearedByAutoCross);
    }

    [Fact]
    public void Apply_ToggleOn_DoesNotTouchExplicitOrDestinationHoldShorts()
    {
        var route = MakeRoute(
            MakeHs(100, HoldShortReason.ExplicitHoldShort, isCleared: false),
            MakeHs(101, HoldShortReason.DestinationRunway, isCleared: false)
        );

        TaxiRouteAutoCross.Apply(route, autoCross: true);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.False(route.HoldShortPoints[1].IsCleared);
        Assert.False(route.HoldShortPoints[1].ClearedByAutoCross);
    }

    [Fact]
    public void Apply_ToggleOff_RevertsOnlyAutoCrossClearedCrossings()
    {
        var route = MakeRoute(
            // Cleared by AutoCross — must revert.
            MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: true, clearedByAutoCross: true),
            // Cleared by some other path (e.g. first-crossing resume) — must persist.
            MakeHs(101, HoldShortReason.RunwayCrossing, isCleared: true, clearedByAutoCross: false),
            // Already uncleared — no-op.
            MakeHs(102, HoldShortReason.RunwayCrossing, isCleared: false)
        );

        TaxiRouteAutoCross.Apply(route, autoCross: false);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(route.HoldShortPoints[1].IsCleared);
        Assert.False(route.HoldShortPoints[1].ClearedByAutoCross);
        Assert.False(route.HoldShortPoints[2].IsCleared);
        Assert.False(route.HoldShortPoints[2].ClearedByAutoCross);
    }

    [Fact]
    public void World_ApplyAutoCrossToActiveTaxiRoutes_UpdatesEveryTaxiingAircraft()
    {
        var world = new SimulationWorld();
        var ac1 = new AircraftState { Callsign = "A1", AircraftType = "C172" };
        ac1.Ground.AssignedTaxiRoute = MakeRoute(MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: false));
        var ac2 = new AircraftState { Callsign = "A2", AircraftType = "C172" };
        ac2.Ground.AssignedTaxiRoute = MakeRoute(MakeHs(200, HoldShortReason.RunwayCrossing, isCleared: false));
        // No taxi route assigned — should be ignored, no NRE.
        var ac3 = new AircraftState { Callsign = "A3", AircraftType = "C172" };
        world.AddAircraft(ac1);
        world.AddAircraft(ac2);
        world.AddAircraft(ac3);

        world.ApplyAutoCrossToActiveTaxiRoutes(autoCross: true);

        Assert.True(ac1.Ground.AssignedTaxiRoute!.HoldShortPoints[0].IsCleared);
        Assert.True(ac1.Ground.AssignedTaxiRoute!.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(ac2.Ground.AssignedTaxiRoute!.HoldShortPoints[0].IsCleared);
        Assert.True(ac2.Ground.AssignedTaxiRoute!.HoldShortPoints[0].ClearedByAutoCross);
    }

    /// <summary>
    /// E2E: an aircraft already holding short of 28R/10L on B is taxied across via
    /// `TAXI B RWY 28L` with AutoCross OFF. The first-crossing-resume path clears the
    /// 28R/10L hold-short with ClearedByAutoCross=false (because AutoCross was off),
    /// so subsequently toggling AutoCross OFF again must NOT re-arm it. The aircraft
    /// must still successfully cross.
    /// </summary>
    [Fact]
    public void E2E_FirstCrossingClearance_SurvivesAutoCrossToggleOff()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load("TestData/10797ffbbfea.zip");
        if (recording is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        engine.Replay(recording, 673);

        // Force AutoCross OFF before issuing the TAXI so we can isolate the
        // first-crossing-resume clearance from the AutoCross blanket clearance.
        engine.Scenario!.AutoCrossRunway = false;

        var result = engine.SendCommand("N342T", "TAXI B RWY 28L");
        Assert.True(result.Success, $"TAXI command failed: {result.Message}");

        var aircraft = engine.FindAircraft("N342T");
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        var firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
        Assert.NotNull(firstCrossing);
        Assert.True(firstCrossing.IsCleared, "First-crossing-resume should clear the first RunwayCrossing");
        Assert.False(
            firstCrossing.ClearedByAutoCross,
            "First-crossing-resume must NOT mark the hold-short as ClearedByAutoCross — AutoCross was off"
        );

        // Now toggle AutoCross OFF again (it's already off, but exercise the helper).
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(autoCross: false);
        Assert.True(firstCrossing.IsCleared, "Toggle-off must NOT revert the first-crossing-resume clearance");

        // Toggle AutoCross ON: helper must skip the already-cleared first crossing.
        engine.Scenario!.AutoCrossRunway = true;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(autoCross: true);
        Assert.True(firstCrossing.IsCleared);
        Assert.False(firstCrossing.ClearedByAutoCross, "Already-cleared crossing must not be retroactively marked");

        // Toggle AutoCross OFF: still must not revert the first-crossing clearance.
        engine.Scenario!.AutoCrossRunway = false;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(autoCross: false);
        Assert.True(firstCrossing.IsCleared);
    }

    /// <summary>
    /// E2E: an aircraft currently sitting in HoldingShortPhase due to an implicit
    /// RunwayCrossing must not be popped out of its phase when AutoCross is toggled
    /// ON. Per agreed semantics, only future crossings get cleared — currently-stopped
    /// aircraft stay stopped.
    /// </summary>
    [Fact]
    public void E2E_ToggleOn_DoesNotPopAircraftCurrentlyInHoldingShortPhase()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load("TestData/10797ffbbfea.zip");
        if (recording is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        engine.Replay(recording, 673);

        var aircraft = engine.FindAircraft("N342T");
        Assert.NotNull(aircraft);

        // Precondition: the aircraft is in HoldingShortPhase at t=673.
        var priorPhase = aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(priorPhase);

        // Flip AutoCross ON via the world helper.
        engine.Scenario!.AutoCrossRunway = true;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(autoCross: true);

        // Aircraft must still be in HoldingShortPhase.
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
    }
}
