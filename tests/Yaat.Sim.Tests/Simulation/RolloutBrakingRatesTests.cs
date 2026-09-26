using Xunit;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Pins the per-category landing-rollout braking rates (aviation rulings 2026-09-24, judgement calls with no FAA
/// figure) and the ordering that keeps them coherent: routine rollout &lt; comfortable exit &lt; firm exit &lt;= expedite.
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
    [InlineData(AircraftCategory.Jet)]
    [InlineData(AircraftCategory.Turboprop)]
    [InlineData(AircraftCategory.Piston)]
    public void RolloutBelowComfortableExitBelowFirm_FixedWing(AircraftCategory category)
    {
        double rollout = CategoryPerformance.RolloutDecelRate(category);
        double comfortable = CategoryPerformance.ComfortableExitDecelRate(category);
        double firm = RolloutBraking.FirmBrakingRateKtsPerSec;

        Assert.True(rollout < comfortable, $"{category}: RolloutDecelRate {rollout} should be below ComfortableExitDecelRate {comfortable}");
        Assert.True(comfortable < firm, $"{category}: ComfortableExitDecelRate {comfortable} should be below the firm rate {firm}");
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
