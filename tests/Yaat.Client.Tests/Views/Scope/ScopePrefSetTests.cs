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
        Assert.Equal(set.Toolbar, loaded.Toolbar);
        Assert.Equal(set.Views, loaded.Views);
        Assert.Equal("eram", loaded.Kind);
        Assert.Equal(set.Lens, loaded.Lens);
        Assert.Equal(set.MapTools, loaded.MapTools);
        Assert.Equal(4, loaded.PinVectorMin);
        Assert.Equal(2, loaded.PinHistory);
        Assert.Equal("all", loaded.VectorOwnership);
        Assert.True(loaded.Tdm);
        Assert.True(loaded.BarHidden);
    }

    [Fact]
    public void PinVectorMin_HalfMinute_RoundTrips()
    {
        ScopePrefSet set = FullSet() with { PinVectorMin = 0.5 };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions));
        ScopePrefSet loaded = RoundTrip(set);

        Assert.Equal(0.5, document.RootElement.GetProperty("pinVectorMin").GetDouble());
        Assert.Equal(0.5, loaded.PinVectorMin);
    }

    [Fact]
    public void TheCaRing_IsSavedAsEramCaRing_AndRoundTrips()
    {
        ScopePrefSet set = FullSet() with { Db = new ScopePrefDb(true, false, true, true, true, EramCaRing: false, 14) };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions));

        Assert.False(document.RootElement.GetProperty("db").GetProperty("eramCaRing").GetBoolean());
        Assert.False(RoundTrip(set).Db.EramCaRing);
        Assert.True(RoundTrip(set with { Db = set.Db with { EramCaRing = true } }).Db.EramCaRing);
    }

    [Fact]
    public void TheAtpaConeToggles_AreSaved_AndRoundTrip()
    {
        ScopePrefSet set = FullSet() with { Db = new ScopePrefDb(true, false, true, AtpaAlertCones: false, AtpaMonitorCones: true, true, 14) };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions));

        JsonElement db = document.RootElement.GetProperty("db");
        Assert.False(db.GetProperty("atpaAlertCones").GetBoolean());
        Assert.True(db.GetProperty("atpaMonitorCones").GetBoolean());
        Assert.Equal(set.Db, RoundTrip(set).Db);
        ScopePrefSet flipped = set with { Db = set.Db with { AtpaAlertCones = true, AtpaMonitorCones = false } };
        Assert.Equal(flipped.Db, RoundTrip(flipped).Db);
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
                "toolbar",
                "views",
                "kind",
                "pinOthersLevel",
                "pinVectorMin",
                "pinHistory",
                "pinRings",
                "vectorOwnership",
                "tdm",
                "barHidden",
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
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
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
    public void AnOldFilesTearoffs_LoadWithTheRestIntact_AndAResaveDropsThem()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "rangeNm": 37.5,
              "centerLat": 37.5,
              "centerLon": -122.5,
              "templateId": "eram",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
              "maps": [3, 15],
              "openViews": ["ssa"],
              "tearoffs": [ { "button": "range", "corner": "tl", "dx": 10, "dy": 20 } ],
              "toolbar": ["range", "pref"],
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null },
              "futureThing": 3
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(loaded, ScopePrefSet.JsonOptions));

        Assert.Equal("SR-DAY", loaded.Name);
        Assert.Equal(37.5, loaded.RangeNm);
        Assert.Equal("eram", loaded.TemplateId);
        Assert.Equal([3, 15], loaded.Maps);
        Assert.Equal(["ssa"], loaded.OpenViews);
        Assert.Equal(["range", "pref"], loaded.Toolbar);
        Assert.Equal("stars", loaded.Kind);
        Assert.False(document.RootElement.TryGetProperty("tearoffs", out _));
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
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.Empty(loaded.Maps);
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
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
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
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.False(loaded.Tdm);
    }

    [Fact]
    public void BarHidden_IsSavedAsBarHidden_AndRoundTrips()
    {
        ScopePrefSet set = FullSet() with { BarHidden = true };

        string json = JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions);

        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("barHidden").GetBoolean());
        Assert.True(RoundTrip(set).BarHidden);
        Assert.False(RoundTrip(set with { BarHidden = false }).BarHidden);
    }

    [Fact]
    public void MissingBarHidden_LoadsAsShown()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
              "kind": "stars",
              "lens": { "enabled": false, "path": false, "queue": false, "checkIn": false },
              "mapTools": { "mva": false, "lock": false, "rangeRing": null, "weather": null, "fixesAndMarkers": null }
            }
            """;

        ScopePrefSet loaded = JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!;

        Assert.False(loaded.BarHidden);
    }

    [Fact]
    public void MapToolsWithNullRingAndWeather_LoadTheDefaults()
    {
        const string json = """
            {
              "name": "SR-DAY",
              "templateId": "stars",
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
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
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
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
              "db": { "line4": true, "hoverDwell": false, "atpa": true, "atpaAlertCones": true, "atpaMonitorCones": true,
                      "eramCaRing": true, "charSize": 14 },
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
            Db = new ScopePrefDb(true, false, true, true, true, false, 14),
            Maps = [3, 15, 40],
            OpenViews = ["ssa", "ca"],
            ViewPlaces = [new ScopePrefView("ssa", null, 8, 8), new ScopePrefView("ca", "br", 410, 260)],
            Toolbar = ["range", "map15", "pref"],
            Views = ["ssa", "ca"],
            Kind = "eram",
            Lens = new ScopePrefLens(true, false, false, false),
            MapTools = new ScopePrefMapTools(false, false, null, null, null),
            PinVectorMin = 4,
            PinHistory = 2,
            VectorOwnership = "all",
            Tdm = true,
            BarHidden = true,
        };

    private static ScopePrefSet RoundTrip(ScopePrefSet set) =>
        JsonSerializer.Deserialize<ScopePrefSet>(JsonSerializer.Serialize(set, ScopePrefSet.JsonOptions), ScopePrefSet.JsonOptions)!;
}
