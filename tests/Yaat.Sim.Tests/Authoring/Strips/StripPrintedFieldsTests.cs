using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Authoring.Strips;

/// <summary>
/// The time-taking field builders print, at the engine's sim time, exactly the array the bare engine prints for the
/// same aircraft; with no time the proposed departure time and the ETA come back empty.
/// </summary>
public class StripPrintedFieldsTests
{
    private const string Callsign = "SWA1234";
    private const string ArrivalCallsign = "ASA451";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public StripPrintedFieldsTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static string Departure(string airport, double lat, double lon, string? flightPlanJson, string type = "B738") =>
        $$"""
            {
              "id": "printed-fields",
              "name": "Printed fields",
              "artccId": "ZOA",
              "primaryAirportId": "{{airport}}",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{Callsign}}",
                  "aircraftType": "{{type}}",
                  "transponderMode": "C",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": { "lat": {{lat}}, "lon": {{lon}} },
                    "altitude": 9,
                    "heading": 290,
                    "speed": 0
                  },
                  {{(flightPlanJson is null ? "" : $"\"flightplan\": {flightPlanJson},")}}
                  "presetCommands": [],
                  "spawnDelay": 0,
                  "airportId": "{{airport}}",
                  "difficulty": "Easy"
                }
              ]
            }
            """;

    private static string OakDeparture(string? flightPlanJson, string type = "B738") => Departure("OAK", 37.7213, -122.2208, flightPlanJson, type);

    private static string Plan(string rules, int cruise, string route, string remarks, string type = "B738/L") =>
        $$"""
            { "rules": "{{rules}}", "departure": "KOAK", "destination": "KSFO", "cruiseAltitude": {{cruise}}, "cruiseSpeed": 250,
              "route": "{{route}}", "remarks": "{{remarks}}", "aircraftType": "{{type}}" }
            """;

