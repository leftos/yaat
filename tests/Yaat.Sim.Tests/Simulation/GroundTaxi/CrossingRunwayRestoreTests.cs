using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A runway crossing restored from a snapshot taken part-way along its slice goes on along the same slice segment as the
/// run that was never interrupted, including when the snapshot is itself taken from a restored crossing that has not yet
/// ticked. Every snapshot goes through the recording JSON before it is restored.
///
/// <para>The geometry is <see cref="OakCrossThenContinueTests"/>': a C560 on KOAK taxiway B, cleared <c>TAXI B W 30</c>,
/// holds short of 28R and is cleared <c>CROSS</c>.</para>
/// </summary>
public class CrossingRunwayRestoreTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AircraftType = "C560";
    private const string AirportId = "OAK";
    private const int HoldBudgetSeconds = 300;
    private const int CosHoldBudgetSeconds = 900;
    private const int CrossingBudgetSeconds = 120;
    private const int CompareSeconds = 30;
    private const double MovingKts = 1.0;
    private const double RestKts = 0.5;

    /// <summary>
    /// A snapshot taken while the crossing is on slice segment 1 or later, restored into a second engine, keeps the
    /// restored aircraft on the original's phase and position every second.
    /// </summary>
    [Fact]
    public void MidCrossing_AtALaterSegment_RestoresOnTheSameSegment()
    {
        var groundData = new TestAirportGroundData();
        if (StartCrossing(groundData) is not { } engine)
        {
            return;
        }

        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        restoredEngine.RestoreFromSnapshot(RoundTrip(engine.CaptureSnapshot()));

        AssertTracksTheOriginal(engine, restoredEngine);
    }

    /// <summary>
    /// A restored crossing snapshotted again before its first tick — before it has rebuilt its slice or navigator — still
    /// carries the slice segment it was on and the pose that segment was set up at, so a second restore goes on along the
    /// same segment as the original rather than starting the slice again from segment 0. The second restore takes the
    /// anchored set-up path (<c>CrossingRunwayPhase.SetupRestoredSegment</c>) from the <c>SegmentSetup*</c> fields.
    /// </summary>
    [Fact]
    public void SnapshotBeforeFirstTick_RestoresAtTheSameSegment()
    {
        var groundData = new TestAirportGroundData();
        if (StartCrossing(groundData) is not { } engine)
        {
            return;
        }

        CrossingRunwayPhaseDto original = CrossingDto(engine);
        SimulationEngine firstRestore = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        firstRestore.RestoreFromSnapshot(RoundTrip(engine.CaptureSnapshot()));

        CrossingRunwayPhaseDto untickedResnapshot = CrossingDto(firstRestore);
        output.WriteLine(
            $"original seg={original.CrossingRouteSegmentIndex} setup=({original.SegmentSetupLat},{original.SegmentSetupLon},"
                + $"{original.SegmentSetupHeadingDeg}); re-snapshot seg={untickedResnapshot.CrossingRouteSegmentIndex} "
                + $"setup=({untickedResnapshot.SegmentSetupLat},{untickedResnapshot.SegmentSetupLon},{untickedResnapshot.SegmentSetupHeadingDeg})"
        );
        Assert.Equal(original.CrossingRouteSegmentIndex, untickedResnapshot.CrossingRouteSegmentIndex);
        Assert.Equal(original.SegmentSetupLat, untickedResnapshot.SegmentSetupLat);
        Assert.Equal(original.SegmentSetupLon, untickedResnapshot.SegmentSetupLon);
        Assert.Equal(original.SegmentSetupHeadingDeg, untickedResnapshot.SegmentSetupHeadingDeg);
        Assert.NotNull(untickedResnapshot.SegmentSetupLat);
        Assert.NotNull(untickedResnapshot.TailClearanceSuppressed);
        Assert.NotNull(untickedResnapshot.TailFullSegmentCount);
        Assert.NotNull(untickedResnapshot.TailCutFt);
        Assert.Equal(original.TailClearanceSuppressed, untickedResnapshot.TailClearanceSuppressed);
        Assert.Equal(original.TailFullSegmentCount, untickedResnapshot.TailFullSegmentCount);
        Assert.Equal(original.TailCutFt, untickedResnapshot.TailCutFt);

        SimulationEngine secondRestore = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        secondRestore.RestoreFromSnapshot(RoundTrip(firstRestore.CaptureSnapshot()));

        AssertTracksTheOriginal(engine, secondRestore);
    }

    /// <summary>
    /// YAAT-1464: KCOS <c>TAXI C2 A2 A HS A</c> for a C172
    /// (as <see cref="Issue899DetourConnectorTests.CosC2A2_ExplicitHsA_HoldsShortOfAPastThe35LBar"/>), snapshotted while its
    /// crossing of 17R/35L is braking onto the stop for A below its crossing speed, restored through the recording JSON, comes
    /// to rest holding short of A where the run that was never interrupted does, within 0.01 ft.
    /// </summary>
    [Fact]
    public void MidBrakingOntoTheStopPastTheCrossing_RestsWhereTheOriginalDoes()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is not { } navDb) || (groundData.GetLayout("COS") is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KCOS layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        string runway = Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "35L");
        (GroundNode start, GroundNode join) = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "COS", "C2", runway);
        SimulationEngine engine = NewCosEngine(groundData);
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "COS", start, GeoMath.BearingTo(start.Position, join.Position));
        engine.World.AddAircraft(aircraft);
        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI C2 A2 A HS A");
        Assert.True(taxi.Success, taxi.Message);
        Assert.True(TickUntil(engine, CosHoldBudgetSeconds, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase), "never held short of 35L");
        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);

        double floorKts = CategoryPerformance.RunwayCrossingSpeed(AircraftCategorization.Categorize(aircraft.AircraftType));
        bool reachedFloor = false;
        bool braking = TickUntil(
            engine,
            CrossingBudgetSeconds,
            () =>
            {
                bool crossing = aircraft.Phases?.CurrentPhase is CrossingRunwayPhase;
                reachedFloor |= crossing && (aircraft.GroundSpeed >= floorKts - MovingKts);
                return crossing && reachedFloor && (aircraft.GroundSpeed < floorKts - (2.0 * MovingKts));
            }
        );
        Assert.True(braking, $"never caught braking below the {floorKts:F0} kt floor in the crossing: {aircraft.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"snapshot mid-braking: gs={aircraft.GroundSpeed:F2} at {aircraft.Position}");

        SimulationEngine restoredEngine = NewCosEngine(groundData);
        restoredEngine.RestoreFromSnapshot(RoundTrip(engine.CaptureSnapshot()));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Issue1334OakDToFAcross33Tests.Callsign));
        bool atRest = TickUntil(
            engine,
            HoldBudgetSeconds,
            () =>
            {
                restoredEngine.TickOneSecond();
                return (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: "A" }) && (aircraft.GroundSpeed < RestKts);
            }
        );
        Assert.True(atRest, $"never came to rest holding short of A: {aircraft.Phases?.CurrentPhase?.Name}");

        double apartFt = GeoMath.DistanceNm(aircraft.Position, restored.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"at rest {apartFt:F4} ft from the uninterrupted run's rest");
        Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
        Assert.True(apartFt <= 0.01, $"the restored run rests {apartFt:F4} ft from the uninterrupted run's rest");
    }

    /// <summary>
    /// YAAT-1464: the KCOS crossing of <see cref="MidBrakingOntoTheStopPastTheCrossing_RestsWhereTheOriginalDoes"/>, on a route that
    /// runs on along A past the hold short of A (<c>TAXI C2 A2 A A3</c>, <c>HS A</c>), with that hold cleared mid-crossing on the
    /// route's own hold point, as the scenario auto-cross clears one. The crossing searches its stop afresh every tick, so it no
    /// longer brakes for A: from the far marking to the end of its slice its speed stays within half a knot of its crossing speed.
    /// </summary>
    [Fact]
    public void HoldClearedMidCrossing_NoLongerBrakesForIt()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is not { } navDb) || (groundData.GetLayout("COS") is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KCOS layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        (SimulationEngine engine, AircraftState aircraft, HoldShortPoint holdA) = HoldShortOf35LBeforeA(groundData, layout);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int entryBarId = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase).HoldShort.NodeId;
        int farBarSegment = route.Segments.FindIndex(s =>
            (s.FromNodeId > 0)
            && (s.FromNodeId != entryBarId)
            && (layout.Nodes[s.FromNodeId].Type == GroundNodeType.RunwayHoldShort)
            && (layout.Nodes[s.FromNodeId].RunwayId?.Contains("35L") == true)
        );
        Assert.True(farBarSegment >= 0, "the route never leaves a far 35L bar");
        GroundNode farBar = layout.Nodes[route.Segments[farBarSegment].FromNodeId];
        var onward = new TrueHeading(GeoMath.BearingTo(farBar.Position, route.Segments[farBarSegment].Edge.ToNode.Position));
        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);

        // Sampled from the far marking on: a turn on the runway caps the speed below the crossing speed, as it always may.
        double minKts = ClearSpeedKts(aircraft);
        bool crossing = false;
        bool cleared = false;
        double lowestKts = double.PositiveInfinity;
        string lowestAt = "none";
        AircraftState? handedBack = SubTickDriver.TickSubTicksUntil(
            engine,
            Issue1334OakDToFAcross33Tests.Callsign,
            ac => crossing && (ac.Phases?.CurrentPhase is not CrossingRunwayPhase),
            CrossingBudgetSeconds,
            ac =>
            {
                bool inCrossing = ac.Phases?.CurrentPhase is CrossingRunwayPhase;
                crossing |= inCrossing;
                bool pastFarBar = GeoMath.AlongTrackDistanceNm(ac.Position, farBar.Position, onward) > 0.0;
                if (inCrossing && cleared && pastFarBar && (ac.GroundSpeed < lowestKts))
                {
                    lowestKts = ac.GroundSpeed;
                    double fromBarFt = GeoMath.DistanceNm(ac.Position, layout.Nodes[holdA.NodeId].Position) * GeoMath.FeetPerNm;
                    lowestAt = $"{fromBarFt:F1} ft from A's bar";
                }

                cleared |= inCrossing && ClearOnceAtSpeed(ac, holdA, cleared);
            }
        );

        Assert.NotNull(handedBack);
        Assert.True(cleared, $"never reached {minKts:F1} kt in the crossing to clear the hold short of A");
        Assert.True(double.IsFinite(lowestKts), $"the crossing ended before the aircraft passed the far marking #{farBar.Id}");
        output.WriteLine($"lowest speed in the crossing after the clearance: {lowestKts:F2} kt at {lowestAt}");
        Assert.True(lowestKts >= minKts, $"the crossing slowed to {lowestKts:F2} kt at {lowestAt} after A was cleared, below {minKts:F1} kt");
    }

    /// <summary>
    /// YAAT-1464: the crossing of <see cref="HoldClearedMidCrossing_NoLongerBrakesForIt"/>, its hold short of A cleared
    /// mid-crossing, snapshotted on its exit segment before it hands the route back and restored into a second engine. The
    /// original decided its tail clearance while A still bound; the restored crossing keeps that decision rather than deciding
    /// again with A cleared, so it tracks the original's phase and position every second until the original comes to rest.
    /// </summary>
    [Fact]
    public void HoldClearedMidCrossing_RestoredOnTheExitSegment_TracksTheOriginal()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is not { } navDb) || (groundData.GetLayout("COS") is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KCOS layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        (SimulationEngine engine, AircraftState aircraft, HoldShortPoint holdA) = HoldShortOf35LBeforeA(groundData, layout);
        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);

        bool cleared = false;
        AircraftState? onExit = SubTickDriver.TickSubTicksUntil(
            engine,
            Issue1334OakDToFAcross33Tests.Callsign,
            ac => cleared && OnTheExitSegment(ac),
            CrossingBudgetSeconds,
            ac => cleared |= (ac.Phases?.CurrentPhase is CrossingRunwayPhase) && ClearOnceAtSpeed(ac, holdA, cleared)
        );
        Assert.True(cleared, "never reached the crossing speed to clear the hold short of A");
        Assert.NotNull(onExit);
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"snapshot on the exit segment: slice of {crossing.CrossingRoute?.Segments.Count} segments at {aircraft.Position}");

        SimulationEngine restoredEngine = NewCosEngine(groundData);
        restoredEngine.RestoreFromSnapshot(RoundTrip(engine.CaptureSnapshot()));
        AssertTracks(engine, restoredEngine, Issue1334OakDToFAcross33Tests.Callsign, HoldBudgetSeconds, static ac => ac.GroundSpeed < RestKts);
    }

    /// <summary>
    /// A C172 at KCOS on <c>TAXI C2 A2 A A3</c> with <c>HS A</c>, ticked until it holds short of 17R/35L on C2: the engine, the
    /// aircraft and the route's hold short of A.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft, HoldShortPoint HoldA) HoldShortOf35LBeforeA(
        TestAirportGroundData groundData,
        AirportGroundLayout layout
    )
    {
        string runway = Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "35L");
        (GroundNode start, GroundNode join) = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "COS", "C2", runway);
        SimulationEngine engine = NewCosEngine(groundData);
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "COS", start, GeoMath.BearingTo(start.Position, join.Position));
        engine.World.AddAircraft(aircraft);
        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI C2 A2 A A3");
        Assert.True(taxi.Success, taxi.Message);
        CommandResult hs = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "HS A");
        Assert.True(hs.Success, hs.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint holdA = Assert.IsType<HoldShortPoint>(route.HoldShortPoints.FirstOrDefault(h => (h.TargetName == "A") && !h.IsCleared));
        Assert.True(TickUntil(engine, CosHoldBudgetSeconds, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase), "never held short of 35L");
        return (engine, aircraft, holdA);
    }

    /// <summary>The crossing speed less half a knot: the speed a crossing that is not braking for a stop keeps.</summary>
    private static double ClearSpeedKts(AircraftState aircraft) =>
        CategoryPerformance.RunwayCrossingSpeed(AircraftCategorization.Categorize(aircraft.AircraftType)) - 0.5;

    /// <summary>
    /// Clears <paramref name="holdA"/> on the route's own hold point, as the scenario auto-cross clears one, the first time the
    /// aircraft reaches <see cref="ClearSpeedKts"/>; true once cleared.
    /// </summary>
    private static bool ClearOnceAtSpeed(AircraftState aircraft, HoldShortPoint holdA, bool alreadyCleared)
    {
        if (alreadyCleared || (aircraft.GroundSpeed < ClearSpeedKts(aircraft)))
        {
            return alreadyCleared;
        }

        holdA.IsCleared = true;
        holdA.ClearedByAutoCross = true;
        return true;
    }

    /// <summary>True while the aircraft's crossing is on the slice segment that ends at its exit node.</summary>
    private static bool OnTheExitSegment(AircraftState aircraft) =>
        (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase crossing)
        && (crossing.CrossingRoute is { CurrentSegment: { } segment })
        && (segment.ToNodeId == crossing.TargetNodeId);

    /// <summary>An engine on the KCOS scenario of <see cref="MidBrakingOntoTheStopPastTheCrossing_RestsWhereTheOriginalDoes"/>.</summary>
    private static SimulationEngine NewCosEngine(TestAirportGroundData groundData) =>
        new(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-yaat-1464-cos-crossing-restore",
                ScenarioName = "COS C2/A2 across 17R/35L, HS A, restored mid-braking",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "COS",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };

    /// <summary>Tick both engines together and require the restored aircraft on the original's phase and position each second.</summary>
    private void AssertTracksTheOriginal(SimulationEngine engine, SimulationEngine restoredEngine) =>
        AssertTracks(engine, restoredEngine, Callsign, CompareSeconds, static _ => false);

    /// <summary>
    /// Tick both engines together, requiring <paramref name="callsign"/> in the restored engine on the original's phase and
    /// position each second, for <paramref name="seconds"/> seconds or until <paramref name="done"/> holds for the original.
    /// </summary>
    private void AssertTracks(SimulationEngine engine, SimulationEngine restoredEngine, string callsign, int seconds, Func<AircraftState, bool> done)
    {
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(callsign));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(callsign));
        int ticked = 0;
        for (int second = 1; second <= seconds; second++)
        {
            ticked = second;
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(aircraft.Position, restored.Position);
            if (done(aircraft))
            {
                break;
            }
        }

        output.WriteLine($"after {ticked}s: {aircraft.Phases?.CurrentPhase?.Name} at {aircraft.Position}");
    }

    /// <summary>
    /// An engine whose aircraft is crossing 28R, moving, on slice segment 1 or later; null when navdata or the KOAK layout
    /// is unavailable.
    /// </summary>
    private SimulationEngine? StartCrossing(TestAirportGroundData groundData)
    {
        if ((NewEngine(groundData) is not { } engine) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return null;
        }

        List<GroundNode> bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        List<GroundNode> bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B");
        GroundNode nearBar28R = bar28R.OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position)).First();
        GroundNode start = nearBar28R
            .Edges.Where(edge => string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.OtherNode(nearBar28R))
            .OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position))
            .First();

        engine.World.AddAircraft(Spawn(start, new TrueHeading(GeoMath.BearingTo(start.Position, nearBar28R.Position)), layout));
        CommandResult taxi = engine.SendCommand(Callsign, "TAXI B W 30");
        Assert.True(taxi.Success, taxi.Message);

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        Assert.True(TickUntil(engine, HoldBudgetSeconds, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase), "never held short of 28R");
        CommandResult cross = engine.SendCommand(Callsign, "CROSS");
        Assert.True(cross.Success, cross.Message);

        bool onLaterSegment = TickUntil(
            engine,
            CrossingBudgetSeconds,
            () =>
                (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase crossing)
                && (((CrossingRunwayPhaseDto)crossing.ToSnapshot()).CrossingRouteSegmentIndex >= 1)
                && (aircraft.GroundSpeed > MovingKts)
        );
        Assert.True(onLaterSegment, $"never caught crossing on slice segment 1 or later: {aircraft.Phases?.CurrentPhase?.Name}");
        output.WriteLine(
            $"crossing on seg={CrossingDto(engine).CrossingRouteSegmentIndex} gs={aircraft.GroundSpeed:F1} "
                + $"hdg={aircraft.TrueHeading.Degrees:F1} at {aircraft.Position}"
        );
        return engine;
    }

    private static bool TickUntil(SimulationEngine engine, int budgetSeconds, Func<bool> done)
    {
        for (int second = 1; second <= budgetSeconds; second++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return true;
            }
        }

        return false;
    }

    private static CrossingRunwayPhaseDto CrossingDto(SimulationEngine engine)
    {
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
        return Assert.IsType<CrossingRunwayPhaseDto>(crossing.ToSnapshot());
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    private SimulationEngine? NewEngine(TestAirportGroundData groundData)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-crossing-restore",
                ScenarioName = "OAK crossing restore",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
    }

    private static AircraftState Spawn(GroundNode node, TrueHeading heading, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
