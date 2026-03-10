using Xunit;

namespace Yaat.Sim.Tests;

public sealed class AircraftApproachSpeedTests : IDisposable
{
    public AircraftApproachSpeedTests()
    {
        // Reset to empty before each test
        AircraftApproachSpeed.Initialize(new Dictionary<string, int>());
    }

    public void Dispose()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int>());
    }

    [Fact]
    public void GetApproachSpeed_Uninitialized_ReturnsNull()
    {
        Assert.Null(AircraftApproachSpeed.GetApproachSpeed("B738"));
    }

    [Fact]
    public void GetApproachSpeed_KnownType_ReturnsSpeed()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

        Assert.Equal(144, AircraftApproachSpeed.GetApproachSpeed("B738"));
    }

    [Fact]
    public void GetApproachSpeed_CaseInsensitive()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

        Assert.Equal(144, AircraftApproachSpeed.GetApproachSpeed("b738"));
    }

    [Fact]
    public void GetApproachSpeed_StripsPrefix()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

        Assert.Equal(144, AircraftApproachSpeed.GetApproachSpeed("H/B738"));
    }

    [Fact]
    public void GetApproachSpeed_UnknownType_ReturnsNull()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

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
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

        double speed = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet, "B738");

        Assert.Equal(144, speed);
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
        // B738: approach 144, category Jet approach default 140
        // Ratio: 144/140 = 1.02857
        // Jet touchdown default: 135
        // Expected: 135 * (144/140) ≈ 138.86
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

        double speed = CategoryPerformance.TouchdownSpeed(AircraftCategory.Jet, "B738");

        Assert.InRange(speed, 138, 140);
    }

    [Fact]
    public void DownwindSpeed_TypeAware_ScalesProportionally()
    {
        // CRJ7: approach 135 (slower than Jet default 140)
        // Ratio: 135/140 = 0.9643
        // Jet downwind default: 200
        // Expected: 200 * (135/140) ≈ 192.86
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["CRJ7"] = 135 });

        double speed = CategoryPerformance.DownwindSpeed(AircraftCategory.Jet, "CRJ7");

        Assert.InRange(speed, 192, 194);
    }

    [Fact]
    public void BaseSpeed_TypeAware_ScalesProportionally()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["C172"] = 62 });

        double speed = CategoryPerformance.BaseSpeed(AircraftCategory.Piston, "C172");
        double catBase = CategoryPerformance.BaseSpeed(AircraftCategory.Piston);
        double catApch = CategoryPerformance.ApproachSpeed(AircraftCategory.Piston);

        // Should be scaled by 62/75
        double expected = catBase * (62.0 / catApch);
        Assert.InRange(speed, expected - 1, expected + 1);
    }

    [Fact]
    public void DefaultSpeed_TypeAware_ScalesProportionally()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int> { ["B738"] = 144 });

        double catDefault = CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 20000);
        double typeDefault = CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 20000, "B738");

        // Ratio should be 144/140
        double expectedRatio = 144.0 / CategoryPerformance.ApproachSpeed(AircraftCategory.Jet);
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
