using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for GitHub issue #302: "Load Live Weather" appeared to do nothing.
///
/// The command resolves the ARTCC's underlying airports before fetching METARs, and bails out
/// early with a status-bar message when that list is empty. The reporter's client log shows
/// "Resolved 0 airports for ZOA" — so every live weather attempt short-circuited.
///
/// In a real vNAS data-api config, <c>starsConfiguration</c> hangs off nodes in the
/// <c>facility</c> / <c>childFacilities</c> tree, never off the document root, so a root-level
/// lookup finds nothing for any ARTCC.
/// </summary>
public class ArtccAirportResolverTests
{
    private const string ZoaConfigPath = "TestData/zoa.json";

    [Fact]
    public void ExtractUnderlyingAirports_RealZoaConfig_FindsTraconAirports()
    {
        if (!File.Exists(ZoaConfigPath))
        {
            return;
        }

        var airports = ArtccAirportResolver.ExtractUnderlyingAirports(File.ReadAllText(ZoaConfigPath));

        Assert.NotEmpty(airports);

        // NCT (the ZOA TRACON) lists these; they are the airports the reporter's scenario used.
        Assert.Contains("OAK", airports);
        Assert.Contains("SFO", airports);
        Assert.Contains("SJC", airports);
    }

    [Fact]
    public void ExtractUnderlyingAirports_RealZoaConfig_DeduplicatesAcrossFacilities()
    {
        if (!File.Exists(ZoaConfigPath))
        {
            return;
        }

        var airports = ArtccAirportResolver.ExtractUnderlyingAirports(File.ReadAllText(ZoaConfigPath));

        // OAK/SFO appear under several facilities and repeatedly across NCT's 32 areas.
        Assert.Equal(airports.Count, airports.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ExtractUnderlyingAirports_ConfigWithoutStarsConfiguration_ReturnsEmpty()
    {
        var airports = ArtccAirportResolver.ExtractUnderlyingAirports("""{"id":"ZZZ","facility":{"id":"ZZZ"}}""");

        Assert.Empty(airports);
    }
}
