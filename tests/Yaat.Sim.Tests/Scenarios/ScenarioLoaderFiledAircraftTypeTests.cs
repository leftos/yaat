using Xunit;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// A scenario's filed aircraft type (<c>flightplan.aircraftType</c>) loads split by the shared parser: the plan keeps the
/// bare type, element a lands in the number of aircraft and the special aircraft indicator, and the suffix in the
/// equipment suffix (/A when the filed string gives none).
/// </summary>
public class ScenarioLoaderFiledAircraftTypeTests
{
    [Theory]
    [InlineData("H/A306/L", "A306", "L", null, true)]
    [InlineData("H/A306", "A306", "A", null, true)]
    [InlineData("2/C130/G", "C130", "G", 2, false)]
    [InlineData("B738/L", "B738", "L", null, false)]
    [InlineData("B738", "B738", "A", null, false)]
    public void ScenarioLoad_FiledType_StoresBareTypeWithSuffixAndElementAApart(
        string filed,
        string expectedType,
        string expectedSuffix,
        int? expectedCount,
        bool expectedSpecialIndicator
    )
    {
        TestVnasData.EnsureInitialized();
        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "UPS2941",
            AircraftType = "A306",
            FlightPlan = new ScenarioFlightPlan { AircraftType = filed, Departure = "KOAK" },
        };

        AircraftState ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: null, primaryApproach: null);

        Assert.Equal(expectedType, ac.FlightPlan.AircraftType);
        Assert.Equal(expectedSuffix, ac.FlightPlan.EquipmentSuffix);
        Assert.Equal(expectedCount, ac.FlightPlan.NumberOfAircraft);
        Assert.Equal(expectedSpecialIndicator, ac.FlightPlan.HasSpecialAircraftIndicator);
    }
}
