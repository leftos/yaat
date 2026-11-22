using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Under the ramp turn model physics turns the nose between navigator ticks, and the navigator adds that turn to its net
/// turn from the heading it remembered at the end of its last tick. A snapshot carries that heading with the navigator's
/// playback, so a taxi restored while physics turns the nose counts the same turn on its next tick as the run that was never
/// interrupted. Where the navigator writes the heading itself (the instant turn model, a helicopter) the snapshot carries
/// none. Every case taxis on real KOAK taxiways: an aircraft standing on B square across it, cleared <c>TAXI B W 30</c>.
/// </summary>
public class GroundNavigatorHeadingMemoryRestoreTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AirportId = "OAK";
    private const int TurnBudgetSeconds = 60;
    private const int MidRunSeconds = 30;
    private const double MinPhysicsTurnRateDegPerSec = 1.0;

    /// <summary>One physics sub-tick, which runs one navigator tick.</summary>
    private const double SubTickSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;

    [Fact]
    public void RampTaxi_RestoredWhilePhysicsTurnsTheNose_CountsTheSameTurnOnItsNextTick()
    {
        var groundData = new TestAirportGroundData();
        if (Taxi(groundData, TurnModel.Ramp, "C560") is not { } taxi)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = taxi;
        Assert.True(TickUntilPhysicsTurnsTheNose(engine, aircraft) > 0, $"physics never turned the nose off an arc in {TurnBudgetSeconds}s");
        StateSnapshotDto live = engine.CaptureSnapshot();
        GroundNavigatorPlaybackDto before = Playback(live);
        double liveHeading = Assert.IsType<double>(before.HeadingAfterLastTickDeg);

        StateSnapshotDto snapshot = RoundTrip(live);
        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(groundData, TurnModel.Ramp));
        restoredEngine.RestoreFromSnapshot(snapshot);
        double restoredHeading = Assert.IsType<double>(Playback(restoredEngine.CaptureSnapshot()).HeadingAfterLastTickDeg);
        Assert.Equal(BitConverter.DoubleToInt64Bits(liveHeading), BitConverter.DoubleToInt64Bits(restoredHeading));

        engine.TickPhysics(SubTickSeconds);
        restoredEngine.TickPhysics(SubTickSeconds);
        GroundNavigatorPlaybackDto liveAfter = Playback(engine.CaptureSnapshot());
        GroundNavigatorPlaybackDto restoredAfter = Playback(restoredEngine.CaptureSnapshot());

        // The live tick stays on the same segment and counts a turn, so a restore that lost the heading would differ.
        Assert.Equal(before.FromNodeId, liveAfter.FromNodeId);
        Assert.NotEqual(before.CumulativeTurnSinceAdvanceDeg, liveAfter.CumulativeTurnSinceAdvanceDeg);
        output.WriteLine(
            $"net turn after the next tick: live {liveAfter.CumulativeTurnSinceAdvanceDeg:R}, "
                + $"restored {restoredAfter.CumulativeTurnSinceAdvanceDeg:R}"
        );
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(liveAfter.CumulativeTurnSinceAdvanceDeg),
            BitConverter.DoubleToInt64Bits(restoredAfter.CumulativeTurnSinceAdvanceDeg)
        );
    }

    [Theory]
    [InlineData(TurnModel.Instant, "C560")]
    [InlineData(TurnModel.Ramp, "EC35")]
    public void TaxiWherePhysicsDoesNotSteer_SnapshotsCarryNoRememberedHeading(TurnModel turnModel, string aircraftType)
    {
        var groundData = new TestAirportGroundData();
        if (Taxi(groundData, turnModel, aircraftType) is not { } taxi)
        {
            return;
        }

        int playbacks = 0;
        for (int second = 1; second <= MidRunSeconds; second++)
        {
            taxi.Engine.TickOneSecond();
            foreach (JsonObject playback in PlaybackObjects(taxi.Engine.CaptureSnapshot()))
            {
                playbacks++;
                Assert.False(
                    playback.ContainsKey(nameof(GroundNavigatorPlaybackDto.HeadingAfterLastTickDeg)),
                    $"t={second}s: the playback remembers a heading under {turnModel} for a {aircraftType}"
                );
            }
        }

        output.WriteLine($"{turnModel} {aircraftType}: {playbacks} playbacks over {MidRunSeconds}s");
        Assert.True(playbacks > 0, $"no navigator playback in {MidRunSeconds}s of the {aircraftType}'s taxi");
    }

    private static GroundNavigatorPlaybackDto Playback(StateSnapshotDto snapshot) =>
        Assert.Single(
            snapshot
                .Aircraft.Single()
                .Phases!.Phases.OfType<TaxiingPhaseDto>()
                .Select(phase => phase.Navigator?.Playback)
                .OfType<GroundNavigatorPlaybackDto>()
        );

    private static IEnumerable<JsonObject> PlaybackObjects(StateSnapshotDto snapshot) =>
        GroundNavigatorArcRestoreTests.PlaybackObjects(JsonSerializer.SerializeToNode(snapshot, RecordingJsonOptions.Default));

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    /// <summary>
    /// Tick until the taxi is off an arc, where the navigator publishes its steer and physics turns the nose at a yaw rate
    /// of at least <see cref="MinPhysicsTurnRateDegPerSec"/>: there physics turns the nose between navigator ticks. On an
    /// arc the navigator writes the pose itself, so physics makes no turn to count.
    /// </summary>
    private int TickUntilPhysicsTurnsTheNose(SimulationEngine engine, AircraftState aircraft)
    {
        for (int second = 1; second <= TurnBudgetSeconds; second++)
        {
            engine.TickOneSecond();
            if (
                (aircraft.Phases?.CurrentPhase is TaxiingPhase)
                && (aircraft.Ground.LastNavDiag is { OnArc: false })
                && (Math.Abs(aircraft.AppliedTurnRateDegPerSec ?? 0.0) >= MinPhysicsTurnRateDegPerSec)
            )
            {
                output.WriteLine($"t={second}s: hdg={aircraft.TrueHeading.Degrees:R} rate={aircraft.AppliedTurnRateDegPerSec:R}");
                return second;
            }
        }

        return -1;
    }

    private sealed record TaxiRun(SimulationEngine Engine, AircraftState Aircraft);

    /// <summary>
    /// An engine under <paramref name="turnModel"/> whose <paramref name="aircraftType"/>, standing on KOAK taxiway B short
    /// of the 28R bar and square across the taxiway, has just been cleared <c>TAXI B W 30</c>; null when navdata or the KOAK
    /// layout is unavailable.
    /// </summary>
    private TaxiRun? Taxi(TestAirportGroundData groundData, TurnModel turnModel, string aircraftType)
    {
        if ((NewEngine(groundData, turnModel) is not { } engine) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return null;
        }

        List<GroundNode> bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        List<GroundNode> bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B");
        GroundNode nearBar28R = bar28R.OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position)).First();
        GroundNode start = FartherBNeighbour(FartherBNeighbour(nearBar28R, bar28L[0]), bar28L[0]);
        var startHeading = new TrueHeading(GeoMath.BearingTo(start.Position, nearBar28R.Position) + 90.0);

        AircraftState aircraft = Spawn(start, startHeading, layout, aircraftType);
        engine.World.AddAircraft(aircraft);
        CommandResult taxi = engine.SendCommand(Callsign, "TAXI B W 30");
        Assert.True(taxi.Success, taxi.Message);
        return new TaxiRun(engine, aircraft);
    }

    private SimulationEngine? NewEngine(TestAirportGroundData groundData, TurnModel turnModel)
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
                ScenarioId = "test-oak-navigator-heading-memory",
                ScenarioName = "OAK navigator heading memory restore",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
                TurnModel = turnModel,
            },
        };
    }

    /// <summary>The B neighbour of <paramref name="node"/> farthest from <paramref name="away"/>.</summary>
    private static GroundNode FartherBNeighbour(GroundNode node, GroundNode away) =>
        node
            .Edges.Where(edge => string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.OtherNode(node))
            .OrderByDescending(other => GeoMath.DistanceNm(other.Position, away.Position))
            .First();

    private static AircraftState Spawn(GroundNode node, TrueHeading heading, AirportGroundLayout layout, string aircraftType)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = aircraftType,
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
