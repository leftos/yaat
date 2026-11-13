using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// An arrival landing at an airport with no ground layout (Stockton, KSCK, which the test data has no map for, as an
/// airport first named mid-session has none) while the primary airport (OAK) has a taxi graph. It lands, rolls out, stops
/// and despawns as at an airport with no vNAS map, under the configured despawn mode. It carries no layout from its landing
/// clearance on, not even the OAK layout it was carrying airborne, and a snapshot restore keeps it without one. Its
/// <see cref="AircraftState.AirportId"/> becomes the field it touches down on, and a restore reads the layout an aircraft
/// on the ground or landing carries as a live tick does: its field's, or none.
/// </summary>
public class LayoutlessLandingFieldTests
{
    private const string Callsign = "AAL100";
    private const int WatchSeconds = 240;

    public LayoutlessLandingFieldTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("Parked", true)]
    [InlineData("Never", false)]
    public void Arrival_AtAnAirportWithNoLayout_RollsOutStops_AndDespawnsByTheConfiguredMode(string autoDeleteMode, bool despawns)
    {
        (SimulationEngine engine, AircraftState arrival) = SpawnOnFinal("SCK", "29R", autoDeleteMode);

        bool stopped = false;
        bool removed = false;
        for (int t = 0; (t < WatchSeconds) && !removed; t++)
        {
            engine.TickOneSecond();
            stopped |= (arrival.Phases?.CurrentPhase is HoldingAfterExitPhase) && (arrival.GroundSpeed < 1);
            _ = engine.TickAutoDelete();
            removed = engine.World.FindAircraft(Callsign) is null;
        }

        Assert.True(stopped, $"{Callsign} never stopped after its rollout: phase={arrival.Phases?.CurrentPhase?.Name ?? "none"}");
        Assert.Equal(despawns, removed);
    }

    [Fact]
    public void Arrival_AtAnAirportWithNoLayout_CarriesNoLayout_FromItsLandingClearance_ThroughARestore()
    {
        (SimulationEngine engine, AircraftState arrival) = SpawnOnFinal("SCK", "29R", "Never");
        Assert.Null(arrival.Ground.Layout);

        for (int t = 0; (t < WatchSeconds) && (arrival.Phases?.CurrentPhase is not HoldingAfterExitPhase); t++)
        {
            engine.TickOneSecond();
            Assert.Null(arrival.Ground.Layout);
        }

        Assert.True(arrival.IsOnGround);
        Assert.IsType<HoldingAfterExitPhase>(arrival.Phases!.CurrentPhase);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        engine.RestoreFromSnapshot(snapshot);

        AircraftState restored = Restored(engine, Callsign);
        Assert.Null(restored.Ground.Layout);
        engine.TickOneSecond();
        Assert.Null(restored.Ground.Layout);
    }

    [Fact]
    public void Touchdown_AtANonSpawnField_SetsAirportIdToThatField()
    {
        (SimulationEngine engine, AircraftState arrival) = SpawnOnFinal("SCK", "29R", "Never");

        TickUntil(engine, arrival, () => arrival.IsOnGround, "touched down");

        Assert.True(NavigationDatabase.AirportIdsMatch(arrival.AirportId, "SCK"), $"AirportId is {arrival.AirportId} after touchdown at SCK");
    }

    [Fact]
    public void Touchdown_AtTheSpawnField_LeavesAirportIdUnchanged()
    {
        (SimulationEngine engine, AircraftState arrival) = SpawnOnFinal("OAK", "28R", "Never");

        TickUntil(engine, arrival, () => arrival.IsOnGround, "touched down");

        Assert.Equal("OAK", arrival.AirportId);
    }

    [Fact]
    public void TouchAndGo_AtANonSpawnField_SetsAirportIdToThatField()
    {
        AircraftState aircraft = TouchDownAtStocktonWith(new TouchAndGoPhase());

        Assert.True(aircraft.IsOnGround);
        Assert.True(NavigationDatabase.AirportIdsMatch(aircraft.AirportId, "SCK"), $"AirportId is {aircraft.AirportId} after a touch-and-go at SCK");
    }

    [Fact]
    public void StopAndGo_AtANonSpawnField_SetsAirportIdToThatField()
    {
        AircraftState aircraft = TouchDownAtStocktonWith(new StopAndGoPhase());

        Assert.True(aircraft.IsOnGround);
        Assert.True(NavigationDatabase.AirportIdsMatch(aircraft.AirportId, "SCK"), $"AirportId is {aircraft.AirportId} after a stop-and-go at SCK");
    }

    /// <summary>
    /// Landed at SCK, with a phase list rebuilt with no runway (as a command that replaces it leaves it), the aircraft still
    /// runs on no layout: its field is the one it touched down on, never the OAK it spawned at.
    /// </summary>
    [Fact]
    public void LandedAtLayoutlessField_PhaseListRebuilt_GetsNoLayout()
    {
        (SimulationEngine engine, AircraftState arrival) = SpawnOnFinal("SCK", "29R", "Never");
        TickUntil(engine, arrival, () => arrival.Phases?.CurrentPhase is HoldingAfterExitPhase, "held after its exit");

        arrival.Phases = new PhaseList();
        arrival.Phases.Add(new HoldingAfterExitPhase());
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival, ActiveRunways.Empty));
        engine.TickOneSecond();

        Assert.Null(arrival.Ground.Layout);
        Assert.Null(engine.ResolveGroundLayout(arrival));
    }

    /// <summary>
    /// A snapshot from a build that let an aircraft on the ground at SCK keep OAK's layout restores it with none, and it
    /// despawns under the auto-delete mode as at an airport with no layout.
    /// </summary>
    [Fact]
    public void Restore_LegacyGroundAircraftCarryingAnotherFieldsLayout_DropsIt()
    {
        AirportGroundLayout oak = OakLayout();
        SimulationEngine engine = NewEngine("Parked", oak);
        RunwayInfo runway = Runway("SCK", "29R");
        LatLon onRunway = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, 0.5);
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = onRunway,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            AirportId = "OAK",
            FlightPlan = FlightPlan("OAK", "SCK"),
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty));
        aircraft.Ground.Layout = oak;
        engine.World.AddAircraft(aircraft);

        engine.RestoreFromSnapshot(engine.CaptureSnapshot());

        Assert.Null(Restored(engine, Callsign).Ground.Layout);
        _ = engine.TickAutoDelete();
        Assert.Null(engine.World.FindAircraft(Callsign));
    }

    /// <summary>
    /// An airborne aircraft in its landing phase at SCK with no landing clearance runs on no layout live, and a restore
    /// brings it back with none too.
    /// </summary>
    [Fact]
    public void Restore_LandingPhaseWithoutClearance_GetsTheSameLayoutAsLive()
    {
        (SimulationEngine engine, AircraftState arrival) = SpawnOnFinal("SCK", "29R", "Never");
        TickUntil(engine, arrival, () => arrival.Phases?.CurrentPhase is LandingPhase, "entered its landing phase");
        arrival.Phases!.LandingClearance = null;
        Assert.IsType<LandingPhase>(arrival.Phases.CurrentPhase);
        Assert.False(arrival.IsOnGround);
        Assert.Null(arrival.Phases.LandingClearance);
        Assert.Null(arrival.Pattern.PendingLandingClearance);
        AirportGroundLayout? live = arrival.Ground.Layout;
        Assert.Null(live);

        engine.RestoreFromSnapshot(engine.CaptureSnapshot());

        Assert.Same(live, Restored(engine, Callsign).Ground.Layout);
    }

    /// <summary>
    /// A phase-less aircraft on the ground at SFO, a field with a layout, carrying none (as a live-traffic shadow does), comes
    /// back from a restore carrying none, and from the first tick runs on SFO's layout, never the primary's.
    /// </summary>
    [Fact]
    public void Restore_GroundShadowWithoutLayout_AtAMappedField_GetsItsFieldsLayout()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout sfo = groundData.GetLayout("SFO") ?? throw new InvalidOperationException("the SFO test layout is missing");
        SimulationEngine engine = NewEngine("Never", OakLayout());
        RunwayInfo runway = Runway("SFO", "28L");
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            AirportId = "SFO",
            FlightPlan = FlightPlan("SFO", "OAK"),
        };
        engine.World.AddAircraft(aircraft);

        engine.RestoreFromSnapshot(engine.CaptureSnapshot());
        AircraftState restored = Restored(engine, Callsign);
        Assert.Null(restored.Ground.Layout);
        engine.TickOneSecond();

        Assert.Same(sfo, engine.ResolveGroundLayout(restored));
    }

    /// <summary>
    /// A helicopter spawned at OAK, carrying SFO's layout, lands on an SFO stand (no runway): it takes SFO, the field of
    /// the layout its spot is in, and keeps running on SFO's layout, never OAK's.
    /// </summary>
    [Fact]
    public void HeliLandingOnAnotherFieldsHelipad_TakesThatField()
    {
        AirportGroundLayout sfo =
            new TestAirportGroundData().GetLayout("SFO") ?? throw new InvalidOperationException("the SFO test layout is missing");
        GroundNode spot =
            sfo.Nodes.Values.FirstOrDefault(n => n.Type == GroundNodeType.Helipad) ?? sfo.Nodes.Values.First(n => n.Type == GroundNodeType.Parking);
        double fieldElevation = NavigationDatabase.Instance.GetAirportElevation("SFO") ?? 13;
        SimulationEngine engine = NewEngine("Never", OakLayout());
        var heli = new AircraftState
        {
            Callsign = "N76SK",
            AircraftType = "S76",
            Position = spot.Position,
            Altitude = fieldElevation + 30,
            IsOnGround = false,
            AirportId = "OAK",
            FlightPlan = FlightPlan("OAK", "SFO"),
            Phases = new PhaseList(),
        };
        heli.Phases.Add(new HelicopterLandingPhase());
        heli.Ground.Layout = sfo;
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, ActiveRunways.Empty, sfo));
        engine.World.AddAircraft(heli);

        TickUntil(engine, heli, () => heli.IsOnGround, "touched down");
        Assert.True(NavigationDatabase.AirportIdsMatch(heli.AirportId, "SFO"), $"AirportId is {heli.AirportId} after touchdown on an SFO stand");
        engine.TickOneSecond();

        Assert.Same(sfo, heli.Ground.Layout);
        Assert.Same(sfo, engine.ResolveGroundLayout(heli));
    }

    private static AircraftState Restored(SimulationEngine engine, string callsign) =>
        engine.World.FindAircraft(callsign) ?? throw new InvalidOperationException($"{callsign} is gone after the restore");

    private static void TickUntil(SimulationEngine engine, AircraftState aircraft, Func<bool> reached, string what)
    {
        for (int t = 0; (t < WatchSeconds) && !reached(); t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(reached(), $"{aircraft.Callsign} never {what}: phase={aircraft.Phases?.CurrentPhase?.Name ?? "none"}");
    }

    /// <summary>
    /// An OAK-spawned aircraft over SCK 29R's threshold at its elevation, whose phase list starts with
    /// <paramref name="touchdown"/>: starting it is the touchdown.
    /// </summary>
    private static AircraftState TouchDownAtStocktonWith(Phase touchdown)
    {
        RunwayInfo runway = Runway("SCK", "29R");
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 60,
            IsOnGround = false,
            AirportId = "OAK",
            FlightPlan = FlightPlan("OAK", "SCK"),
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(touchdown);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty));
        return aircraft;
    }

    /// <summary>
    /// An airliner 3 nm out on final to <paramref name="airport"/>'s <paramref name="runwayId"/> in an OAK scenario, filed
    /// from OAK and carrying OAK's layout as an airborne aircraft amended to another airport does, with the production
    /// landing chain, cleared to land.
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Arrival) SpawnOnFinal(string airport, string runwayId, string autoDeleteMode)
    {
        const double distNm = 3;
        Assert.Null(new TestAirportGroundData().GetLayout("SCK"));
        AirportGroundLayout oak = OakLayout();
        RunwayInfo runway = Runway(airport, runwayId);

        LatLon threshold = LandingThreshold.Resolve(runway, (AirportGroundLayout?)null);
        var arrival = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(distNm, runway.ElevationFt, AircraftCategorization.Categorize("B738")),
            IndicatedAirspeed = ShortFinalArrival.ApproachIas,
            IsOnGround = false,
            AirportId = "OAK",
            FlightPlan = FlightPlan("OAK", airport),
            Phases = new PhaseList { AssignedRunway = runway },
        };
        arrival.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        arrival.Phases.Add(new LandingPhase());
        arrival.Phases.Add(new RunwayExitPhase());
        arrival.Phases.Add(new HoldingAfterExitPhase());
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival, ActiveRunways.Empty));
        arrival.Ground.Layout = oak;

        SimulationEngine engine = NewEngine(autoDeleteMode, oak);
        engine.World.AddAircraft(arrival);

        CommandResult cland = engine.SendCommand(Callsign, "CLAND");
        Assert.True(cland.Success, $"CLAND failed: {cland.Message}");
        return (engine, arrival);
    }

    private static SimulationEngine NewEngine(string autoDeleteMode, AirportGroundLayout oak)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-layoutless-landing-field",
                ScenarioName = "Layoutless landing field",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                ScenarioAutoDeleteMode = autoDeleteMode,
            },
        };
        engine.World.GroundLayout = oak;
        return engine;
    }

    private static AirportGroundLayout OakLayout()
    {
        AirportGroundLayout oak =
            new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("the OAK test layout is missing");
        Assert.True(oak.HasTaxiGraph);
        return oak;
    }

    private static RunwayInfo Runway(string airport, string runwayId) =>
        NavigationDatabase.Instance.GetRunway(airport, runwayId) ?? throw new InvalidOperationException($"{airport} {runwayId} is not in navdata");

    private static AircraftFlightPlan FlightPlan(string departure, string destination) =>
        new()
        {
            Departure = departure,
            Destination = destination,
            FlightRules = "IFR",
            Altitude = PlannedAltitude.Ifr(3000),
        };
}
