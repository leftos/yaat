using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A command that needs a ground layout, given while the aircraft's airport is still loading, is refused "Layout loading,
/// try again" and goes through from the tick after the layout lands; an airport admitted with no map keeps the usual
/// "No airport ground layout available", and a command that needs no layout is not refused.
/// </summary>
public class LayoutLoadingRefusalTests
{
    private const string Parked = "TEST1";
    private const string Taxi = "TAXI D C B 28R";
    private const string Loading = "Layout loading, try again";

    private const string ScenarioJson = """
        {
          "id": "layout-loading-refusal",
          "name": "Layout loading refusal",
          "primaryAirportId": "SMF",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N1",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 38.80, "lon": -121.50 },
                "altitude": 3500,
                "heading": 90,
                "speed": 100
              },
              "flightplan": {
                "rules": "VFR",
                "departure": "KSMF",
                "destination": "KSMF",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              }
            }
          ]
        }
        """;

    public LayoutLoadingRefusalTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Taxi_AtALoadingAirport_IsRefused_AndGoesThroughTheTickAfterItLands()
    {
        var data = new PartlyLoadingGroundData("OAK");
        SimulationEngine engine = Load(data);
        AircraftState parked = AddParkedAtOak(engine, "KOAK");

        CommandResult refused = engine.SendCommand(Parked, Taxi);

        Assert.False(refused.Success);
        Assert.Equal(Loading, refused.Message);
        Assert.Null(parked.Ground.AssignedTaxiRoute);

        data.Release("OAK");
        engine.TickOneSecond();
        CommandResult accepted = engine.SendCommand(Parked, Taxi);

        Assert.True(accepted.Success, accepted.Message);
    }

