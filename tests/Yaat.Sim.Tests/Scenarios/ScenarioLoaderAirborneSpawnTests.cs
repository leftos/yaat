using System.Text.Json;
using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// <see cref="ScenarioLoader.IsAirborneSpawn"/> is the loader's own ground gate as the strip editor reads it, so for every
/// starting-condition kind the loader places its answer must match the aircraft the loader actually spawned.
/// </summary>
public class ScenarioLoaderAirborneSpawnTests
{
    private const string PrimaryAirportId = "OAK";

    private static readonly TestAirportGroundData GroundData = new();

    /// <summary>OAK, one aircraft per kind: parking 25 and runway 28R exist in the committed layout and navdata.</summary>
    private const string ScenarioJson = """
        {
          "id": "airborne-spawn",
          "name": "Airborne spawn",
          "primaryAirportId": "OAK",
          "aircraft": [
            { "id": "parked", "aircraftId": "PARK1", "aircraftType": "B738", "airportId": "OAK",
              "startingConditions": { "type": "Parking", "parking": "25" },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000 } },
            { "id": "runway", "aircraftId": "RWY1", "aircraftType": "B738", "airportId": "OAK",
              "startingConditions": { "type": "OnRunway", "runway": "28R" },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000 } },
            { "id": "final", "aircraftId": "FIN1", "aircraftType": "B738", "airportId": "OAK",
              "startingConditions": { "type": "OnFinal", "runway": "28R", "distanceFromRunway": 6.0 },
              "flightplan": { "rules": "IFR", "departure": "KLAX", "destination": "KOAK", "cruiseAltitude": 35000 } },
            { "id": "ground", "aircraftId": "GND1", "aircraftType": "CL60", "airportId": "OAK",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.726, "lon": -122.205333 }, "altitude": 9, "heading": 120 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KBJC" } },
            { "id": "cruise", "aircraftId": "AIR1", "aircraftType": "A320", "airportId": "OAK",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.6, "lon": -121.95 }, "altitude": 35000, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KBJC", "cruiseAltitude": 35000 } },
            { "id": "fix", "aircraftId": "FIX1", "aircraftType": "A320", "airportId": "OAK",
              "startingConditions": { "type": "FixOrFrd", "fix": "OSI", "altitude": 6000, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KBJC", "cruiseAltitude": 6000 } }
          ]
        }
        """;

    private static readonly Dictionary<string, bool> ExpectedAirborne = new(StringComparer.Ordinal)
    {
        ["PARK1"] = false,
        ["RWY1"] = false,
        ["FIN1"] = true,
        ["GND1"] = false,
        ["AIR1"] = true,
        ["FIX1"] = true,
    };

    public ScenarioLoaderAirborneSpawnTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void IsAirborneSpawn_AgreesWithEveryLoadedStartingCondition()
    {
        ScenarioLoadResult loaded = ScenarioLoader.Load(ScenarioJson, GroundData, new Random(0), MagneticDeclination.EvaluationDateUtc);
        var states = loaded.AllAircraftStates.ToDictionary(state => state.Callsign, StringComparer.Ordinal);
        Scenario scenario = JsonSerializer.Deserialize<Scenario>(ScenarioJson, ScenarioLoader.JsonOptions)!;

        Assert.Equal(ExpectedAirborne.Count, scenario.Aircraft.Count);
        Assert.Equal(ExpectedAirborne.Count, states.Count);
        foreach (ScenarioAircraft aircraft in scenario.Aircraft)
        {
            string callsign = aircraft.AircraftId;
            Assert.True(
                states.TryGetValue(callsign, out AircraftState? state),
                $"{callsign} ({aircraft.StartingConditions.Type}) did not load: {string.Join("; ", loaded.Warnings)}"
            );
            Assert.NotNull(state);

            bool expected = ExpectedAirborne[callsign];
            Assert.Equal(expected, !state.IsOnGround);
            Assert.Equal(expected, ScenarioLoader.IsAirborneSpawn(aircraft, PrimaryAirportId));
        }
    }
}
