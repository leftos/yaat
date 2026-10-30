using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A clear-runway pull-forward (<see cref="ClearRunwayPhase"/>, the <c>CLRWY</c> command) restored from a snapshot taken
/// part-way round its turn onto the taxiway resumes the navigator's playback where it stood, and so goes on exactly as the
/// run that was never interrupted. Every snapshot goes through the recording JSON before it is restored.
///
/// <para>The geometry is KOAK taxiway B at 28R: a C560 stands on the B holding position south of 28R, its tail over the
/// runway, with its nose swung off the taxiway, and pulls forward along B to ½ its length past the bars. The recorded
/// CLRWY of <see cref="Issue172Jbu577ClearRunwayTests"/> (JBU577 at KSFO) never shows the navigator part-way round a
/// curve.</para>
/// </summary>
public class ClearRunwayRestoreTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AircraftType = "C560";
    private const string AirportId = "OAK";

    /// <summary>How far the nose stands off B: far enough that the navigator opens with an entry-alignment slow turn.</summary>
    private const double NoseOffTaxiwayDeg = 80.0;

    /// <summary>How far the nose must already have swung for the snapshot to be part-way round the turn.</summary>
    private const double MinSwungDeg = 10.0;

    private const int TurnBudgetSeconds = 30;
    private const int CompareSeconds = 30;

    /// <summary>
    /// A snapshot taken part-way round the navigator's entry-alignment slow turn onto B, restored into a second engine,
    /// keeps the restored aircraft on the original's phase, position, heading and speed every second. The turn is solved from the pose the
    /// aircraft had when the segment was set up; a restore that set the segment up again from the part-turned pose would
    /// fly a different curve.
    /// </summary>
    [Fact]
    public void ClearRunway_SurvivesSnapshotRoundTripMidTurn()
    {
        var groundData = new TestAirportGroundData();
        if ((NewEngine(groundData) is not { } engine) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return;
        }

        AircraftState aircraft = SpawnClearingRunway(engine, layout);
        TrueHeading startHeading = aircraft.TrueHeading;
        bool midTurn = false;
        for (int second = 1; (second <= TurnBudgetSeconds) && !midTurn; second++)
        {
            engine.TickOneSecond();
            midTurn =
                (aircraft.Phases?.CurrentPhase is ClearRunwayPhase phase)
                && PlaysASlowTurn(phase)
                && (Math.Abs(startHeading.SignedAngleTo(aircraft.TrueHeading)) >= MinSwungDeg);
        }

        Assert.True(midTurn, $"the clear-runway navigator was never caught part-way round a slow turn within {TurnBudgetSeconds}s");
        output.WriteLine($"mid-turn: hdg={aircraft.TrueHeading.Degrees:F1} gs={aircraft.GroundSpeed:F1} at {aircraft.Position}");

        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        restoredEngine.RestoreFromSnapshot(RoundTrip(engine.CaptureSnapshot()));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));

        for (int second = 1; second <= CompareSeconds; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(aircraft.Position, restored.Position);
            Assert.Equal(aircraft.TrueHeading, restored.TrueHeading);
            Assert.Equal(aircraft.IndicatedAirspeed, restored.IndicatedAirspeed);
        }

        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"after {CompareSeconds}s: {aircraft.Phases?.CurrentPhase?.Name} at {aircraft.Position}");
    }

    /// <summary>Whether the phase's snapshot carries a navigator playing a slow turn.</summary>
    private static bool PlaysASlowTurn(ClearRunwayPhase phase) =>
        GroundNavigatorArcRestoreTests
            .PlaybackObjects(JsonSerializer.SerializeToNode<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default))
            .Any(playback => (string?)playback["Primitive"]?["$type"] == "SlowTurn");

    /// <summary>
    /// Stand the aircraft on the B holding position south of 28R (the bar nearer 28L), its nose swung
    /// <see cref="NoseOffTaxiwayDeg"/> off B, and start it pulling clear of 28R along B.
    /// </summary>
    private static AircraftState SpawnClearingRunway(SimulationEngine engine, AirportGroundLayout layout)
    {
        GroundNode bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B")[0];
        GroundNode farBar28R = TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(layout, "28R", "B")
            .OrderBy(node => GeoMath.DistanceNm(node.Position, bar28L.Position))
            .First();
        GroundNode runwaySide = farBar28R
            .Edges.Where(edge => string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.OtherNode(farBar28R))
            .OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L.Position))
            .First();

        double alongB = GeoMath.BearingTo(runwaySide.Position, farBar28R.Position);
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = farBar28R.Position,
            TrueHeading = new TrueHeading(alongB + NoseOffTaxiwayDeg),
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
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "B";
        aircraft.Phases.Add(new ClearRunwayPhase(farBar28R.Id, runwaySide.Id));
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        engine.World.AddAircraft(aircraft);
        return aircraft;
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
                ScenarioId = "test-oak-clear-runway-restore",
                ScenarioName = "OAK clear-runway restore",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
            },
        };
    }
}
