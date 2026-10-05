using Xunit;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Pins the per-category landing-rollout braking rates (aviation rulings 2026-09-24, judgement calls with no FAA
/// figure) and the ordering that keeps them coherent: routine rollout &lt;= comfortable exit &lt;= firm exit &lt; expedite.
/// </summary>
public class RolloutBrakingRatesTests
{
    private static readonly AircraftCategory[] FixedWing = [AircraftCategory.Jet, AircraftCategory.Turboprop, AircraftCategory.Piston];

    [Theory]
    [InlineData(AircraftCategory.Jet, 3.6)]
    [InlineData(AircraftCategory.Turboprop, 3.0)]
    [InlineData(AircraftCategory.Piston, 2.5)]
    [InlineData(AircraftCategory.Helicopter, 0.0)]
    public void RolloutDecelRate_PerCategory(AircraftCategory category, double expected) =>
        Assert.Equal(expected, CategoryPerformance.RolloutDecelRate(category));

    [Theory]
    [InlineData(AircraftCategory.Jet, 4.5)]
    [InlineData(AircraftCategory.Turboprop, 3.75)]
    [InlineData(AircraftCategory.Piston, 3.75)]
    [InlineData(AircraftCategory.Helicopter, 0.0)]
    public void ComfortableExitDecelRate_PerCategory(AircraftCategory category, double expected) =>
        Assert.Equal(expected, CategoryPerformance.ComfortableExitDecelRate(category));

    [Theory]
    [InlineData(AircraftCategory.Jet, 1.0)]
    [InlineData(AircraftCategory.Turboprop, 1.5)]
    [InlineData(AircraftCategory.Piston, 1.5)]
    [InlineData(AircraftCategory.Helicopter, 0.0)]
    public void TouchAndGoDecelRate_PerCategory(AircraftCategory category, double expected) =>
        Assert.Equal(expected, CategoryPerformance.TouchAndGoDecelRate(category));

    [Theory]
    [InlineData(AircraftCategory.Jet, 5.0)]
    [InlineData(AircraftCategory.Turboprop, 5.0)]
    [InlineData(AircraftCategory.Piston, 4.0)]
    [InlineData(AircraftCategory.Helicopter, 3.0)]
    public void FirmBrakingRate_PerCategory(AircraftCategory category, double expected) =>
        Assert.Equal(expected, CategoryPerformance.FirmBrakingRate(category));

    [Fact]
    public void PistonExpediteExitDecelRate_Is4Point5() => Assert.Equal(4.5, CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston));

    [Theory]
    [InlineData(AircraftCategory.Jet)]
    [InlineData(AircraftCategory.Turboprop)]
    [InlineData(AircraftCategory.Piston)]
    public void RolloutAtOrBelowComfortableAtOrBelowFirmBelowExpedite_FixedWing(AircraftCategory category)
    {
        double rollout = CategoryPerformance.RolloutDecelRate(category);
        double comfortable = CategoryPerformance.ComfortableExitDecelRate(category);
        double firm = CategoryPerformance.FirmBrakingRate(category);
        double expedite = CategoryPerformance.ExpediteExitDecelRate(category);

        Assert.True(rollout <= comfortable, $"{category}: RolloutDecelRate {rollout} should not exceed ComfortableExitDecelRate {comfortable}");
        Assert.True(comfortable <= firm, $"{category}: ComfortableExitDecelRate {comfortable} should not exceed FirmBrakingRate {firm}");
        Assert.True(firm < expedite, $"{category}: FirmBrakingRate {firm} should be below ExpediteExitDecelRate {expedite}");
    }

    [Fact]
    public void FirmBelowExpedite_Helicopter()
    {
        double firm = CategoryPerformance.FirmBrakingRate(AircraftCategory.Helicopter);
        double expedite = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Helicopter);
        Assert.True(firm < expedite, $"Helicopter: FirmBrakingRate {firm} should be below ExpediteExitDecelRate {expedite}");
    }

    [Fact]
    public void ComfortableExitAtOrBelowExpedite_FixedWing()
    {
        foreach (AircraftCategory category in FixedWing)
        {
            double comfortable = CategoryPerformance.ComfortableExitDecelRate(category);
            double expedite = CategoryPerformance.ExpediteExitDecelRate(category);
            Assert.True(
                comfortable <= expedite,
                $"{category}: ComfortableExitDecelRate {comfortable} should not exceed ExpediteExitDecelRate {expedite}"
            );
        }
    }
}
