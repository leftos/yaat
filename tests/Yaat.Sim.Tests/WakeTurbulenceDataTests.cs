using Xunit;

namespace Yaat.Sim.Tests;

public class WakeTurbulenceDataTests
{
    public WakeTurbulenceDataTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("A388", "A")]
    [InlineData("B77W", "B")]
    [InlineData("B763", "C")]
    [InlineData("B738", "F")]
    [InlineData("E170", "G")]
    [InlineData("C172", "I")]
    public void GetCwt_KnownTypes_ReturnsCorrectCode(string type, string expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.GetCwt(type));
    }

    [Fact]
    public void GetCwt_UnknownType_ReturnsNull()
    {
        Assert.Null(WakeTurbulenceData.GetCwt("ZZZZ"));
    }

    [Fact]
    public void GetCwt_TypeWithEquipmentSuffix_StripsSlash()
    {
        Assert.Equal(WakeTurbulenceData.GetCwt("B738"), WakeTurbulenceData.GetCwt("B738/L"));
    }

    [Fact]
    public void GetCwt_CaseInsensitive()
    {
        Assert.Equal(WakeTurbulenceData.GetCwt("B738"), WakeTurbulenceData.GetCwt("b738"));
    }

    [Theory]
    [InlineData("A388", 15.0)]
    [InlineData("B77W", 12.0)]
    [InlineData("B763", 10.0)]
    [InlineData("B738", 7.0)]
    [InlineData("E170", 5.0)]
    [InlineData("C172", 2.5)]
    public void TrafficDetectionRangeNm_CwtBased(string type, double expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.TrafficDetectionRangeNm(type, AircraftCategory.Jet));
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, 7.0)]
    [InlineData(AircraftCategory.Turboprop, 5.0)]
    [InlineData(AircraftCategory.Piston, 2.5)]
    [InlineData(AircraftCategory.Helicopter, 2.5)]
    public void TrafficDetectionRangeNm_FallbackByCategory(AircraftCategory cat, double expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.TrafficDetectionRangeNm("ZZZZ", cat));
    }
}