    private const string ArrivalToOak = """
        {
          "id": "strip-arrival",
          "name": "Strip arrival at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "ASA451",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -121.37 },
                "altitude": 8000,
                "heading": 270,
                "speed": 250
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KSEA",
                "destination": "KOAK",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "/t/ NEW PILOT",
                "aircraftType": "B738/L"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    private SimulationEngine? Engine(string scenarioJson, string positionCallsign, string positionType)
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(scenarioJson, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars(positionCallsign, positionCallsign[..3], 3, "O");
        scenario.StudentPositionType = positionType;
        engine.InitializeFromArtcc();
        return engine;
    }

    private bool DisplaysDestination(string positionCallsign) =>
        _zoa!.GetAllAccessibleStripBays(positionCallsign).First(b => !b.IsExternal).Owner.FlightStripsConfiguration!.DisplayDestinationAirportIds;

    /// <summary>Spawns the departure, lets the engine print it, and checks the time-taking builder against it.</summary>
    private void AssertDepartureMatchesEngine(string scenarioJson, string positionCallsign = "OAK_TWR", string positionType = "TWR")
    {
        if (Engine(scenarioJson, positionCallsign, positionType) is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(Callsign)!;
        engine.AfterAircraftSpawned(ac);
        StripItemRecord strip = Assert.Single(engine.Strips.Items.Values);
        bool showDest = DisplaysDestination(positionCallsign);

        string[] fields = StripMutations.BuildDepartureStripFields(ac, engine.Scenario!.SimTimeUtc, showDest);

        Assert.Equal(strip.FieldValues, fields);
        AssertNoTimeLeavesOnlyBox6Empty(ac, showDest, fields);
    }

    private static void AssertNoTimeLeavesOnlyBox6Empty(AircraftState ac, bool showDest, string[] timed)
    {
        string[] untimed = StripMutations.BuildDepartureStripFields(ac, (DateTime?)null, showDest);
        Assert.Equal("", untimed[StripMutations.FieldIdxPropDep]);
        Assert.NotEqual("", timed[StripMutations.FieldIdxPropDep]);
        string[] expected = [.. timed];
        expected[StripMutations.FieldIdxPropDep] = "";
        Assert.Equal(expected, untimed);
    }

    [Fact]
    public void Departure_IfrRoute_MatchesTheEngine() => AssertDepartureMatchesEngine(OakDeparture(Plan("IFR", 35000, "CNDEL5 CNDEL", "")));

    [Fact]
    public void Departure_Vfr_MatchesTheEngine() => AssertDepartureMatchesEngine(OakDeparture(Plan("VFR", 4500, "", "")));

    [Fact]
    public void Departure_Otp_MatchesTheEngine()
    {
        if (Engine(OakDeparture(Plan("IFR", 5500, "", "")), "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(Callsign)!;
        ac.FlightPlan.Altitude = PlannedAltitude.Otp(5500);
        engine.AfterAircraftSpawned(ac);
        StripItemRecord strip = Assert.Single(engine.Strips.Items.Values);

        string[] fields = StripMutations.BuildDepartureStripFields(ac, engine.Scenario!.SimTimeUtc, DisplaysDestination("OAK_TWR"));

        Assert.Equal(strip.FieldValues, fields);
        Assert.Equal(StripMutations.FormatRequestedAltitude(PlannedAltitude.Otp(5500)), fields[StripMutations.FieldIdxReqAlt]);
    }

    [Fact]
    public void Departure_NoRemarks_MatchesTheEngine() => AssertDepartureMatchesEngine(OakDeparture(Plan("IFR", 35000, "SFO", "")));

    [Fact]
    public void Departure_RemarksOnly_KeepsTheVoiceToken()
    {
        AssertDepartureMatchesEngine(OakDeparture(Plan("IFR", 35000, "", "/t/ NEW PILOT")));
        if (Engine(OakDeparture(Plan("IFR", 35000, "", "/t/ NEW PILOT")), "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        string[] fields = StripMutations.BuildDepartureStripFields(engine.FindAircraft(Callsign)!, (DateTime?)null, true);

        Assert.EndsWith("\n/t/ NEW PILOT", fields[StripMutations.FieldIdxRouteRemarks]);
    }

    [Fact]
    public void Departure_DisplayDestinationOn_MatchesTheEngine()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.True(DisplaysDestination("OAK_TWR"));
        AssertDepartureMatchesEngine(OakDeparture(Plan("IFR", 35000, "", "")));
    }

    [Fact]
    public void Departure_DisplayDestinationOff_MatchesTheEngine()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.False(DisplaysDestination("FAT_GND"));
        string plan = """
            { "rules": "IFR", "departure": "KFAT", "destination": "KLAX", "cruiseAltitude": 24000, "cruiseSpeed": 250,
              "route": "", "remarks": "", "aircraftType": "CRJ2/L" }
            """;
        AssertDepartureMatchesEngine(Departure("FAT", 36.7762, -119.7181, plan, "CRJ2"), "FAT_GND", "GND");
    }

    [Fact]
    public void Departure_UnknownType_PrintsNoCwt()
    {
        AssertDepartureMatchesEngine(OakDeparture(Plan("IFR", 35000, "", "", "ZZZZ/L")));
        if (Engine(OakDeparture(Plan("IFR", 35000, "", "", "ZZZZ/L")), "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        string[] fields = StripMutations.BuildDepartureStripFields(engine.FindAircraft(Callsign)!, (DateTime?)null, true);

        Assert.Equal("ZZZZ/L", fields[StripMutations.FieldIdxEquipment]);
    }

    [Fact]
    public void Departure_ColdCall_MatchesTheEngineRequest()
    {
        if (Engine(OakDeparture(null), "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(Callsign)!;
        Assert.False(ac.FlightPlan.HasFlightPlan);
        StripItemRecord strip = StripMutations.RequestDepartureStripForAircraft(
            engine.Strips,
            ac,
            engine.Scenario!,
            new StripPrintTarget("OAK", false)
        );

        string[] fields = StripMutations.BuildDepartureStripFields(ac, engine.Scenario!.SimTimeUtc, false);

        Assert.Equal(strip.FieldValues, fields);
    }

    [Fact]
    public void Departure_ScenarioAircraft_HasNoCidOrBeacon()
    {
        Scenario scenario = System.Text.Json.JsonSerializer.Deserialize<Scenario>(OakDeparture(Plan("IFR", 35000, "", "")))!;
        AircraftState ac = ScenarioLoader.CreateBaseState(scenario.Aircraft[0], scenario.PrimaryAirportId, null);

        string[] fields = StripMutations.BuildDepartureStripFields(ac, (DateTime?)null, false);

        Assert.Equal(
            ("", "", ""),
            (fields[StripMutations.FieldIdxCid], fields[StripMutations.FieldIdxBeacon], fields[StripMutations.FieldIdxPropDep])
        );
        Assert.Equal(Callsign, fields[StripMutations.FieldIdxAircraftId]);
    }

    [Fact]
    public void Arrival_MatchesTheEngine()
    {
        if (Engine(ArrivalToOak, "OAK_APP", "APP") is not { } engine)
        {
            return;
        }

        engine.TickAutoArrivalStrips();
        StripItemRecord strip = Assert.Single(engine.Strips.Items.Values);
        AircraftState ac = engine.FindAircraft(ArrivalCallsign)!;
        (double lat, double lon) = StripRequests.ResolveAirportPosition("OAK")!.Value;
        double etaMinutes = (GeoMath.DistanceNm(ac.Position, new LatLon(lat, lon)) / ac.GroundSpeed) * 60.0;

        string[] fields = StripMutations.BuildArrivalStripFields(ac, engine.Scenario!.SimTimeUtc, etaMinutes);
        string[] untimed = StripMutations.BuildArrivalStripFields(ac, (DateTime?)null, etaMinutes);

        Assert.Equal(strip.FieldValues, fields);
        Assert.Equal("", untimed[StripMutations.FieldIdxEta]);
        string[] expected = [.. fields];
        expected[StripMutations.FieldIdxEta] = "";
        Assert.Equal(expected, untimed);
        Assert.EndsWith("/t/ NEW PILOT", fields[StripMutations.FieldIdxDestRemarks]);
    }
}
