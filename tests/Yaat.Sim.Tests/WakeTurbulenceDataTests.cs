using Xunit;

namespace Yaat.Sim.Tests;

[Collection("WakeTurbulenceData")]
public class WakeTurbulenceDataTests : IDisposable
{
    public WakeTurbulenceDataTests()
    {
        WakeTurbulenceData.Initialize(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["A388"] = "A",
                ["B77W"] = "B",
                ["B763"] = "C",
                ["B738"] = "D",
                ["E170"] = "E",
                ["C172"] = "F",
            }
        );
    }

    public void Dispose()
    {
        // Reset to empty to avoid test pollution
        WakeTurbulenceData.Initialize(new Dictionary<string, string>());
    }

    [Theory]
    [InlineData("A388", "A")]
    [InlineData("B77W", "B")]
    [InlineData("B763", "C")]
    [InlineData("B738", "D")]
    [InlineData("E170", "E")]
    [InlineData("C172", "F")]
    public void GetWtg_KnownTypes_ReturnsCorrectCode(string type, string expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.GetWtg(type));
    }

    [Fact]
    public void GetWtg_UnknownType_ReturnsNull()
    {
        Assert.Null(WakeTurbulenceData.GetWtg("ZZZZ"));
    }

    [Fact]
    public void GetWtg_TypeWithEquipmentSuffix_StripsSlash()
    {
        Assert.Equal("D", WakeTurbulenceData.GetWtg("B738/L"));
    }

    [Fact]
    public void GetWtg_CaseInsensitive()
    {
        Assert.Equal("D", WakeTurbulenceData.GetWtg("b738"));
    }

    [Theory]
    [InlineData("A388", 15.0)]
    [InlineData("B77W", 12.0)]
    [InlineData("B763", 10.0)]
    [InlineData("B738", 8.0)]
    [InlineData("E170", 6.0)]
    [InlineData("C172", 3.0)]
    public void TrafficDetectionRangeNm_WtgBased(string type, double expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.TrafficDetectionRangeNm(type, AircraftCategory.Jet));
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, 8.0)]
    [InlineData(AircraftCategory.Turboprop, 5.0)]
    [InlineData(AircraftCategory.Piston, 3.0)]
    [InlineData(AircraftCategory.Helicopter, 3.0)]
    public void TrafficDetectionRangeNm_FallbackByCategory(AircraftCategory cat, double expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.TrafficDetectionRangeNm("ZZZZ", cat));
    }
}
