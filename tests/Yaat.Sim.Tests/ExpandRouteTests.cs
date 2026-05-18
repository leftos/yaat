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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

        var result = db.ExpandRoute("CNDEL5");

        Assert.Equal(["LEJAY", "CNDEL", "PORTE"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_EmptyRoute_ReturnsEmpty()
    {
        var navData = BuildNavData();
        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

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

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

        var result = db.ExpandRouteForNavigation("SUNOL BDEGA4", null);

        Assert.Equal(["SUNOL", "BDEGA"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_RvSidWithExitFixThenAirway_OmitsBogusTransitionFixes()
    {
        // Mirrors NIMI5's vNAS protobuf encoding: body=[OAK] (colocated VOR),
        // and 5 enroute "transitions" each [OAK,X] where X is CCR/PYE/SAC/SAU/SGD.
        // These transitions are vNAS adapted-route hints, not published CIFP
        // transitions (real radar-vectors SIDs have no enroute transitions).
        //
        // Filed route "NIMI5 OAK V6 SAC" must NOT expand to a route that includes
        // CCR/PYE/SAU/SGD or repeat OAK — the aircraft is vectored away from the
        // departure airport and proceeds on V6 to SAC.
        var navData = BuildNavData(
            sids:
            [
                new Sid
                {
                    Id = "NIMI5",
                    Body = { "OAK" },
                    Transitions =
                    {
                        new Transition { Fixes = { "OAK", "CCR" } },
                        new Transition { Fixes = { "OAK", "PYE" } },
                        new Transition { Fixes = { "OAK", "SAC" } },
                        new Transition { Fixes = { "OAK", "SAU" } },
                        new Transition { Fixes = { "OAK", "SGD" } },
                    },
                },
            ],
            airways:
            [
                // V6: OAK → FESIK → COLLI → PITTS → RYMAR → REJOY → COUPS → SAC
                new Airway { Id = "V6", Fixes = { "OAK", "FESIK", "COLLI", "PITTS", "RYMAR", "REJOY", "COUPS", "SAC" } },
            ],
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
                    Id = "CCR",
                    Location = new GeoPoint { Lat = 37.9897, Lon = -122.0569 },
                },
                new Fix
                {
                    Id = "PYE",
                    Location = new GeoPoint { Lat = 38.0798, Lon = -122.8678 },
                },
                new Fix
                {
                    Id = "SAC",
                    Location = new GeoPoint { Lat = 38.5129, Lon = -121.4933 },
                },
                new Fix
                {
                    Id = "SAU",
                    Location = new GeoPoint { Lat = 37.8553, Lon = -122.5228 },
                },
                new Fix
                {
                    Id = "SGD",
                    Location = new GeoPoint { Lat = 38.1794, Lon = -122.3732 },
                },
                new Fix
                {
                    Id = "FESIK",
                    Location = new GeoPoint { Lat = 37.8361, Lon = -122.1110 },
                },
                new Fix
                {
                    Id = "COLLI",
                    Location = new GeoPoint { Lat = 37.8630, Lon = -122.0835 },
                },
                new Fix
                {
                    Id = "PITTS",
                    Location = new GeoPoint { Lat = 38.0499, Lon = -121.8914 },
                },
                new Fix
                {
                    Id = "RYMAR",
                    Location = new GeoPoint { Lat = 38.1100, Lon = -121.8292 },
                },
                new Fix
                {
                    Id = "REJOY",
                    Location = new GeoPoint { Lat = 38.1664, Lon = -121.7709 },
                },
                new Fix
                {
                    Id = "COUPS",
                    Location = new GeoPoint { Lat = 38.3163, Lon = -121.6526 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

        var result = db.ExpandRouteForNavigation("NIMI5 OAK V6 SAC", "OAK");

        // The expansion must follow only the real filed route: V6 from OAK to SAC.
        // Leading colocated OAK is stripped by ExpandRouteForNavigation.
        Assert.DoesNotContain(result, n => string.Equals(n, "OAK", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, n => string.Equals(n, "CCR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, n => string.Equals(n, "PYE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, n => string.Equals(n, "SAU", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, n => string.Equals(n, "SGD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["FESIK", "COLLI", "PITTS", "RYMAR", "REJOY", "COUPS", "SAC"], result);
    }

    [Fact]
    public void ExpandRouteForNavigation_RvSidWithExitFixThenDirectFix_GivesDirectFix()
    {
        // Filed "NIMI5 OAK CCR" — pilot vectored after departure, then direct CCR.
        // Must produce [CCR] (the OAK transit fix is paperwork-only).
        var navData = BuildNavData(
            sids:
            [
                new Sid
                {
                    Id = "NIMI5",
                    Body = { "OAK" },
                    Transitions =
                    {
                        new Transition { Fixes = { "OAK", "CCR" } },
                        new Transition { Fixes = { "OAK", "PYE" } },
                    },
                },
            ],
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
                    Id = "CCR",
                    Location = new GeoPoint { Lat = 37.9897, Lon = -122.0569 },
                },
                new Fix
                {
                    Id = "PYE",
                    Location = new GeoPoint { Lat = 38.0798, Lon = -122.8678 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

        var result = db.ExpandRouteForNavigation("NIMI5 OAK CCR", "OAK");

        // No turn-back to OAK, no spurious PYE.
        Assert.DoesNotContain(result, n => string.Equals(n, "OAK", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, n => string.Equals(n, "PYE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["CCR"], result);
    }

    [Fact]
    public void ExpandRoute_Autocomplete_NoTransitionMatch_StillEmitsAllTransitions()
    {
        // Autocomplete-facing ExpandRoute keeps the "emit all transitions on
        // mismatch" behavior so the UI can suggest any transition's exit fix.
        var navData = BuildNavData(
            sids:
            [
                new Sid
                {
                    Id = "NIMI5",
                    Body = { "OAK" },
                    Transitions =
                    {
                        new Transition { Fixes = { "OAK", "CCR" } },
                        new Transition { Fixes = { "OAK", "PYE" } },
                    },
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
                    Id = "CCR",
                    Location = new GeoPoint { Lat = 37.9897, Lon = -122.0569 },
                },
                new Fix
                {
                    Id = "PYE",
                    Location = new GeoPoint { Lat = 38.0798, Lon = -122.8678 },
                },
            ]
        );

        var db = new NavigationDatabase(navData, "", artccsBaseDir: "");

        var result = db.ExpandRoute("NIMI5");

        Assert.Contains("CCR", result);
        Assert.Contains("PYE", result);
    }

    private static NavDataSet BuildNavData(
        IEnumerable<Sid>? sids = null,
        IEnumerable<Star>? stars = null,
        IEnumerable<Airport>? airports = null,
        IEnumerable<Fix>? fixes = null,
        IEnumerable<Airway>? airways = null
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

        if (airways is not null)
        {
            navData.Airways.AddRange(airways);
        }

        return navData;
    }
}
