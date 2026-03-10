using Xunit;

namespace Yaat.Sim.Tests;

public sealed class AircraftApproachSpeedTests
{
    public AircraftApproachSpeedTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void GetApproachSpeed_KnownType_ReturnsSpeed()
    {
        var speed = AircraftApproachSpeed.GetApproachSpeed("B738");
        Assert.NotNull(speed);
        Assert.True(speed > 100 && speed < 200, $"B738 approach speed {speed} out of expected range");
    }

    [Fact]
    public void GetApproachSpeed_CaseInsensitive()
    {
        Assert.Equal(AircraftApproachSpeed.GetApproachSpeed("B738"), AircraftApproachSpeed.GetApproachSpeed("b738"));
    }

    [Fact]
    public void GetApproachSpeed_StripsPrefix()
    {
        Assert.Equal(AircraftApproachSpeed.GetApproachSpeed("B738"), AircraftApproachSpeed.GetApproachSpeed("H/B738"));
    }

    [Fact]
    public void GetApproachSpeed_UnknownType_ReturnsNull()
    {
        Assert.Null(AircraftApproachSpeed.GetApproachSpeed("ZZZZ"));
    }

    [Fact]
    public void GetApproachSpeed_NullInput_ReturnsNull()
    {
        Assert.Null(AircraftApproachSpeed.GetApproachSpeed(null));
    }

    [Fact]
    public void GetApproachSpeed_EmptyInput_ReturnsNull()
    {
        Assert.Null(AircraftApproachSpeed.GetApproachSpeed(""));
    }

    [Fact]
    public void ApproachSpeed_TypeAware_UsesTypeSpeed()
    {
        int? typeSpeed = AircraftApproachSpeed.GetApproachSpeed("B738");
        Assert.NotNull(typeSpeed);

        double speed = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet, "B738");
        Assert.Equal(typeSpeed.Value, speed);
    }

    [Fact]
    public void ApproachSpeed_TypeAware_FallsBackToCategory()
    {
        double speed = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet, "ZZZZ");
        Assert.Equal(CategoryPerformance.ApproachSpeed(AircraftCategory.Jet), speed);
    }

    [Fact]
    public void TouchdownSpeed_TypeAware_ScalesProportionally()
    {
        int? typeApproach = AircraftApproachSpeed.GetApproachSpeed("B738");
        Assert.NotNull(typeApproach);

        double catApproach = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet);
        double catTouchdown = CategoryPerformance.TouchdownSpeed(AircraftCategory.Jet);
        double expectedRatio = typeApproach.Value / catApproach;
        double expected = catTouchdown * expectedRatio;

        double speed = CategoryPerformance.TouchdownSpeed(AircraftCategory.Jet, "B738");
        Assert.InRange(speed, expected - 1, expected + 1);
    }

    [Fact]
    public void DownwindSpeed_TypeAware_ScalesProportionally()
    {
        int? typeApproach = AircraftApproachSpeed.GetApproachSpeed("CRJ7");
        Assert.NotNull(typeApproach);

        double catApproach = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet);
        double catDownwind = CategoryPerformance.DownwindSpeed(AircraftCategory.Jet);
        double expected = catDownwind * (typeApproach.Value / catApproach);

        double speed = CategoryPerformance.DownwindSpeed(AircraftCategory.Jet, "CRJ7");
        Assert.InRange(speed, expected - 1, expected + 1);
    }

    [Fact]
    public void BaseSpeed_TypeAware_ScalesProportionally()
    {
        int? typeApproach = AircraftApproachSpeed.GetApproachSpeed("C172");
        Assert.NotNull(typeApproach);

        double catBase = CategoryPerformance.BaseSpeed(AircraftCategory.Piston);
        double catApproach = CategoryPerformance.ApproachSpeed(AircraftCategory.Piston);
        double expected = catBase * (typeApproach.Value / catApproach);

        double speed = CategoryPerformance.BaseSpeed(AircraftCategory.Piston, "C172");
        Assert.InRange(speed, expected - 1, expected + 1);
    }

    [Fact]
    public void DefaultSpeed_TypeAware_ScalesProportionally()
    {
        int? typeApproach = AircraftApproachSpeed.GetApproachSpeed("B738");
        Assert.NotNull(typeApproach);

        double catDefault = CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 20000);
        double catApproach = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet);
        double expectedRatio = typeApproach.Value / catApproach;

        double typeDefault = CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 20000, "B738");
        Assert.InRange(typeDefault, catDefault * expectedRatio - 1, catDefault * expectedRatio + 1);
    }

    [Fact]
    public void DefaultSpeed_TypeAware_NullType_ReturnsCategoryDefault()
    {
        double catDefault = CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 20000);
        double typeDefault = CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 20000, null);
        Assert.Equal(catDefault, typeDefault);
    }
}
