using Xunit;

namespace Yaat.Sim.Tests;

public sealed class FaaAircraftDataServiceTests
{
    public FaaAircraftDataServiceTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void GetApproachSpeed_KnownTypes_ReturnExpectedValues()
    {
        Assert.NotNull(AircraftApproachSpeed.GetApproachSpeed("B738"));
        Assert.NotNull(AircraftApproachSpeed.GetApproachSpeed("A320"));
        Assert.NotNull(AircraftApproachSpeed.GetApproachSpeed("C172"));
    }

    [Fact]
    public void NoCacheAvailable_CategoryDefaultsUsed()
    {
        // Unknown type → approach speed returns null → category default
        double speed = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet, "ZZZZ");
        Assert.Equal(CategoryPerformance.ApproachSpeed(AircraftCategory.Jet), speed);
    }
}
