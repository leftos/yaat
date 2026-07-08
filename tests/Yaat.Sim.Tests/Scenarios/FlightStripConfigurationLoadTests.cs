using System.Text.Json;
using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Issue #277: the scenario's <c>flightStripConfigurations</c> pre-assigns aircraft (by ULID)
/// to a strip bay/rack. Verifies the model captures the full shape and that
/// <see cref="ScenarioLoader.Load"/> resolves it to a callsign-keyed map (ULID → callsign join),
/// which is the link the server's spawn hook uses to place strips.
/// </summary>
[Collection("NavDbMutator")]
public class FlightStripConfigurationLoadTests
{
    public FlightStripConfigurationLoadTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string ScenarioWithStripConfig = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "flightStripConfigurations": [
            {
              "id": "cfg1",
              "facilityId": "OAK",
              "bayId": "BAY-GND-1",
              "rack": 2,
              "aircraftIds": ["ac1", "ac3"]
            }
          ],
          "aircraft": [
            { "id": "ac1", "aircraftId": "N111", "aircraftType": "C172", "startingConditions": { "type": "Parking", "parking": "A1" } },
            { "id": "ac2", "aircraftId": "N222", "aircraftType": "C172", "startingConditions": { "type": "Parking", "parking": "A2" } }
          ]
        }
        """;

    [Fact]
    public void FlightStripConfiguration_DeserializesFullShape()
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(ScenarioWithStripConfig);

        Assert.NotNull(scenario);
        var config = Assert.Single(scenario!.FlightStripConfigurations);
        Assert.Equal("OAK", config.FacilityId);
        Assert.Equal("BAY-GND-1", config.BayId);
        Assert.Equal(2, config.Rack);
        Assert.Equal(["ac1", "ac3"], config.AircraftIds);
    }

    [Fact]
    public void Load_ResolvesStripBayAssignment_ByCallsign()
    {
        var result = ScenarioLoader.Load(ScenarioWithStripConfig, groundData: null, new Random(0));

        // ac1 → callsign N111 is configured; ac3 is referenced but not a real aircraft (skipped);
        // ac2/N222 is not configured.
        Assert.True(result.InitialStripBayByCallsign.TryGetValue("N111", out var assignment));
        Assert.Equal("BAY-GND-1", assignment!.BayId);
        Assert.Equal(2, assignment.Rack);
        Assert.Equal("OAK", assignment.FacilityId);
        Assert.False(result.InitialStripBayByCallsign.ContainsKey("N222"));
    }

    [Fact]
    public void Load_NoStripConfig_YieldsEmptyMap()
    {
        const string noConfig = """
            {
              "id": "test",
              "name": "Test",
              "aircraft": [
                { "id": "ac1", "aircraftId": "N111", "aircraftType": "C172", "startingConditions": { "type": "Parking", "parking": "A1" } }
              ]
            }
            """;

        var result = ScenarioLoader.Load(noConfig, groundData: null, new Random(0));

        Assert.Empty(result.InitialStripBayByCallsign);
    }
}
