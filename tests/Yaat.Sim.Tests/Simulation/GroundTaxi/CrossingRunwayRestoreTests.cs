using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
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
    private const int CrossingBudgetSeconds = 120;
    private const int CompareSeconds = 30;
    private const double MovingKts = 1.0;

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

        SimulationEngine secondRestore = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        secondRestore.RestoreFromSnapshot(RoundTrip(firstRestore.CaptureSnapshot()));

        AssertTracksTheOriginal(engine, secondRestore);
    }

    /// <summary>Tick both engines together and require the restored aircraft on the original's phase and position each second.</summary>
    private void AssertTracksTheOriginal(SimulationEngine engine, SimulationEngine restoredEngine)
    {
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));
        for (int second = 1; second <= CompareSeconds; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(aircraft.Position, restored.Position);
        }

        output.WriteLine($"after {CompareSeconds}s: {aircraft.Phases?.CurrentPhase?.Name} at {aircraft.Position}");
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
        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-crossing-restore",
                ScenarioName = "OAK crossing restore",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunway = false,
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
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
