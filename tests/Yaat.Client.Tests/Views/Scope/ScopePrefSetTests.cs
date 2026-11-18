using System.Text.Json;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The pref-set records' JSON: the property names the files carry, the round trip through the store's options, the
/// passthrough of a field this build does not know, and the empty collections a file without them gives.
/// </summary>
public class ScopePrefSetTests
{
    [Fact]
    public void FullSet_RoundTripsWithEveryFieldEqual()
    {
        ScopePrefSet set = FullSet();

        ScopePrefSet loaded = RoundTrip(set);

        Assert.Equal(set.Name, loaded.Name);
        Assert.Equal(set.RangeNm, loaded.RangeNm);
        Assert.Equal(set.CenterLat, loaded.CenterLat);
        Assert.Equal(set.CenterLon, loaded.CenterLon);
        Assert.Equal(set.TemplateId, loaded.TemplateId);
        Assert.Equal(set.Brightness.Count, loaded.Brightness.Count);
        Assert.All(set.Brightness, pair => Assert.Equal(pair.Value, loaded.Brightness[pair.Key]));
        Assert.Equal(set.Db, loaded.Db);
        Assert.Equal(set.Maps, loaded.Maps);
        Assert.Equal(set.OpenViews, loaded.OpenViews);
        Assert.Equal(set.ViewPlaces, loaded.ViewPlaces);
        Assert.Equal(set.Tearoffs, loaded.Tearoffs);
        Assert.Equal(set.Toolbar, loaded.Toolbar);
        Assert.Equal(set.Views, loaded.Views);
        Assert.Equal("eram", loaded.Kind);
        Assert.Equal(set.Lens, loaded.Lens);
        Assert.Equal(set.MapTools, loaded.MapTools);
        Assert.Equal(4, loaded.PinVectorMin);
        Assert.Equal(2, loaded.PinHistory);
        Assert.Equal("all", loaded.VectorOwnership);
        Assert.True(loaded.Tdm);
    }

    [Fact]
    public void SerializedSet_UsesTheFiledPropertyNames()
    {
        string json = JsonSerializer.Serialize(FullSet(), ScopePrefSet.JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            [
                "name",
                "rangeNm",
                "centerLat",
                "centerLon",
                "templateId",
                "brightness",
                "db",
                "maps",
                "openViews",
                "viewPlaces",
                "tearoffs",
                "toolbar",
                "views",
                "kind",
                "pinOthersLevel",
                "pinVectorMin",
                "pinHistory",
                "pinRings",
                "vectorOwnership",
                "tdm",
                "lens",
                "mapTools",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name)
        );
    }

    [Fact]
    public void UnknownField_IsKeptOnResave()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "rangeNm": 37.5,
              "centerLat": 37.5,
              "centerLon": -122.5,
              "templateId": "eram",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null },
              "futureThing": 3
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        string resaved = JsonSerializer.Serialize(loaded, ScopePrefSet.JsonOptions);
        using var document = JsonDocument.Parse(resaved);

        Assert.Equal(3, document.RootElement.GetProperty("futureThing").GetInt32());
    }

    [Fact]
    public void MissingCollections_LoadEmpty()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "rangeNm": 37.5,
              "centerLat": 37.5,
              "centerLon": -122.5,
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.Empty(loaded.Maps);
        Assert.Empty(loaded.Tearoffs);
        Assert.Empty(loaded.Brightness);
        Assert.Empty(loaded.OpenViews);
        Assert.Empty(loaded.ViewPlaces);
        Assert.Empty(loaded.Toolbar);
        Assert.Empty(loaded.Views);
    }

    [Fact]
    public void MissingVectorOwnership_LoadsAsOwn()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.Equal("own", loaded.VectorOwnership);
    }

    [Fact]
    public void MissingTdm_LoadsAsOff()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.False(loaded.Tdm);
    }

    [Fact]
    public void MapToolsWithNullRingAndWeather_LoadTheDefaults()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": true, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.True(loaded.MapTools.Mva);
        Assert.False(loaded.MapTools.Lock);
        Assert.Null(loaded.MapTools.RangeRing);
        Assert.Null(loaded.MapTools.Weather);
    }

    [Fact]
    public void MapToolsWithNullFixesAndMarkers_LoadsWithNone()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": true, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.Null(loaded.MapTools.FixesAndMarkers);
    }

    [Fact]
    public void FixesAndMarkers_RoundTrip()
    {
        ScopePrefSet set = FullSet() with
        {
            MapTools = new ScopePrefMapTools(false, false, null, null, new ScopePrefFixesAndMarkers(true, ["SUNOL", "OAK090010"])),
        };

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(
            JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions),
            ScopePrefSet.JsonOptions
        )!;

        ScopePrefFixesAndMarkers? fixesAndMarkers = loaded.MapTools.FixesAndMarkers;
        Assert.NotNull(fixesAndMarkers);
        Assert.True(fixesAndMarkers.ShowFixes);
        Assert.Equal(["SUNOL", "OAK090010"], fixesAndMarkers.Markers);
    }

    [Fact]
    public void TheDeconflictionMode_RoundTrips()
    {
        ScopePrefSet set = FullSet() with
        {
            MapTools = new ScopePrefMapTools(false, false, null, null, null) { Deconflict = DatablockDeconflictMode.CompassSnap },
        };

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(
            JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions),
            ScopePrefSet.JsonOptions
        )!;

        Assert.Equal(DatablockDeconflictMode.CompassSnap, loaded.MapTools.Deconflict);
    }

    [Fact]
    public void MapToolsWithoutTheDeconflictionMode_LoadsWithNone()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": true, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.Null(loaded.MapTools.Deconflict);
    }

    private static ScopePrefSet FullSet() =>
        new()
        {
            Name = "SR-DAY",
            RangeNm = 37.5,
            CenterLat = 37.6173,
            CenterLon = -122.375,
            TemplateId = "eram",
            Brightness = new Dictionary<string, int>
            {
                ["mapa"] = 100,
                ["line4"] = -15,
                ["dwell"] = 20,
            },
            Db = new ScopePrefDb(true, false, true, 14),
            Maps = [3, 15, 40],
            OpenViews = ["ssa", "ca"],
            ViewPlaces = [new ScopePrefView("ssa", null, 8, 8), new ScopePrefView("ca", "br", 410, 260)],
            Tearoffs = [new ScopePrefTearoff("range", "tl", 10, 20), new ScopePrefTearoff("map15", "tr", -30, 40)],
            Toolbar = ["range", "map15", "pref"],
            Views = ["ssa", "ca"],
            Kind = "eram",
            Lens = new ScopePrefLens(true, false, false, false),
            MapTools = new ScopePrefMapTools(false, false, null, null, null),
            PinVectorMin = 4,
            PinHistory = 2,
            VectorOwnership = "all",
            Tdm = true,
        };

    private static ScopePrefSet RoundTrip(ScopePrefSet set) =>
        JsonSerializer.Deserialize<ScopePrefSet>(JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions), ScopePrefSet.JsonOptions)!;
}
