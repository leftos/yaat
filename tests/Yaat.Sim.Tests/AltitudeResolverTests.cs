using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class AltitudeResolverTests
{
    private static readonly NavigationDatabase Fixes = TestNavDbFactory.WithElevations(("KOAK", 9.0), ("OAK", 9.0), ("KSFO", 13.0));

    public AltitudeResolverTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- Numeric formats (unchanged behavior) ---

    [Theory]
    [InlineData("050", 5000)]
    [InlineData("100", 10000)]
    [InlineData("5000", 5000)]
    [InlineData("1500", 1500)]
    [InlineData("1", 100)]
    public void Numeric_ReturnsExpected(string arg, int expected)
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Equal(expected, AltitudeResolver.Resolve(arg));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Numeric_InvalidReturnsNull(string arg)
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve(arg));
    }

    // --- New AGL format with '+' separator ---

    [Fact]
    public void Agl_IcaoCode_PlusFormat()
    {
        // KOAK elevation 9 ft, 010 → 1000 AGL → 1009 MSL
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Equal(1009, AltitudeResolver.Resolve("KOAK+010"));
    }

    [Fact]
    public void Agl_FaaCode_PlusFormat()
    {
        // OAK elevation 9 ft, 050 → 5000 AGL → 5009 MSL
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Equal(5009, AltitudeResolver.Resolve("OAK+050"));
    }

    [Fact]
    public void Agl_AbsoluteAglValue()
    {
        // KOAK elevation 9 ft, 1500 → 1500 AGL → 1509 MSL
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Equal(1509, AltitudeResolver.Resolve("KOAK+1500"));
    }

    // --- Old format rejected ---

    [Fact]
    public void OldFormat_NoPlus_ReturnsNull()
    {
        // KOAK010 without '+' should NOT be parsed as AGL
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("KOAK010"));
    }

    [Fact]
    public void OldFormat_FaaNoPlus_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("OAK050"));
    }

    // --- Edge cases ---

    [Fact]
    public void Agl_NullArg_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve(null));
    }

    [Fact]
    public void Agl_UnknownAirport_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("ZZZZ+010"));
    }

    [Fact]
    public void Agl_NoDigitsAfterPlus_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("KOAK+"));
    }

    [Fact]
    public void Agl_PlusOnly_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("+"));
    }

    [Fact]
    public void Agl_NothingBeforePlus_ParsesAsNumeric()
    {
        // "+010" is valid for int.TryParse (sign prefix) → 10 → 1000 ft
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Equal(1000, AltitudeResolver.Resolve("+010"));
    }

    [Fact]
    public void Agl_ZeroAgl_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("KOAK+0"));
    }

    [Fact]
    public void Agl_NonNumericAfterPlus_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(Fixes);
        Assert.Null(AltitudeResolver.Resolve("KOAK+ABC"));
    }
}
