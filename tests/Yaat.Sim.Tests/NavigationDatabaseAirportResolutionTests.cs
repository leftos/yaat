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
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("OAK", out var canonical);

        Assert.True(ok);
        Assert.Equal("KOAK", canonical);
    }

    [Fact]
    public void TryResolveAirport_IcaoCode_ReturnsIcao()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("KOAK", out var canonical);

        Assert.True(ok);
        Assert.Equal("KOAK", canonical);
    }

    [Fact]
    public void TryResolveAirport_Lowercase_Resolves()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("oak", out var canonical);

        Assert.True(ok);
        Assert.Equal("KOAK", canonical);
    }

    [Fact]
    public void TryResolveAirport_WithSurroundingWhitespace_Resolves()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("  KSFO  ", out var canonical);

        Assert.True(ok);
        Assert.Equal("KSFO", canonical);
    }

    [Fact]
    public void TryResolveAirport_Unknown_ReturnsFalse()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("ZZZZ", out var canonical);

        Assert.False(ok);
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void TryResolveAirport_Empty_ReturnsFalse()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("", out var canonical);

        Assert.False(ok);
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void TryResolveAirport_FixOnly_NotMatched()
    {
        // BERKS is a named intersection in the SF Bay area — it is a fix in NavData
        // but not an airport. The resolver must reject it.
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveAirport("BERKS", out var canonical);

        Assert.False(ok);
        Assert.Equal(string.Empty, canonical);
    }
}
