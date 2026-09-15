using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// The scenario validator flags a physical aircraft type that disagrees with the filed one
/// (top-level <c>aircraftType</c> vs <c>flightplan.aircraftType</c>).
/// </summary>
public class ScenarioValidatorTypeMismatchTests
{
    public ScenarioValidatorTypeMismatchTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static Scenario MakeScenario(string aircraftType, ScenarioFlightPlan? flightPlan)
    {
        return new Scenario
        {
            Id = "test",
            Name = "Test",
            Aircraft =
            [
                new ScenarioAircraft
                {
                    AircraftId = "UAL123",
                    AircraftType = aircraftType,
                    StartingConditions = new StartingConditions { Type = "Coordinates" },
                    FlightPlan = flightPlan,
                },
            ],
        };
    }

    [Fact]
    public void Validator_FlagsDifferentBaseTypes()
    {
        var scenario = MakeScenario(
            "A388",
            new ScenarioFlightPlan
            {
                Departure = "KSFO",
                Destination = "KLAX",
                AircraftType = "B744",
            }
        );

        var result = ScenarioValidator.Validate(scenario);

        var mismatch = Assert.Single(result.AircraftTypeMismatches);
        Assert.Equal("UAL123", mismatch.AircraftId);
        Assert.Equal("A388", mismatch.ActualType);
        Assert.Equal("B744", mismatch.FiledType);
    }

    [Theory]
    [InlineData("H/B763/L", "B763")]
    [InlineData("B738/L", "b738")]
    [InlineData("C130", "2/C130/G")]
    [InlineData("3/F18H/P", "F18H")]
    public void Validator_IgnoresWakePrefixAndSuffix(string physicalType, string filedType)
    {
        var scenario = MakeScenario(
            physicalType,
            new ScenarioFlightPlan
            {
                Departure = "KSFO",
                Destination = "KLAX",
                AircraftType = filedType,
            }
        );

        var result = ScenarioValidator.Validate(scenario);

        Assert.Empty(result.AircraftTypeMismatches);
    }

    [Fact]
    public void Validator_IgnoresBlankFiledType()
    {
        Assert.Empty(
            ScenarioValidator
                .Validate(
                    MakeScenario(
                        "A388",
                        new ScenarioFlightPlan
                        {
                            Departure = "KSFO",
                            Destination = "KLAX",
                            AircraftType = "",
                        }
                    )
                )
                .AircraftTypeMismatches
        );

        Assert.Empty(ScenarioValidator.Validate(MakeScenario("A388", flightPlan: null)).AircraftTypeMismatches);
    }
}
