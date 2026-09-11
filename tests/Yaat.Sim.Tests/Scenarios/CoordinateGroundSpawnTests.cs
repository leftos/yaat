using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// A scenario aircraft authored at "Coordinates" sitting at (or near) field elevation with
/// no explicit speed is a ground departure — scenario authors express "ready to taxi from
/// this point" this way, the same intent as a "Parking" spawn but at an arbitrary surface
/// point. It must spawn on the ground (zero speed, ground layout loaded, snapped to the
/// taxi graph) so its TAXI preset fires, not airborne flying off in the authored heading.
/// </summary>
[Collection("NavDbMutator")]
public class CoordinateGroundSpawnTests
{
    public CoordinateGroundSpawnTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // OAK field elevation is 9 ft. Coordinates near taxiway B, mirroring the S2-OAK-2 bug bundle.
    private const string CoordinatesAtFieldElevation = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "TWY85",
              "aircraftType": "CL60",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.726, "lon": -122.205333 }, "altitude": 9, "heading": 120 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KBJC" },
              "presetCommands": [ { "id": "p1", "command": "TAXI B 28R", "timeOffset": 0 } ],
              "airportId": "OAK"
            }
          ]
        }
        """;

    private const string CoordinatesAtCruise = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "TWY85",
              "aircraftType": "CL60",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.726, "lon": -122.205333 }, "altitude": 35000, "heading": 120 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KBJC" },
              "airportId": "OAK"
            }
          ]
        }
        """;

    private const string CoordinatesAtFieldElevationExplicitSpeed = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "TWY85",
              "aircraftType": "CL60",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.726, "lon": -122.205333 }, "altitude": 9, "heading": 120, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KBJC" },
              "airportId": "OAK"
            }
          ]
        }
        """;

    // Real scenarios author a Coordinates ground spawn by setting the aircraft's `airportId` and
    // (usually) omitting the flight plan entirely — every shipped example in
    // docs/atctrainer-scenario-examples/ does this. The airport under the aircraft is therefore
    // identified by `airportId`, NOT by FlightPlan.Departure. KDEN field elevation is 5434 ft, so a
    // ground departure is authored at ~5434 ft with no positive speed (the documented -1 sentinel).
    private const string ColdCallCoordinatesAtHighFieldElevation = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "DEN",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N456HE",
              "aircraftType": "C172",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 39.8617, "lon": -104.6731 }, "altitude": 5434, "heading": 170 },
              "airportId": "DEN"
            }
          ]
        }
        """;

    [Fact]
    public void ColdCallCoordinatesAtHighFieldElevation_IdentifiedByAirportId_SpawnsOnGround()
    {
        // Precondition: the high-elevation field resolves in the test navdata.
        var denElevation = NavigationDatabase.Instance.GetAirportElevation("KDEN");
        Assert.True(denElevation is > 5000, $"KDEN elevation must resolve in test navdata (got {denElevation})");

        var result = ScenarioLoader.Load(
            ColdCallCoordinatesAtHighFieldElevation,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        var state = Assert.Single(result.ImmediateAircraft).State;
        // Field elevation must be resolved from `airportId` (like LoadOnRunway/LoadOnFinal and
        // FieldElevationResolver), not solely from the absent FlightPlan.Departure. Otherwise
        // fieldElevation falls back to 0, agl = 5434 fails the <200 ft ground gate, and the intended
        // ground departure spawns airborne — either frozen at 0 kt or flying off at a cruise default.
        Assert.True(state.IsOnGround, "Coordinates ground spawn at a high-elevation field (via airportId) must be on the ground");
        Assert.Equal(0, state.IndicatedAirspeed);
        Assert.IsType<AtParkingPhase>(state.Phases?.CurrentPhase);
    }

    // A cold call may omit `airportId` too: CreateBaseState then defaults the aircraft's airport to
    // the scenario's primaryAirportId, so the field under it must resolve the same way.
    private const string ColdCallCoordinatesAtHighFieldElevationNoAirportId = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "DEN",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N789HE",
              "aircraftType": "C172",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 39.8617, "lon": -104.6731 }, "altitude": 5434, "heading": 170 }
            }
          ]
        }
        """;

    [Fact]
    public void ColdCallCoordinatesAtHighFieldElevation_NoAirportIdNoFlightPlan_FallsBackToPrimaryAirport()
    {
        var denElevation = NavigationDatabase.Instance.GetAirportElevation("KDEN");
        Assert.True(denElevation is > 5000, $"KDEN elevation must resolve in test navdata (got {denElevation})");

        var result = ScenarioLoader.Load(
            ColdCallCoordinatesAtHighFieldElevationNoAirportId,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        var state = Assert.Single(result.ImmediateAircraft).State;
        Assert.True(
            state.IsOnGround,
            "Coordinates ground spawn at a high-elevation primary airport (no airportId, no flight plan) must be on the ground"
        );
        Assert.Equal(0, state.IndicatedAirspeed);
        Assert.IsType<AtParkingPhase>(state.Phases?.CurrentPhase);
    }

    [Fact]
    public void CoordinatesAtFieldElevation_OmittedSpeed_SpawnsOnGround()
    {
        var groundData = new TestAirportGroundData();

        var result = ScenarioLoader.Load(CoordinatesAtFieldElevation, groundData, new Random(0), MagneticDeclination.EvaluationDateUtc);

        var state = Assert.Single(result.ImmediateAircraft).State;
        Assert.True(state.IsOnGround, "Coordinates spawn at field elevation must be on the ground");
        Assert.Equal(0, state.IndicatedAirspeed);
        Assert.IsType<AtParkingPhase>(state.Phases?.CurrentPhase);
        Assert.NotNull(state.Ground.Layout);
        Assert.True(state.Ground.AutoDeleteExempt, "Ground departures must be exempt from Parked auto-delete");
        Assert.True(state.Ground.IsScriptedDeparture, "A TAXI preset marks this a scripted departure");
    }

    [Fact]
    public void CoordinatesAtCruiseAltitude_OmittedSpeed_StaysAirborne()
    {
        var result = ScenarioLoader.Load(CoordinatesAtCruise, new TestAirportGroundData(), new Random(0), MagneticDeclination.EvaluationDateUtc);

        var state = Assert.Single(result.ImmediateAircraft).State;
        Assert.False(state.IsOnGround);
        Assert.True(state.IndicatedAirspeed > 0, "Airborne spawn resolves to a cruise speed");
    }

    [Fact]
    public void CoordinatesAtFieldElevation_ExplicitSpeed_StaysAirborne()
    {
        var result = ScenarioLoader.Load(
            CoordinatesAtFieldElevationExplicitSpeed,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        var state = Assert.Single(result.ImmediateAircraft).State;
        Assert.False(state.IsOnGround);
        Assert.Equal(250, state.IndicatedAirspeed);
    }
}
