using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="CategoryPerformance.CornerSpeedForAngle"/>: the speed taper from
/// <see cref="CategoryPerformance.TaxiCornerSpeed"/> at 90° to <see cref="CategoryPerformance.TaxiTightCornerSpeed"/>
/// at 150°+, and the invariant that no category is allowed to take a tight corner faster than a standard one.
/// </summary>
public class CornerSpeedForAngleTests
{
    [Fact]
    public void Piston_TapersFrom90To150Degrees()
    {
        double atNinety = CategoryPerformance.CornerSpeedForAngle(AircraftCategory.Piston, 90.0);
        double atOneFifty = CategoryPerformance.CornerSpeedForAngle(AircraftCategory.Piston, 150.0);
        double atOneEighty = CategoryPerformance.CornerSpeedForAngle(AircraftCategory.Piston, 180.0);

        Assert.True(atOneFifty < atNinety);
        Assert.Equal(5.0, atOneFifty, 1e-9);
        Assert.Equal(5.0, atOneEighty, 1e-9);
    }

    [Fact]
    public void Piston_NinetyDegreeTurnStaysAtCornerSpeed() =>
        Assert.Equal(10.0, CategoryPerformance.CornerSpeedForAngle(AircraftCategory.Piston, 90.0), 1e-9);

    [Theory]
    [InlineData(AircraftCategory.Jet)]
    [InlineData(AircraftCategory.Turboprop)]
    [InlineData(AircraftCategory.Piston)]
    [InlineData(AircraftCategory.Helicopter)]
    public void EveryCategory_TightCornerIsNoFasterThanCorner(AircraftCategory category) =>
        Assert.True(CategoryPerformance.TaxiTightCornerSpeed(category) <= CategoryPerformance.TaxiCornerSpeed(category));
}
