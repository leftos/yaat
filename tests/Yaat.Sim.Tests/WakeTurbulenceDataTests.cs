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
    // Common airline types go through the FAA ACD physical-dimension formula
    // (~12 arcmin detection threshold, clamp [1.5, 10] nm). Ranges derived from
    // actual wingspan/length/tail data in FaaAcd.json.
    [InlineData("A388", 10.0)] // Super: silhouette ~332 ft, clamped
    [InlineData("B77W", 10.0)] // Heavy widebody: clamped
    [InlineData("B763", 10.0)] // Heavy widebody: ~10.3 nm, clamped
    [InlineData("B738", 7.6)] // Narrowbody jet
    [InlineData("C172", 2.0)] // Small GA
    public void TrafficDetectionRangeNm_PhysicalDimensions(string type, double expected)
    {
        var actual = WakeTurbulenceData.TrafficDetectionRangeNm(type, AircraftCategory.Jet);
        Assert.InRange(actual, expected - 0.5, expected + 0.5);
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, 7.6)]
    [InlineData(AircraftCategory.Turboprop, 4.8)]
    [InlineData(AircraftCategory.Piston, 2.0)]
    [InlineData(AircraftCategory.Helicopter, 2.0)]
    public void TrafficDetectionRangeNm_FallbackByCategory(AircraftCategory cat, double expected)
    {
        // "ZZZZ" has no FAA ACD record and no CWT entry, so falls all the way
        // through to the category fallback.
        Assert.Equal(expected, WakeTurbulenceData.TrafficDetectionRangeNm("ZZZZ", cat));
    }
}
