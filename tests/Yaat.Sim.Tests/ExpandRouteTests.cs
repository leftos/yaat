using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Tests;

public class ExpandRouteTests
{
    [Fact]
    public void ExpandRouteForNavigation_RvSid_SkipsEntirelyAndStripsColocatedVor()
    {
        // OAK6 is a radar-vectors SID with body [OAK] (colocated VOR)
        // Route: "OAK6 OAK SYRAH" should produce only [SYRAH]
        var navData = BuildNavData(
            sids: [new Sid { Id = "OAK6", Body = { "OAK" } }],
            airports:
            [
                new Airport
                {
                    FaaId = "OAK",
                    Location = new GeoPoint { Lat = 37.7213, Lon = -122.2208 },
                    Elevation = 9,
                },
            ],
            fixes:
            [
                new Fix
                {
                    Id = "OAK",
                    Location = new GeoPoint { Lat = 37.7213, Lon = -122.2208 },
                },
                new Fix
                {
                    Id = "SYRAH",
                    Location = new GeoPoint { Lat = 38.0, Lon = -121.5 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRouteForNavigation("OAK6 OAK SYRAH", "OAK");

        Assert.Equal(["SYRAH"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_PublishedSid_EmitsBodyInOrder()
    {
        // CNDEL5 has body [LEJAY, CNDEL, PORTE]
        // Route: "CNDEL5 PORTE FFOIL" → [LEJAY, CNDEL, PORTE, FFOIL]
        // (PORTE deduped: last body fix = next route token)
        var navData = BuildNavData(
            sids: [new Sid { Id = "CNDEL5", Body = { "LEJAY", "CNDEL", "PORTE" } }],
            fixes:
            [
                new Fix
                {
                    Id = "LEJAY",
                    Location = new GeoPoint { Lat = 37.9, Lon = -122.0 },
                },
                new Fix
                {
                    Id = "CNDEL",
                    Location = new GeoPoint { Lat = 38.0, Lon = -121.9 },
                },
                new Fix
                {
                    Id = "PORTE",
                    Location = new GeoPoint { Lat = 38.1, Lon = -121.8 },
                },
                new Fix
                {
                    Id = "FFOIL",
                    Location = new GeoPoint { Lat = 38.2, Lon = -121.7 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRouteForNavigation("CNDEL5 PORTE FFOIL", null);

        Assert.Equal(["LEJAY", "CNDEL", "PORTE", "FFOIL"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_PlainRoute_PassesThrough()
    {
        var navData = BuildNavData(
            fixes:
            [
                new Fix
                {
                    Id = "OAK",
                    Location = new GeoPoint { Lat = 37.7, Lon = -122.2 },
                },
                new Fix
                {
                    Id = "SYRAH",
                    Location = new GeoPoint { Lat = 38.0, Lon = -121.5 },
                },
                new Fix
                {
                    Id = "SUNOL",
                    Location = new GeoPoint { Lat = 37.6, Lon = -121.9 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRouteForNavigation("OAK SYRAH V244 SUNOL", null);

        Assert.Equal(["OAK", "SYRAH", "V244", "SUNOL"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_SkipsNumericTokens()
    {
        var navData = BuildNavData(
            fixes:
            [
                new Fix
                {
                    Id = "SUNOL",
                    Location = new GeoPoint { Lat = 37.6, Lon = -121.9 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRouteForNavigation("SUNOL 050", null);

        Assert.Equal(["SUNOL"], result);
    }

    [Fact]
    public void ExpandRoute_Autocomplete_IncludesAllTransitionFixes()
    {
        // OAK6 with body [OAK] and transition fixes [PORTE, CNDEL]
        var navData = BuildNavData(
            sids:
            [
                new Sid
                {
                    Id = "OAK6",
                    Body = { "OAK" },
                    Transitions = { new Transition { Fixes = { "PORTE", "CNDEL" } } },
                },
            ],
            fixes:
            [
                new Fix
                {
                    Id = "OAK",
                    Location = new GeoPoint { Lat = 37.7, Lon = -122.2 },
                },
                new Fix
                {
                    Id = "SYRAH",
                    Location = new GeoPoint { Lat = 38.0, Lon = -121.5 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRoute("OAK6 SYRAH");

        // Autocomplete should get all fixes: OAK (body) + PORTE, CNDEL (transition) + SYRAH
        Assert.Contains("OAK", result);
        Assert.Contains("PORTE", result);
        Assert.Contains("CNDEL", result);
        Assert.Contains("SYRAH", result);
    }

    [Fact]
    public void ExpandRoute_Autocomplete_PreservesOrder()
    {
        // Published SID with ordered body
        var navData = BuildNavData(sids: [new Sid { Id = "CNDEL5", Body = { "LEJAY", "CNDEL", "PORTE" } }]);

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRoute("CNDEL5");

        Assert.Equal(["LEJAY", "CNDEL", "PORTE"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_EmptyRoute_ReturnsEmpty()
    {
        var navData = BuildNavData();
        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        Assert.Empty(db.ExpandRouteForNavigation("", null));
        Assert.Empty(db.ExpandRouteForNavigation("  ", null));
    }

    [Fact]
    public void ExpandRouteForNavigation_SingleFixStar_StillEmitsFix()
    {
        // STAR with single body fix — the fix is still a valid waypoint
        var navData = BuildNavData(
            stars: [new Star { Id = "BDEGA4", Body = { "BDEGA" } }],
            fixes:
            [
                new Fix
                {
                    Id = "BDEGA",
                    Location = new GeoPoint { Lat = 38.3, Lon = -123.0 },
                },
                new Fix
                {
                    Id = "SUNOL",
                    Location = new GeoPoint { Lat = 37.6, Lon = -121.9 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", customFixesBaseDir: "");

        var result = db.ExpandRouteForNavigation("SUNOL BDEGA4", null);

        Assert.Equal(["SUNOL", "BDEGA"], result);
    }

    private static NavDataSet BuildNavData(
        IEnumerable<Sid>? sids = null,
        IEnumerable<Star>? stars = null,
        IEnumerable<Airport>? airports = null,
        IEnumerable<Fix>? fixes = null
    )
    {
        var navData = new NavDataSet();

        if (sids is not null)
        {
            navData.Sids.AddRange(sids);
        }

        if (stars is not null)
        {
            navData.Stars.AddRange(stars);
        }

        if (airports is not null)
        {
            navData.Airports.AddRange(airports);
        }

        if (fixes is not null)
        {
            navData.Fixes.AddRange(fixes);
        }

        return navData;
    }
}