    [Fact]
    public void WaitThenTaxi_AtALoadingAirport_IsDeferred_NotRefused()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData("OAK"));
        AircraftState parked = AddParkedAtOak(engine, "KOAK");

        CommandResult result = engine.SendCommand(Parked, $"WAIT 10 {Taxi}");

        Assert.True(result.Success, result.Message);
        Assert.Single(parked.DeferredDispatches);
    }

    [Fact]
    public void ACommandThatNeedsNoLayout_AtALoadingAirport_IsNotRefused()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData("OAK"));
        _ = AddParkedAtOak(engine, "KOAK");

        CommandResult result = engine.SendCommand(Parked, "SQ 1200");

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void WarpGround_AtALoadingAirport_IsRefused()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData("OAK"));
        AddParkedAtOak(engine, "KOAK");

        CommandResult result = engine.SendCommand(Parked, "WARPG #42");

        Assert.False(result.Success);
        Assert.Equal(Loading, result.Message);
    }

    [Fact]
    public void AChainHoldingATaxi_AtALoadingAirport_IsRefusedWhole()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData("OAK"));
        AircraftState parked = AddParkedAtOak(engine, "KOAK");
        uint assignedBefore = parked.Transponder.AssignedCode;
        uint codeBefore = parked.Transponder.Code;

        CommandResult result = engine.SendCommand(Parked, $"SQ 1234; {Taxi}");

        Assert.False(result.Success);
        Assert.Equal(Loading, result.Message);
        Assert.Equal(assignedBefore, parked.Transponder.AssignedCode);
        Assert.Equal(codeBefore, parked.Transponder.Code);
    }

    [Fact]
    public void Taxi_AtAnAirportLoadingThenAdmittedWithNoMap_IsRefusedLoadingThenNoLayout()
    {
        var data = new PartlyLoadingGroundData("LVK");
        SimulationEngine engine = Load(data);
        AircraftState parked = AddParkedAtOak(engine, "KLVK");
        parked.AirportId = "LVK";
        parked.FlightPlan.Destination = "KLVK";

        CommandResult loading = engine.SendCommand(Parked, Taxi);
        Assert.False(loading.Success);
        Assert.Equal(Loading, loading.Message);

        data.Release("LVK");
        engine.TickOneSecond();
        CommandResult noMap = engine.SendCommand(Parked, Taxi);

        Assert.False(noMap.Success);
        Assert.Equal("No airport ground layout available", noMap.Message);
    }

    [Fact]
    public void TypedAssume_OfAGroundShadowAtALoadingAirport_ThroughTheEngine_IsRefused()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData("OAK"));
        AircraftState shadow = GroundShadowAtOak();
        engine.World.AddAircraft(shadow);

        CommandResult result = engine.SendCommand(shadow.Callsign, "ASSUME");

        Assert.False(result.Success);
        Assert.Equal(Loading, result.Message);
        Assert.True(shadow.IsShadow);
        Assert.False(shadow.AssumedFromLiveTraffic);
        Assert.Null(shadow.Phases);
    }

    [Fact]
    public void Taxi_AtAnAirportAdmittedWithNoMap_KeepsTheNoLayoutRefusal()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData());
        AircraftState parked = AddParkedAtOak(engine, "KLVK");
        parked.AirportId = "LVK";
        parked.FlightPlan.Destination = "KLVK";

        CommandResult result = engine.SendCommand(Parked, Taxi);

        Assert.False(result.Success);
        Assert.Equal("No airport ground layout available", result.Message);
    }

    [Fact]
    public void PickGroundLayout_TellsLoadingFromAdmittedAndFromNoMap()
    {
        var data = new PartlyLoadingGroundData("OAK");
        SimulationEngine engine = Load(data);
        AircraftState parked = AddParkedAtOak(engine, "KOAK");

        Assert.Equal(NoLayout(loading: true), engine.PickGroundLayout(parked));
        int reads = data.ReadCount("OAK");
        Assert.Equal(NoLayout(loading: true), engine.PickGroundLayout(parked));
        Assert.Equal(reads, data.ReadCount("OAK"));

        data.Release("OAK");
        engine.TickOneSecond();
        (AirportGroundLayout? layout, bool loading) = engine.PickGroundLayout(parked);
        Assert.NotNull(layout);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(layout.AirportId));
        Assert.False(loading);

        parked.AirportId = "LVK";
        parked.FlightPlan.Departure = "KLVK";
        parked.FlightPlan.Destination = "KLVK";
        Assert.Equal(NoLayout(loading: false), engine.PickGroundLayout(parked));
    }

    [Theory]
    [InlineData(Taxi, true)]
    [InlineData("RTAXI D C B 28R", true)]
    [InlineData("PUSH", true)]
    [InlineData("CROSS 28R", true)]
    [InlineData("HS 28R", true)]
    [InlineData("RES", true)]
    [InlineData("LAHSO 33", true)]
    [InlineData("CTO", true)]
    [InlineData("LUAW", true)]
    [InlineData("WARPG #42", true)]
    [InlineData("HOLD", false)]
    [InlineData("FOLLOWG N2", false)]
    [InlineData("CLRWY", false)]
    [InlineData("TAXIALL 30", false)]
    [InlineData("CTOPP", false)]
    [InlineData("GIVEWAY N2", false)]
    [InlineData("BREAK", false)]
    [InlineData("CM 050", false)]
    [InlineData("SQ 1200", false)]
    public void NeedsGroundLayout_IsTheSetWhoseHandlersRefuseWithNoLayout(string input, bool needs)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        ParsedCommand command = Assert.Single(Assert.Single(parsed.Value!.Blocks).Commands);

        Assert.Equal(needs, CommandDescriber.NeedsGroundLayout(command));
    }

    [Fact]
    public void NeedsGroundLayout_HoldsTheCommandsBuiltWithoutText()
    {
        Assert.True(CommandDescriber.NeedsGroundLayout(new TaxiAutoCommand("28R", null, null, false)));
        Assert.True(CommandDescriber.NeedsGroundLayout(new AirTaxiCommand("28R")));
        Assert.True(CommandDescriber.NeedsGroundLayout(new LandCommand("H1")));
        Assert.True(CommandDescriber.NeedsGroundLayout(new PushbackMultiCommand([], null, false)));
    }

    [Fact]
    public void GroundShadow_AtALoadingAirport_IsNotAssumed()
    {
        AircraftState shadow = GroundShadowAtOak();

        CommandResult result = LiveTrafficAssumer.Assume(shadow, LoadingContext(shadow), out AssumeUndo? undo);

        Assert.False(result.Success);
        Assert.Equal(Loading, result.Message);
        Assert.Null(undo);
        Assert.True(shadow.IsShadow);
        Assert.False(shadow.AssumedFromLiveTraffic);
    }

    [Fact]
    public void AirborneShadow_InboundToALoadingAirport_IsAssumed()
    {
        AircraftState shadow = Shadow(new LiveTrafficSample(0, 37.9, -121.3, 11_000, 300, 270, 0, LiveTrafficSource.Stars, 4521));

        CommandResult result = LiveTrafficAssumer.Assume(shadow, LoadingContext(shadow), out _);

        Assert.True(result.Success, result.Message);
        Assert.False(shadow.IsShadow);
    }

    private static (AirportGroundLayout? Layout, bool Loading) NoLayout(bool loading) => (null, loading);

    // A shadow rolling slowly on OAK 28R, 0.7 nm past the threshold, inbound from KLAX.
    private static AircraftState GroundShadowAtOak()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.7);
        AircraftState shadow = Shadow(
            new LiveTrafficSample(0, pos.Lat, pos.Lon, runway.ElevationFt, 12, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex, 4521)
        );
        Assert.True(shadow.IsOnGround);
        return shadow;
    }

    private static AircraftState Shadow(LiveTrafficSample sample) =>
        LiveTrafficKinematics.CreateShadow(
            "UAL123",
            "B738",
            sample,
            new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KLAX",
                Destination = "KOAK",
            }
        );

    private static DispatchContext LoadingContext(AircraftState shadow) =>
        TestDispatch.Context(new Random(1), findAircraft: cs => cs == shadow.Callsign ? shadow : null, listAircraft: () => [shadow]) with
        {
            GroundLayoutLoading = true,
        };

    private static SimulationEngine Load(PartlyLoadingGroundData data)
    {
        var engine = new SimulationEngine(data, PushPlanStore.CreateShipped());
        engine.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.NotNull(engine.World.FindAircraft("N1"));
        return engine;
    }

    // A jet parked at KOAK's NEW7 with no layout attached, as one is while its airport's layout is still loading.
    private static AircraftState AddParkedAtOak(SimulationEngine engine, string departure)
    {
        AirportGroundLayout oak = new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("no OAK test layout");
        GroundNode parking = oak.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.Parking) && string.Equals(n.Name, "NEW7", StringComparison.OrdinalIgnoreCase)
        );
        var aircraft = new AircraftState
        {
            Callsign = Parked,
            AircraftType = "B738",
            Position = parking.Position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            AirportId = "OAK",
            FlightPlan = new AircraftFlightPlan
            {
                Departure = departure,
                Destination = "KSMF",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(23000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, null));
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
