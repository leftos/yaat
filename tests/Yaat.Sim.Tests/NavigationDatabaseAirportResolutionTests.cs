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
        // A K-strip only handles CONUS "K" prefixes and would return these unchanged. Resolving
        // through the published FAA id is the difference between displaying "ANC" and displaying
        // "PANC" in the STARS scratchpad slot; NormalizeAirport resolves through it too.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        bool ok = navDb.TryResolveFaaId(icao, out string? faaId);

        Assert.True(ok);
        Assert.Equal(expectedFaa, faaId);
        Assert.Equal(expectedFaa, NavigationDatabase.NormalizeAirport(icao));
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

    [Theory]
    [InlineData("PHNL", "I08L")]
    [InlineData("PHOG", "I02-Y")]
    [InlineData("PANC", "I07L")]
    [InlineData("PGUM", "I06L")]
    public void GetApproaches_PacificAndAlaskaAirports_FindTheirCifpApproaches(string icao, string approachId)
    {
        // FAACIFP18 files Hawaii and Guam under area code PAC ("SPACP") and Alaska under CAN ("SCANP"),
        // not USA ("SUSAP"), so a USA-only record filter found no approach at any of them.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        Assert.Contains(navDb.GetApproaches(icao), a => a.ApproachId == approachId);
    }

    [Fact]
    public void GetSids_PacificAirport_LoadsItsCifpDepartures()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        Assert.Contains(navDb.GetSids("PHNL"), sid => sid.ProcedureId == "HNL2");
    }

    [Fact]
    public void GetApproaches_ByFaaId_HNL_FindsPhnlApproaches()
    {
        // The CIFP files Honolulu as PHNL; a K-prefix fallback asked it for "KHNL".
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        Assert.Contains(navDb.GetApproaches("HNL"), a => a.ApproachId == "I08L");
        Assert.NotNull(navDb.GetApproach("HNL", "I08L"));
    }

    [Fact]
    public void GetApproach_ThreeCharacterLid_FindsItsCifpApproach()
    {
        // YAAT-479 / YAAT-1664: the CIFP files 12J under its bare LID, so a "K12J" lookup found no approach.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        Assert.NotNull(navDb.GetApproach("12J", "D30"));
    }

    [Theory]
    [InlineData("PHNL", "HNL", true)]
    [InlineData("PHOG", "OGG", true)]
    [InlineData("PANC", "ANC", true)]
    [InlineData("PHNL", "OGG", false)]
    [InlineData("KOAK", "OAK", true)]
    [InlineData("koak", "OAK", true)]
    [InlineData("KOAK", "SFO", false)]
    public void AirportIdsMatch_IcaoAndFaaIds_OfAPacificAirport_Match(string a, string b, bool expected)
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        Assert.Equal(expected, NavigationDatabase.AirportIdsMatch(a, b));
    }
}
