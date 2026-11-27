using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// A scenario's primary approach seeds the expected approach of an aircraft bound for the primary airport, whichever
/// form either id is written in: "KOAK" and "OAK", and "PHNL" and "HNL" (YAAT-2909), are the same airport.
/// </summary>
public class ScenarioLoaderPrimaryApproachTests
{
    [Theory]
    [InlineData("KOAK", "OAK", "I28R")]
    [InlineData("PHNL", "HNL", "I08L")]
    [InlineData("HNL", "PHNL", "I08L")]
    public void CreateBaseState_DestinationIsThePrimaryInAnotherForm_TakesThePrimaryApproach(string destination, string primary, string approach)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "HAL12",
            AircraftType = "A321",
            FlightPlan = new ScenarioFlightPlan { AircraftType = "A321/L", Destination = destination },
        };

        AircraftState ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: primary, primaryApproach: approach);

        Assert.Equal(approach, ac.Approach.Expected);
    }

    [Fact]
    public void CreateBaseState_DestinationIsAnotherAirport_TakesNoPrimaryApproach()
    {
        TestVnasData.EnsureInitialized();
        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "HAL12",
            AircraftType = "A321",
            FlightPlan = new ScenarioFlightPlan { AircraftType = "A321/L", Destination = "PHOG" },
        };

        AircraftState ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: "HNL", primaryApproach: "I08L");

        Assert.Null(ac.Approach.Expected);
    }
}
