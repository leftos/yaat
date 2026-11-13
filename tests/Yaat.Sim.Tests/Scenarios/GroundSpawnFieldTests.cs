using System.Globalization;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// A coordinate-placed ground spawn with no <c>airportId</c> takes its field from its filed departure, the airport its
/// layout comes from, so the layout it carries is its field's and it keeps running on it.
/// </summary>
public class GroundSpawnFieldTests
{
    private const string Callsign = "UAL1";

    public GroundSpawnFieldTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void CoordinatesGroundSpawn_WithNoAirportId_TakesItsDeparturesField()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout sfo = groundData.GetLayout("SFO") ?? throw new InvalidOperationException("the SFO test layout is missing");
        GroundNode stand = sfo.Nodes.Values.First(n => n.Type == GroundNodeType.Parking);
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        List<string> warnings = engine.LoadScenario(ScenarioJson(stand.Position), 42, new DateTime(2026, 11, 13, 18, 0, 0, DateTimeKind.Utc));

        engine.TickOneSecond();

        AircraftState aircraft =
            engine.World.FindAircraft(Callsign) ?? throw new InvalidOperationException($"{Callsign} did not load: {string.Join("; ", warnings)}");
        Assert.True(aircraft.IsOnGround);
        Assert.True(NavigationDatabase.AirportIdsMatch(aircraft.AirportId, "SFO"), $"AirportId is {aircraft.AirportId}");
        Assert.Same(sfo, aircraft.Ground.Layout);
        Assert.Same(sfo, engine.ResolveGroundLayout(aircraft));
    }

    private static string ScenarioJson(LatLon position)
    {
        string lat = position.Lat.ToString("R", CultureInfo.InvariantCulture);
        string lon = position.Lon.ToString("R", CultureInfo.InvariantCulture);
        return $$"""
            {
              "id": "ground-spawn-field", "name": "Ground spawn field", "artccId": "ZOA", "primaryAirportId": "OAK",
              "aircraft": [
                {
                  "id": "ac1", "aircraftId": "{{Callsign}}", "aircraftType": "B738",
                  "startingConditions": { "type": "Coordinates", "coordinates": { "lat": {{lat}}, "lon": {{lon}} } },
                  "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "route": "SFO" }
                }
              ]
            }
            """;
    }
}
