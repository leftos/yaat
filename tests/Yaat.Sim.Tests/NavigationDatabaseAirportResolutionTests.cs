using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="NavigationDatabase.TryResolveAirport"/> — the shared
/// resolver that accepts both FAA ("OAK") and ICAO ("KOAK") airport identifiers
/// and returns the canonical ICAO form for storage in flight-plan fields.
/// </summary>
[Collection("NavDbMutator")]
public class NavigationDatabaseAirportResolutionTests
{
    public NavigationDatabaseAirportResolutionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TryResolveAirport_FaaCode_ReturnsIcao()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("OAK", out string? canonical);

        Assert.True(ok);
        Assert.Equal("KOAK", canonical);
    }

    [Fact]
    public void TryResolveAirport_IcaoCode_ReturnsIcao()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("KOAK", out string? canonical);

        Assert.True(ok);
        Assert.Equal("KOAK", canonical);
    }

    [Fact]
    public void TryResolveAirport_Lowercase_Resolves()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("oak", out string? canonical);

        Assert.True(ok);
        Assert.Equal("KOAK", canonical);
    }

    [Fact]
    public void TryResolveAirport_WithSurroundingWhitespace_Resolves()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("  KSFO  ", out string? canonical);

        Assert.True(ok);
        Assert.Equal("KSFO", canonical);
    }

    [Fact]
    public void TryResolveAirport_Unknown_ReturnsFalse()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("ZZZZ", out string? canonical);

        Assert.False(ok);
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void TryResolveAirport_Empty_ReturnsFalse()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("", out string? canonical);

        Assert.False(ok);
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void TryResolveAirport_FixOnly_NotMatched()
    {
        // BERKS is a named intersection in the SF Bay area — it is a fix in NavData
        // but not an airport. The resolver must reject it.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("BERKS", out string? canonical);

        Assert.False(ok);
        Assert.Equal(string.Empty, canonical);
    }

    [Theory]
    [InlineData("KOAK", "OAK")]
    [InlineData("OAK", "OAK")]
    [InlineData("koak", "OAK")]
    [InlineData("  KSFO  ", "SFO")]
    public void TryResolveFaaId_ConusForms_ReturnFaaId(string input, string expected)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveFaaId(input, out string? faaId);

        Assert.True(ok);
        Assert.Equal(expected, faaId);
    }

    [Theory]
    [InlineData("PANC", "ANC")]
    [InlineData("PHNL", "HNL")]
    [InlineData("TJSJ", "SJU")]
    public void TryResolveFaaId_NonConus_ResolvesWhereKStripFails(string icao, string expectedFaa)
    {
        // The repo-wide NormalizeAirport K-strip only handles CONUS "K" prefixes and would
        // return these unchanged. Resolving through the published FAA id is the difference
        // between displaying "ANC" and displaying "PANC" in the STARS scratchpad slot.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveFaaId(icao, out string? faaId);

        Assert.True(ok);
        Assert.Equal(expectedFaa, faaId);
        Assert.Equal(icao, NavigationDatabase.NormalizeAirport(icao));
    }

    [Theory]
    [InlineData("ZZZZ")]
    [InlineData("")]
    [InlineData("BERKS")]
    public void TryResolveFaaId_UnknownOrEmpty_ReturnsFalse(string input)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveFaaId(input, out string? faaId);

        Assert.False(ok);
        Assert.Equal(string.Empty, faaId);
    }

    [Fact]
    public void TryResolveFaaId_ForeignAirportWithNoFaaId_ReturnsFalse()
    {
        // Heathrow publishes no FAA id. Reporting failure (rather than substituting the ICAO
        // form) is what lets the display path fall back to the identifier as filed.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        // Guard: only meaningful if NavData actually carries the airport.
        if (!navDb.TryResolveAirport("EGLL", out _))
        {
            return;
        }

        bool ok = navDb.TryResolveFaaId("EGLL", out string? faaId);

        Assert.False(ok);
        Assert.Equal(string.Empty, faaId);
    }
}
