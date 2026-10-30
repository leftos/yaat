using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-1522: the ground view's Taxi here sends the taxiways it found, then pins the clicked node (<c>#node</c>) after them,
/// and offers a hold short of the first runway the route crosses: <c>TAXI F C B #187 HS 15</c> from OAK's OLD1. The route was
/// cut at the far 15/33 bar of C's crossing, as if the hold short were where the clearance ends, so B was never reached and
/// the TAXI was refused ("Cannot taxi via B"); without B, <c>TAXI F C #pin HS 15</c> stopped at that bar short of its pin.
/// A trailing pin is where the clearance ends, and the hold short before it is en route.
/// </summary>
[Collection("Acceptance")]
public class Yaat1522TrailingNodePinHoldShortTests
{
    private const string Callsign = "N248ZV";
    private const string Stand = "OLD1";

    public Yaat1522TrailingNodePinHoldShortTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiFC_PinOnCPastThe15Crossing_HoldShort15_EndsAtThePin()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode bc = GroundNodeLookup.Junction(layout, "B", "C");
        GroundNode farBar = GroundNodeLookup.HoldShortBar(layout, "33", "C", bc);
        GroundNode pin = GroundNodeLookup.OnTaxiway(layout, "C", farBar, 500.0, bc);
        AircraftState aircraft = MakeParkedAtOld1(layout);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, ParseTaxi($"TAXI F C #{pin.Id} HS 15"), layout);

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.True(
            route.Segments[^1].ToNodeId == pin.Id,
            $"the route ends at #{route.Segments[^1].ToNodeId}, not the pin #{pin.Id}: {route.ToSummary()}"
        );
    }

    [Fact]
    public void TaxiFCB_PinOnB_HoldShort15_HoldsShortOn15AndRunsToThePin()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode pin = PinOnBAt28R(layout);
        AircraftState aircraft = MakeParkedAtOld1(layout);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, ParseTaxi($"TAXI F C B #{pin.Id} HS 15"), layout);

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.True(
            route.Segments[^1].ToNodeId == pin.Id,
            $"the route ends at #{route.Segments[^1].ToNodeId}, not the pin #{pin.Id}: {route.ToSummary()}"
        );
        HoldShortPoint hold33 = Assert.Single(route.HoldShortPoints, h => h.TargetName?.Contains("33") == true);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hold33.Reason);
        Assert.Contains(layout.Nodes[hold33.NodeId].Edges, e => e.MatchesTaxiway("C"));
        GroundNode bar28R = GroundNodeLookup.HoldShortBar(layout, "28R", "B", GroundNodeLookup.Parking(layout, Stand));
        Assert.True(
            route.HoldShortPoints.Any(h => h.NodeId == bar28R.Id),
            $"no hold short at B's 28R bar #{bar28R.Id} before the pin on the runway: {route.ToSummary()}"
        );
    }

    [Fact]
    public void FlyoutCommand_ThroughTheEngine_IsAccepted()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode pin = PinOnBAt28R(layout);
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped()) { Scenario = NewScenario() };
        engine.World.AddAircraft(MakeParkedAtOld1(layout));

        CommandResult result = engine.SendCommand(Callsign, $"TAXI F C B #{pin.Id} HS 15");

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Taxi via F C B HS 15/33 at C", result.Message);
    }

    /// <summary>Where B meets 28R/10L's centreline past B's 28R bar on OLD1's side: the node the ground view pins.</summary>
    private static GroundNode PinOnBAt28R(AirportGroundLayout layout) =>
        GroundNodeLookup.Junction(
            layout,
            "B",
            Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "28R"),
            GroundNodeLookup.HoldShortBar(layout, "28R", "B", GroundNodeLookup.Parking(layout, Stand))
        );

    private static TaxiCommand ParseTaxi(string text)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);
        return Assert.IsType<TaxiCommand>(parsed.Value);
    }

    private static AircraftState MakeParkedAtOld1(AirportGroundLayout layout)
    {
        GroundNode stand = GroundNodeLookup.Parking(layout, Stand);
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = stand.Position,
            TrueHeading = stand.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "RNO" },
            Phases = new PhaseList(),
        };
        aircraft.Ground.Layout = layout;
        aircraft.Ground.ParkingSpot = Stand;
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        return aircraft;
    }

    private static SimScenarioState NewScenario() =>
        new()
        {
            ScenarioId = "yaat-1522",
            ScenarioName = "YAAT-1522 trailing node pin with a hold short",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };
}
