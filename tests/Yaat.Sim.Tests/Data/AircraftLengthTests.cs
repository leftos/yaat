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

        Assert.Equal(250.0, AircraftLength.ResolveFt("A225"));
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
