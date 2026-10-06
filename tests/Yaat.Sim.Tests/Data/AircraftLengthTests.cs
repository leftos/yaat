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

    /// <summary>
    /// A type the FAA database lacks with a known CWT bucket: <c>SimplePushbackDistanceNm</c> takes its length
    /// from <see cref="AircraftLength.ResolveFt"/> (the ruled CWT bucket length), not its own letter table.
    /// </summary>
    [Theory]
    [InlineData("C5M", "B", 220.0)]
    [InlineData("AN26", "F", 125.0)]
    [InlineData("C295", "G", 100.0)]
    public void SimplePushbackDistanceNm_UnknownType_UsesResolveFt(string type, string expectedCwt, double expectedLengthFt)
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(FaaAircraftDatabase.Get(type));
        Assert.Equal(expectedCwt, WakeTurbulenceData.GetCwt(type));

        Assert.Equal(expectedLengthFt / 6076.12, CategoryPerformance.SimplePushbackDistanceNm(type), 6);
    }

    /// <summary>An I-bucket light type the FAA database lacks floors at the prior 0.015 nm baseline.</summary>
    [Fact]
    public void SimplePushbackDistanceNm_UnknownLightType_FloorsAtBaseline()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(FaaAircraftDatabase.Get("DA62"));
        Assert.Equal("I", WakeTurbulenceData.GetCwt("DA62"));

        Assert.Equal(0.015, CategoryPerformance.SimplePushbackDistanceNm("DA62"), 6);
    }

    /// <summary>A type the FAA database carries pushes back by its FAA length, not a CWT table row.</summary>
    [Fact]
    public void SimplePushbackDistanceNm_B738_UsesItsFaaLength()
    {
        TestVnasData.EnsureInitialized();
        Assert.Equal(129.5, AircraftLength.ResolveFt("B738"));

        Assert.Equal(129.5 / 6076.12, CategoryPerformance.SimplePushbackDistanceNm("B738"), 6);
    }
}
