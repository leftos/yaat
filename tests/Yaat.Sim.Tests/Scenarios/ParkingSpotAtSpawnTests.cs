using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// <c>Ground.ParkingSpot</c> is the datum the Aircraft List Info column renders as
/// "at parking &lt;spot&gt;". A scenario aircraft authored at a stand must carry it from
/// the moment it loads — the stand is known, so the column must name it — and a TAXI
/// clearance off that stand must drop it again, because the aircraft is no longer there.
/// </summary>
[Collection("NavDbMutator")]
public class ParkingSpotAtSpawnTests
{
    private const string Stand = "A4";

    private static readonly TestAirportGroundData GroundData = new();

    private const string ParkedAtSfo = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "UAL123",
              "aircraftType": "B738",
              "startingConditions": { "type": "Parking", "parking": "A4" },
              "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "KLAX" },
              "airportId": "SFO"
            }
          ]
        }
        """;

    public ParkingSpotAtSpawnTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ScenarioLoader_ParkingSpawn_SetsGroundParkingSpot()
    {
        var layout = GroundData.GetLayout("SFO");
        Assert.NotNull(layout);
        var node = layout.FindParkingByName(Stand) ?? layout.FindSpotByName(Stand);
        Assert.NotNull(node);

        var result = ScenarioLoader.Load(ParkedAtSfo, GroundData, new Random(0), MagneticDeclination.EvaluationDateUtc);

        var state = Assert.Single(result.ImmediateAircraft).State;
        Assert.IsType<AtParkingPhase>(state.Phases?.CurrentPhase);
        Assert.Equal(node.Name, state.Ground.ParkingSpot);

        // The Info column is a pure projection of that datum: naming the stand there is the point of setting it.
        var status = AircraftStatusDescriber.Describe(state, AircraftStatusContext.None).Text;
        Assert.Contains($"at parking {node.Name}", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Taxi_FromStand_ClearsGroundParkingSpot()
    {
        var layout = GroundData.GetLayout("SFO");
        Assert.NotNull(layout);
        var stand = layout.FindParkingByName(Stand) ?? layout.FindSpotByName(Stand);
        Assert.NotNull(stand);

        var ac = AircraftAtStand(stand);
        Assert.Equal(Stand, ac.Ground.ParkingSpot);

        // Auto-routed so the test asserts the clearance's effect on ParkingSpot, not a hand-written
        // SFO ramp path; TAXIAUTO runs the same TryTaxiCore rebuild as a typed TAXI <route> <rwy>.
        var result = GroundCommandHandler.TryTaxiAuto(
            ac,
            new TaxiAutoCommand(DestinationRunway: "28L", DestinationParking: null, DestinationSpot: null),
            layout
        );

        Assert.True(result.Success, $"TAXIAUTO 28L from {Stand} should succeed: {result.Message}");
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);
        Assert.Null(ac.Ground.ParkingSpot);
    }

    private static AircraftState AircraftAtStand(GroundNode stand)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = stand.Position,
            TrueHeading = new TrueHeading(stand.TrueHeading?.Degrees ?? 0),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO" },
        };
        ac.Ground.ParkingSpot = stand.Name;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }
}
