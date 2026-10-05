using System.Globalization;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
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
        double? denElevation = NavigationDatabase.Instance.GetAirportElevation("KDEN");
        Assert.True(denElevation is > 5000, $"KDEN elevation must resolve in test navdata (got {denElevation})");

        ScenarioLoadResult result = ScenarioLoader.Load(
            ColdCallCoordinatesAtHighFieldElevation,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
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
        double? denElevation = NavigationDatabase.Instance.GetAirportElevation("KDEN");
        Assert.True(denElevation is > 5000, $"KDEN elevation must resolve in test navdata (got {denElevation})");

        ScenarioLoadResult result = ScenarioLoader.Load(
            ColdCallCoordinatesAtHighFieldElevationNoAirportId,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
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

        ScenarioLoadResult result = ScenarioLoader.Load(
            CoordinatesAtFieldElevation,
            groundData,
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
        Assert.True(state.IsOnGround, "Coordinates spawn at field elevation must be on the ground");
        Assert.Equal(0, state.IndicatedAirspeed);
        Assert.IsType<AtParkingPhase>(state.Phases?.CurrentPhase);
        Assert.NotNull(state.Ground.Layout);
        Assert.True(state.Ground.AutoDeleteExempt, "Ground departures must be exempt from Parked auto-delete");
        // The point sits within the lane width of taxiway C, so its TAXI to the runway is scripted and never calls.
        Assert.Equal("C", state.Ground.SpawnTaxiway);
        Assert.Equal(InitialCallupPlan.None, state.Ground.InitialCallup);
    }

    // Taxiway K at OAK, the H2 case's ground spawn (YAAT-308): a coordinate spawn on a movement-area taxiway.
    private const string CoordinatesOnTaxiwayK = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.73212069, "lon": -122.22503752 } },
              "flightplan": { "rules": "VFR", "departure": "KLVK", "destination": "KOAK" },
              "presetCommands": __PRESETS__,
              "airportId": "OAK"
            }
          ]
        }
        """;

    // The south GA ramp at OAK between GA7 and GA8: no movement-area taxiway within the lane width.
    private const string CoordinatesOnTheGaRamp = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "N123SP",
              "aircraftType": "C172",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.732479, "lon": -122.215235 } },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KSFO" },
              "presetCommands": [ { "id": "p1", "command": "TAXI W 28R", "timeOffset": 0 } ],
              "airportId": "OAK"
            }
          ]
        }
        """;

    private static AircraftState LoadSingle(string json) =>
        Assert
            .Single(ScenarioLoader.Load(json, new TestAirportGroundData(), new Random(0), MagneticDeclination.EvaluationDateUtc).ImmediateAircraft)
            .State;

    [Theory]
    [InlineData("[]", InitialCallupPlan.StandCall)]
    [InlineData("""[ { "id": "p1", "command": "TAXI K W 28R", "timeOffset": 0 } ]""", InitialCallupPlan.None)]
    [InlineData("""[ { "id": "p1", "command": "PUSH K", "timeOffset": 0 } ]""", InitialCallupPlan.AfterPush)]
    public void CoordinateSpawnOnTaxiwayK_RecordsTheTaxiwayAndPlansFromItsPresets(string presets, InitialCallupPlan expected)
    {
        AircraftState state = LoadSingle(CoordinatesOnTaxiwayK.Replace("__PRESETS__", presets, StringComparison.Ordinal));

        Assert.Equal("K", state.Ground.SpawnTaxiway);
        Assert.Equal(expected, state.Ground.InitialCallup);
    }

    [Fact]
    public void CoordinateSpawnOnTheRamp_HasNoSpawnTaxiwayAndItsScriptedTaxiToTheRunwayMakesNoCall()
    {
        AircraftState state = LoadSingle(CoordinatesOnTheGaRamp);

        Assert.True(state.IsOnGround);
        Assert.Null(state.Ground.SpawnTaxiway);
        Assert.Equal(InitialCallupPlan.None, state.Ground.InitialCallup);
    }

    // Y-PY SMFN TWR's coordinate spawns parked at SMF stands (the YAAT-317 inventory): each sits a few feet from its
    // stand, so it is at the stand, not on a taxiway, however near a taxiway runs.
    private const string CoordinatesAtAnSmfStand = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "SMF",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "SWA2756",
              "aircraftType": "B737",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": __LAT__, "lon": __LON__ } },
              "flightplan": { "rules": "IFR", "departure": "KSMF", "destination": "KBUR" },
              "presetCommands": [],
              "airportId": "SMF"
            }
          ]
        }
        """;

    // A coordinate spawn on a stand that lies within the lane width of a movement-area taxiway: the nearest taxi edge
    // alone would call it on that taxiway, but the stand is nearer, so it is at the stand.
    [Theory]
    [InlineData("OAK")]
    [InlineData("SMF")]
    public void CoordinateSpawnOnAStandBesideATaxiway_IsNotOnTheTaxiway(string airport)
    {
        AirportGroundLayout layout = new TestAirportGroundData().GetLayout(airport)!;
        var movementArea = MovementAreaClassification.For(layout);
        GroundNode stand = layout.Nodes.Values.First(node =>
            (node.Type == GroundNodeType.Parking)
            && layout.FindNearestTaxiEdge(node.Position) is { } edge
            && (edge.DistNm * GeoMath.FeetPerNm is > 1.0 and <= RampLaneReposition.CurrentLaneMaxFt)
            && movementArea.IsMovementArea(edge.Edge.TaxiwayName)
        );
        string json = CoordinatesAtAnSmfStand
            .Replace("\"SMF\"", $"\"{airport}\"", StringComparison.Ordinal)
            .Replace("__LAT__", stand.Position.Lat.ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__LON__", stand.Position.Lon.ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("KSMF", "K" + airport, StringComparison.Ordinal);

        AircraftState state = LoadSingle(json);

        Assert.True(state.IsOnGround);
        Assert.Null(state.Ground.SpawnTaxiway);
        Assert.Equal(InitialCallupPlan.StandCall, state.Ground.InitialCallup);
    }

    [Theory]
    [InlineData("38.695229", "-121.593859")] // SWA2756, 24 ft from stand B17
    [InlineData("38.69478", "-121.58803")] // SKW5513, 19 ft from stand A16
    [InlineData("38.691187", "-121.597433")] // FDX3670, 19 ft from stand F2
    [InlineData("38.695234", "-121.591971")] // NKS1819, 25 ft from stand B11
    public void CoordinateSpawnAtAnSmfStand_IsNotOnATaxiway(string lat, string lon)
    {
        AircraftState state = LoadSingle(
            CoordinatesAtAnSmfStand.Replace("__LAT__", lat, StringComparison.Ordinal).Replace("__LON__", lon, StringComparison.Ordinal)
        );

        Assert.True(state.IsOnGround);
        Assert.Null(state.Ground.SpawnTaxiway);
        Assert.Equal(InitialCallupPlan.StandCall, state.Ground.InitialCallup);
    }

    [Fact]
    public void CoordinatesAtCruiseAltitude_OmittedSpeed_StaysAirborne()
    {
        ScenarioLoadResult result = ScenarioLoader.Load(
            CoordinatesAtCruise,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
        Assert.False(state.IsOnGround);
        Assert.True(state.IndicatedAirspeed > 0, "Airborne spawn resolves to a cruise speed");
    }

    // An arrival authored at a point in the air, as S1-SFO-2's arrivals are (issue #448).
    private const string AirborneCoordinatesArrival = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "ac1",
              "aircraftId": "SKW3398",
              "aircraftType": "E75L",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.40, "lon": -122.05 }, "altitude": 6000, "heading": 300 },
              "flightplan": { "rules": "IFR", "departure": "KLAX", "destination": "KSFO" },
              "airportId": "SFO"
            }
          ]
        }
        """;

    /// <summary>
    /// Issue #448: an airborne Coordinates arrival carries its destination's layout from spawn, as an OnFinal spawn
    /// does, so once it has landed every ground verb and the cached-layout readers see the airport it is on.
    /// </summary>
    [Fact]
    public void CoordinatesAirborneArrival_CarriesDestinationLayout()
    {
        ScenarioLoadResult result = ScenarioLoader.Load(
            AirborneCoordinatesArrival,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
        Assert.False(state.IsOnGround);
        Assert.NotNull(state.Ground.Layout);
        Assert.Equal("SFO", state.Ground.LayoutAirportId, ignoreCase: true);
    }

    [Fact]
    public void CoordinatesAtFieldElevation_ExplicitSpeed_StaysAirborne()
    {
        ScenarioLoadResult result = ScenarioLoader.Load(
            CoordinatesAtFieldElevationExplicitSpeed,
            new TestAirportGroundData(),
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
        Assert.False(state.IsOnGround);
        Assert.Equal(250, state.IndicatedAirspeed);
    }
}
