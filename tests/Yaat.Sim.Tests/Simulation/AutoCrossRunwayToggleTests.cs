using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Changing the auto-cross mode mid-session must update implicit RunwayCrossing hold-shorts
/// on aircraft that are already taxiing — turning it on pre-clears their remaining
/// crossings, turning it off re-arms only the crossings that were cleared by AutoCross
/// (preserving first-crossing-resume and explicit CROSS-keyword clearances). Under
/// <see cref="AutoCrossRunwayMode.InactiveOnly"/> only crossings of runways outside the
/// airport's active-runway list are pre-cleared, and a runway that becomes active re-arms its own.
/// </summary>
public class AutoCrossRunwayToggleTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<ActiveRunway> NoActiveRunways = [];

    private static HoldShortPoint MakeHs(int nodeId, HoldShortReason reason, bool isCleared, bool clearedByAutoCross = false) =>
        new()
        {
            NodeId = nodeId,
            Reason = reason,
            TargetName = reason == HoldShortReason.RunwayCrossing ? "28R" : "28L",
            IsCleared = isCleared,
            ClearedByAutoCross = clearedByAutoCross,
        };

    private static HoldShortPoint MakeCrossing(int nodeId, string runway, bool isCleared, bool clearedByAutoCross) =>
        new()
        {
            NodeId = nodeId,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = runway,
            IsCleared = isCleared,
            ClearedByAutoCross = clearedByAutoCross,
        };

    private static TaxiRoute MakeRoute(params HoldShortPoint[] holdShorts) => new() { Segments = [], HoldShortPoints = [.. holdShorts] };

    [Fact]
    public void Apply_ToggleOn_MarksUnclearedRunwayCrossings_AsClearedByAutoCross()
    {
        TaxiRoute route = MakeRoute(MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: false));

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.All, NoActiveRunways);

        HoldShortPoint hs = route.HoldShortPoints[0];
        Assert.True(hs.IsCleared);
        Assert.True(hs.ClearedByAutoCross);
    }

    [Fact]
    public void Apply_ToggleOn_DoesNotMarkAlreadyClearedCrossings()
    {
        // Already cleared by some other path (first-crossing or explicit CROSS keyword);
        // ClearedByAutoCross stays false so a future toggle-OFF won't revert it.
        TaxiRoute route = MakeRoute(MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: true));

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.All, NoActiveRunways);

        HoldShortPoint hs = route.HoldShortPoints[0];
        Assert.True(hs.IsCleared);
        Assert.False(hs.ClearedByAutoCross);
    }

    [Fact]
    public void Apply_ToggleOn_DoesNotTouchExplicitOrDestinationHoldShorts()
    {
        TaxiRoute route = MakeRoute(
            MakeHs(100, HoldShortReason.ExplicitHoldShort, isCleared: false),
            MakeHs(101, HoldShortReason.DestinationRunway, isCleared: false)
        );

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.All, NoActiveRunways);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.False(route.HoldShortPoints[1].IsCleared);
        Assert.False(route.HoldShortPoints[1].ClearedByAutoCross);
    }

    [Fact]
    public void Apply_ToggleOff_RevertsOnlyAutoCrossClearedCrossings()
    {
        TaxiRoute route = MakeRoute(
            // Cleared by AutoCross — must revert.
            MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: true, clearedByAutoCross: true),
            // Cleared by some other path (e.g. first-crossing resume) — must persist.
            MakeHs(101, HoldShortReason.RunwayCrossing, isCleared: true, clearedByAutoCross: false),
            // Already uncleared — no-op.
            MakeHs(102, HoldShortReason.RunwayCrossing, isCleared: false)
        );

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.Off, NoActiveRunways);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(route.HoldShortPoints[1].IsCleared);
        Assert.False(route.HoldShortPoints[1].ClearedByAutoCross);
        Assert.False(route.HoldShortPoints[2].IsCleared);
        Assert.False(route.HoldShortPoints[2].ClearedByAutoCross);
    }

    [Fact]
    public void Apply_InactiveOnly_ClearsOnlyCrossingsOfInactiveRunways()
    {
        TaxiRoute route = MakeRoute(
            MakeCrossing(100, "28R", isCleared: false, clearedByAutoCross: false),
            MakeCrossing(101, "01L", isCleared: false, clearedByAutoCross: false)
        );
        IReadOnlyList<ActiveRunway> active = [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("28R", ActiveRunwayUse.Both)];

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, active);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(route.HoldShortPoints[1].IsCleared);
        Assert.True(route.HoldShortPoints[1].ClearedByAutoCross);
    }

    [Fact]
    public void Apply_InactiveOnly_ActiveForArrivalOnly_StillCountsAsActive()
    {
        TaxiRoute route = MakeRoute(MakeCrossing(100, "28R", isCleared: false, clearedByAutoCross: false));
        IReadOnlyList<ActiveRunway> active = [new ActiveRunway("28R", ActiveRunwayUse.Arrival)];

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, active);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
    }

    [Fact]
    public void Apply_InactiveOnly_NoActiveRunways_ClearsEveryCrossing()
    {
        TaxiRoute route = MakeRoute(
            MakeCrossing(100, "28R", isCleared: false, clearedByAutoCross: false),
            MakeCrossing(101, "01L", isCleared: false, clearedByAutoCross: false)
        );

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, NoActiveRunways);

        Assert.All(
            route.HoldShortPoints,
            hs =>
            {
                Assert.True(hs.IsCleared);
                Assert.True(hs.ClearedByAutoCross);
            }
        );
    }

    [Fact]
    public void Apply_InactiveOnly_RunwayBecomesActive_RevertsOnlyItsAutoClearance()
    {
        TaxiRoute route = MakeRoute(
            MakeCrossing(100, "28R", isCleared: false, clearedByAutoCross: false),
            MakeCrossing(101, "01L", isCleared: false, clearedByAutoCross: false),
            // Cleared by an explicit CROSS: never the auto-cross's to revert.
            MakeCrossing(102, "28R", isCleared: true, clearedByAutoCross: false)
        );
        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, NoActiveRunways);
        Assert.True(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(route.HoldShortPoints[1].ClearedByAutoCross);

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, [new ActiveRunway("28R", ActiveRunwayUse.Both)]);

        Assert.False(route.HoldShortPoints[0].IsCleared);
        Assert.False(route.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(route.HoldShortPoints[1].IsCleared);
        Assert.True(route.HoldShortPoints[1].ClearedByAutoCross);
        Assert.True(route.HoldShortPoints[2].IsCleared);
        Assert.False(route.HoldShortPoints[2].ClearedByAutoCross);
    }

    [Fact]
    public void World_ApplyAutoCrossToActiveTaxiRoutes_UpdatesEveryTaxiingAircraft()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        var ac1 = new AircraftState { Callsign = "A1", AircraftType = "C172" };
        ac1.Ground.AssignedTaxiRoute = MakeRoute(MakeHs(100, HoldShortReason.RunwayCrossing, isCleared: false));
        var ac2 = new AircraftState { Callsign = "A2", AircraftType = "C172" };
        ac2.Ground.AssignedTaxiRoute = MakeRoute(MakeHs(200, HoldShortReason.RunwayCrossing, isCleared: false));
        // No taxi route assigned — should be ignored, no NRE.
        var ac3 = new AircraftState { Callsign = "A3", AircraftType = "C172" };
        world.AddAircraft(ac1);
        world.AddAircraft(ac2);
        world.AddAircraft(ac3);

        world.ApplyAutoCrossToActiveTaxiRoutes(AutoCrossRunwayMode.All, ActiveRunways.Empty);

        Assert.True(ac1.Ground.AssignedTaxiRoute!.HoldShortPoints[0].IsCleared);
        Assert.True(ac1.Ground.AssignedTaxiRoute!.HoldShortPoints[0].ClearedByAutoCross);
        Assert.True(ac2.Ground.AssignedTaxiRoute!.HoldShortPoints[0].IsCleared);
        Assert.True(ac2.Ground.AssignedTaxiRoute!.HoldShortPoints[0].ClearedByAutoCross);
    }

    /// <summary>
    /// SFO in a 28 configuration (28L/28R active) under Inactive runways only: a TAXI from the B/H junction to 28R via
    /// B H F C crosses 1L, 1R and 28L. The 1L and 1R crossings are pre-cleared; the 28L crossing still needs a CROSS.
    /// </summary>
    [Fact]
    public void Taxi_InactiveOnly_Sfo28Configuration_PreClearsOnlyThe1L1RCrossings()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.InactiveOnly) is not { } ground)
        {
            return;
        }

        ground.Engine.Scenario!.ActiveRunways = ActiveRunways.Empty.With(
            "SFO",
            [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("28R", ActiveRunwayUse.Both)]
        );
        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "N596A", "B738", "B", "H");

        CommandResult result = ground.Engine.SendCommand("N596A", "TAXI B H F C RWY 28R");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.AssertHoldShorts(
            output,
            route,
            ("1L", HoldShortReason.RunwayCrossing, true),
            ("1R", HoldShortReason.RunwayCrossing, true),
            ("28L", HoldShortReason.RunwayCrossing, false),
            ("28R", HoldShortReason.DestinationRunway, false)
        );
        Assert.All(
            route.HoldShortPoints.Where(hs => hs.IsCleared),
            hs => Assert.True(hs.ClearedByAutoCross, $"{hs.TargetName} cleared, but not by the auto-cross")
        );
    }

    /// <summary>
    /// An <c>ARWY</c> that makes a crossed runway active re-checks the routes already assigned under Inactive runways
    /// only: that runway's auto-clearance reverts, the other crossings stay cleared.
    /// </summary>
    [Fact]
    public void ArwyChange_UnderInactiveOnly_RechecksAssignedRoutes()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.InactiveOnly) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "N596B", "B738", "B", "H");
        CommandResult taxi = ground.Engine.SendCommand("N596B", "TAXI B H F C RWY 28R");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.All(route.HoldShortPoints.Where(hs => hs.Reason == HoldShortReason.RunwayCrossing), hs => Assert.True(hs.IsCleared));

        CommandResult arwy = ground.Engine.Actions.Issue(new ActionInput("", "ARWY SFO 28L 28R", "conn-1", "XX", Baked: null)).Result;

        Assert.True(arwy.Success, arwy.Message);
        SfoGroundHarness.AssertHoldShorts(
            output,
            route,
            ("1L", HoldShortReason.RunwayCrossing, true),
            ("1R", HoldShortReason.RunwayCrossing, true),
            ("28L", HoldShortReason.RunwayCrossing, false),
            ("28R", HoldShortReason.DestinationRunway, false)
        );
    }

    /// <summary>
    /// An <c>ARWY</c> that takes a crossed runway out of the active list re-checks the routes already assigned under Inactive
    /// runways only: that runway's crossing is pre-cleared by the auto-cross.
    /// </summary>
    [Fact]
    public void ArwyChange_RunwayLeavesTheActiveList_PreClearsItsCrossing()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.InactiveOnly) is not { } ground)
        {
            return;
        }

        ground.Engine.Scenario!.ActiveRunways = ActiveRunways.Empty.With(
            "SFO",
            [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("28R", ActiveRunwayUse.Both)]
        );
        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "N596C", "B738", "B", "H");
        CommandResult taxi = ground.Engine.SendCommand("N596C", "TAXI B H F C RWY 28R");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint crossing28L = route.HoldShortPoints.Single(hs =>
            (hs.Reason == HoldShortReason.RunwayCrossing) && hs.TargetName!.Contains("28L")
        );
        Assert.False(crossing28L.IsCleared, "28L is active, so its crossing needs a CROSS");

        CommandResult arwy = ground.Engine.Actions.Issue(new ActionInput("", "ARWY SFO 28R", "conn-1", "XX", Baked: null)).Result;

        Assert.True(arwy.Success, arwy.Message);
        Assert.True(crossing28L.IsCleared, "28L left the active list, but its crossing was not pre-cleared");
        Assert.True(crossing28L.ClearedByAutoCross);
    }

    /// <summary>
    /// A crossing cleared by an explicit <c>CROSS</c> on the TAXI is the controller's clearance, not the auto-cross's: an
    /// <c>ARWY</c> that makes its runway active under Inactive runways only leaves it cleared.
    /// </summary>
    [Fact]
    public void ArwyChange_UnderInactiveOnly_KeepsACrossingClearedByAnExplicitCross()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.InactiveOnly) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "N596D", "B738", "B", "H");
        CommandResult taxi = ground.Engine.SendCommand("N596D", "TAXI B H F C RWY 28R CROSS 28L");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint crossing28L = route.HoldShortPoints.Single(hs =>
            (hs.Reason == HoldShortReason.RunwayCrossing) && hs.TargetName!.Contains("28L")
        );
        Assert.True(crossing28L.IsCleared);
        Assert.False(crossing28L.ClearedByAutoCross, "the explicit CROSS 28L is the crossing's clearance");

        CommandResult arwy = ground.Engine.Actions.Issue(new ActionInput("", "ARWY SFO 28L 28R", "conn-1", "XX", Baked: null)).Result;

        Assert.True(arwy.Success, arwy.Message);
        Assert.True(crossing28L.IsCleared, "the ARWY reverted a crossing cleared by an explicit CROSS");
    }

    /// <summary>
    /// The route's airport is the layout the TAXI was resolved on, even when the aircraft carries no layout of its own: under
    /// Inactive runways only, the crossing of a runway active at SFO still needs a CROSS.
    /// </summary>
    [Fact]
    public void Taxi_InactiveOnly_AircraftWithoutItsOwnLayout_LeavesActiveRunwayCrossings()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.InactiveOnly) is not { } ground)
        {
            return;
        }

        ground.Engine.Scenario!.ActiveRunways = ActiveRunways.Empty.With(
            "SFO",
            [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("28R", ActiveRunwayUse.Both)]
        );
        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "N596E", "B738", "B", "H");
        aircraft.Ground.Layout = null;

        CommandResult result = ground.Engine.SendCommand("N596E", "TAXI B H F C RWY 28R");

        Assert.True(result.Success, result.Message);
        SfoGroundHarness.AssertHoldShorts(
            output,
            Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute),
            ("1L", HoldShortReason.RunwayCrossing, true),
            ("1R", HoldShortReason.RunwayCrossing, true),
            ("28L", HoldShortReason.RunwayCrossing, false),
            ("28R", HoldShortReason.DestinationRunway, false)
        );
    }

    /// <summary>
    /// The world's re-check takes each aircraft's airport from its stored layout reference when no layout is attached, and
    /// under Inactive runways only leaves the routes of an aircraft on an unknown airport as they are.
    /// </summary>
    [Fact]
    public void World_InactiveOnly_UsesTheStoredAirport_AndLeavesAnUnknownAirportsRoutes()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        var atSfo = new AircraftState { Callsign = "A1", AircraftType = "C172" };
        atSfo.Ground.LayoutAirportId = "SFO";
        atSfo.Ground.AssignedTaxiRoute = MakeRoute(MakeCrossing(100, "28R", isCleared: true, clearedByAutoCross: true));
        var unknown = new AircraftState { Callsign = "A2", AircraftType = "C172" };
        unknown.Ground.AssignedTaxiRoute = MakeRoute(
            MakeCrossing(200, "28R", isCleared: false, clearedByAutoCross: false),
            MakeCrossing(201, "01L", isCleared: true, clearedByAutoCross: true)
        );
        world.AddAircraft(atSfo);
        world.AddAircraft(unknown);

        world.ApplyAutoCrossToActiveTaxiRoutes(
            AutoCrossRunwayMode.InactiveOnly,
            ActiveRunways.Empty.With("SFO", [new ActiveRunway("28R", ActiveRunwayUse.Both)])
        );

        Assert.False(atSfo.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared, "28R is active at SFO, the aircraft's stored airport");
        Assert.False(unknown.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared, "an unknown airport's crossing was pre-cleared");
        Assert.True(unknown.Ground.AssignedTaxiRoute.HoldShortPoints[1].IsCleared, "an unknown airport's crossing was reverted");
    }

    /// <summary>
    /// A crossing whose runway has no name cannot be told inactive: under Inactive runways only it needs a CROSS while the
    /// airport has any active runway.
    /// </summary>
    [Fact]
    public void Apply_InactiveOnly_UnnamedCrossing_IsActiveWhileAnyRunwayIsActive()
    {
        TaxiRoute route = MakeRoute(MakeCrossing(100, "", isCleared: false, clearedByAutoCross: false));

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, [new ActiveRunway("01L", ActiveRunwayUse.Departure)]);

        Assert.False(route.HoldShortPoints[0].IsCleared);
    }

    /// <summary>With no active runways every runway is inactive, the unnamed one too, so its crossing is pre-cleared.</summary>
    [Fact]
    public void Apply_InactiveOnly_UnnamedCrossing_NoActiveRunways_IsPreCleared()
    {
        TaxiRoute route = MakeRoute(MakeCrossing(100, "", isCleared: false, clearedByAutoCross: false));

        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.InactiveOnly, NoActiveRunways);

        Assert.True(route.HoldShortPoints[0].IsCleared);
        Assert.True(route.HoldShortPoints[0].ClearedByAutoCross);
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

        SessionRecording? recording = RecordingLoader.Load("TestData/10797ffbbfea.zip");
        if (recording is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());

        engine.Replay(recording, 673);

        // Force AutoCross OFF before issuing the TAXI so we can isolate the
        // first-crossing-resume clearance from the AutoCross blanket clearance.
        engine.Scenario!.AutoCrossRunwayMode = AutoCrossRunwayMode.Off;

        CommandResult result = engine.SendCommand("N342T", "TAXI B RWY 28L");
        Assert.True(result.Success, $"TAXI command failed: {result.Message}");

        AircraftState? aircraft = engine.FindAircraft("N342T");
        Assert.NotNull(aircraft);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        HoldShortPoint? firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
        Assert.NotNull(firstCrossing);
        Assert.True(firstCrossing.IsCleared, "First-crossing-resume should clear the first RunwayCrossing");
        Assert.False(
            firstCrossing.ClearedByAutoCross,
            "First-crossing-resume must NOT mark the hold-short as ClearedByAutoCross — AutoCross was off"
        );

        // Now toggle AutoCross OFF again (it's already off, but exercise the helper).
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(AutoCrossRunwayMode.Off, engine.Scenario.ActiveRunways);
        Assert.True(firstCrossing.IsCleared, "Toggle-off must NOT revert the first-crossing-resume clearance");

        // Toggle AutoCross ON: helper must skip the already-cleared first crossing.
        engine.Scenario!.AutoCrossRunwayMode = AutoCrossRunwayMode.All;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(AutoCrossRunwayMode.All, engine.Scenario.ActiveRunways);
        Assert.True(firstCrossing.IsCleared);
        Assert.False(firstCrossing.ClearedByAutoCross, "Already-cleared crossing must not be retroactively marked");

        // Toggle AutoCross OFF: still must not revert the first-crossing clearance.
        engine.Scenario!.AutoCrossRunwayMode = AutoCrossRunwayMode.Off;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(AutoCrossRunwayMode.Off, engine.Scenario.ActiveRunways);
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

        SessionRecording? recording = RecordingLoader.Load("TestData/10797ffbbfea.zip");
        if (recording is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());

        engine.Replay(recording, 673);

        AircraftState? aircraft = engine.FindAircraft("N342T");
        Assert.NotNull(aircraft);

        // Precondition: the aircraft is in HoldingShortPhase at t=673.
        var priorPhase = aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(priorPhase);

        // Flip AutoCross ON via the world helper.
        engine.Scenario!.AutoCrossRunwayMode = AutoCrossRunwayMode.All;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(AutoCrossRunwayMode.All, engine.Scenario.ActiveRunways);

        // Aircraft must still be in HoldingShortPhase.
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
    }
}
