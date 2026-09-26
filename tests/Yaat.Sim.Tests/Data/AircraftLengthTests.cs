using Xunit;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// <see cref="AircraftLength.ResolveFt"/> reads the FAA database length and, for a type the database lacks, the CWT
/// bucket length.
/// </summary>
public class AircraftLengthTests
{
    [Fact]
    public void ResolveFt_KnownType_ReturnsFaaDatabaseLength()
    {
        TestVnasData.EnsureInitialized();

        Assert.Equal(129.5, AircraftLength.ResolveFt("B738"));
    }

    [Fact]
    public void ResolveFt_UnknownType_ReturnsCwtFallback()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(FaaAircraftDatabase.Get("A225"));

        Assert.Equal(240.0, AircraftLength.ResolveFt("A225"));
    }

    [Theory]
    [InlineData("A225", "A", 240.0)]
    [InlineData("C5M", "B", 220.0)]
    [InlineData("B763", "C", 185.0)]
    [InlineData("DC85", "D", 185.0)]
    [InlineData("B752", "E", 155.0)]
    [InlineData("AN26", "F", 125.0)]
    [InlineData("C295", "G", 100.0)]
    [InlineData("B350", "H", 60.0)]
    [InlineData("C172", "I", 30.0)]
    public void CwtFallbackLengthFt_MatchesRuledBucketLength(string type, string expectedCwt, double expectedLengthFt)
    {
        TestVnasData.EnsureInitialized();

        Assert.Equal(expectedCwt, WakeTurbulenceData.GetCwt(type));
        Assert.Equal(expectedLengthFt, AircraftLength.CwtFallbackLengthFt(type));
    }

    [Fact]
    public void ResolveFt_UnknownNonCwtType_ReturnsMediumDefault()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(FaaAircraftDatabase.Get("ZZZZ"));
        Assert.Null(WakeTurbulenceData.GetCwt("ZZZZ"));

        Assert.Equal(80.0, AircraftLength.ResolveFt("ZZZZ"));
    }
}
