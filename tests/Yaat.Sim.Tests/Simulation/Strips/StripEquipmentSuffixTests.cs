using Xunit;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Simulation.Strips;

/// <summary>
/// A scenario files its aircraft type without a suffix to make the student ask for it, and the strip shows the type
/// exactly as filed: the equipment column reads <c>{CWT}/{type}</c> with no invented <c>/A</c> tail, as vNAS shows it.
/// </summary>
public class StripEquipmentSuffixTests
{
    [Fact]
    public void ScenarioFiledTypeWithoutSuffix_DepartureStripEquipmentHasNoSlashA()
    {
        TestVnasData.EnsureInitialized();
        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "SWA1234",
            AircraftType = "B738",
            FlightPlan = new ScenarioFlightPlan
            {
                AircraftType = "B738",
                Departure = "KOAK",
                Destination = "KSFO",
            },
        };

        AircraftState ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: null, primaryApproach: null);
        var scenario = new SimScenarioState
        {
            ScenarioId = "strip-equipment-suffix",
            ScenarioName = "Strip equipment suffix",
            RngSeed = 7,
            OriginalScenarioJson = "{}",
        };

        string[] fields = StripMutations.BuildDepartureStripFields(ac, scenario, displayDestinationAirportIds: false);

        Assert.Equal("F/B738", fields[StripMutations.FieldIdxEquipment]);
    }
}
