using Xunit;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

public sealed class FaaAircraftDataServiceTests
{
    public FaaAircraftDataServiceTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void FaaAcd_KnownTypes_HaveApproachSpeed()
    {
        Assert.NotNull(FaaAircraftDatabase.Get("B738")?.ApproachSpeedKnot);
        Assert.NotNull(FaaAircraftDatabase.Get("A320")?.ApproachSpeedKnot);
        Assert.NotNull(FaaAircraftDatabase.Get("C172")?.ApproachSpeedKnot);
    }

    [Fact]
    public void ApproachSpeed_UnknownType_FallsBackToCategory()
    {
        double speed = AircraftPerformance.ApproachSpeed("ZZZZ", AircraftCategory.Jet);
        Assert.Equal(CategoryPerformance.ApproachSpeed(AircraftCategory.Jet), speed);
    }
}
