using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// The v3 → v4 preferences migration: each saved layout's Radar window rows (and the top-level window-geometry store's)
// are rewritten once into Scope window rows at the same position and size, and the Radar rows are dropped. Each test
// starts on its own fresh preferences file.
public class UserPreferencesRadarMigrationTests
{
    private static string PreferencesPath => UserPreferences.FilePath;

    private static JsonObject Geometry(int x, int y, double width, double height) =>
        new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
        };

    private static JsonArray Extras(params (int Ordinal, string Airport)[] views)
    {
        var array = new JsonArray();
        foreach ((int ordinal, string airport) in views)
        {
            array.Add(new JsonObject { ["ordinal"] = ordinal, ["airportId"] = airport });
        }

        return array;
    }

    private static void WriteV3File(JsonObject layout, Action<JsonObject>? extra = null)
    {
        var file = new JsonObject
        {
            ["preferencesVersion"] = 3,
            ["layouts"] = new JsonArray { layout },
        };
        extra?.Invoke(file);
        File.WriteAllText(PreferencesPath, file.ToJsonString());
    }

    private static void AssertGeometry(SavedWindowGeometry? actual, int x, int y, double width, double height)
    {
        Assert.NotNull(actual);
        Assert.Equal(x, actual.X);
        Assert.Equal(y, actual.Y);
        Assert.Equal(width, actual.Width);
        Assert.Equal(height, actual.Height);
    }

    private static void AssertNoRadarRows(SavedLayout layout)
    {
        Assert.DoesNotContain(layout.WindowGeometries.Keys, k => k.StartsWith("RadarView", StringComparison.Ordinal));
        Assert.Null(layout.ExtraRadarViews);
        Assert.Null(layout.IsRadarViewPoppedOut);
    }

    private static JsonObject WrittenFile() => JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();

    [Fact]
    public void PoppedOutRadar_BecomesPoppedOutScope()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-Popped",
                ["isRadarViewPoppedOut"] = true,
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(10, 20, 900, 600) },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-Popped")!;

        Assert.True(layout.IsScopePoppedOut);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView"), 10, 20, 900, 600);
        AssertNoRadarRows(layout);
    }

    [Fact]
    public void ExtraRadarN_BecomesExtraScope()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-Extras",
                ["extraRadarViews"] = Extras((2, "KOAK"), (3, "KSFO")),
                ["windowGeometries"] = new JsonObject { ["RadarView#2"] = Geometry(1, 2, 300, 200), ["RadarView#3"] = Geometry(3, 4, 500, 400) },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-Extras")!;

        Assert.Equal([new SavedExtraView(2, ""), new SavedExtraView(3, "")], layout.ExtraScopeViews);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView#2"), 1, 2, 300, 200);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView#3"), 3, 4, 500, 400);
        AssertNoRadarRows(layout);
    }

    [Fact]
    public void GeometryOnly_RadarKeysMoveToScopeKeys()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-GeometryOnly",
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(10, 20, 900, 600), ["RadarView#4"] = Geometry(30, 40, 700, 500) },
            },
            file => file["radarViewWindowGeometry"] = Geometry(50, 60, 1000, 800)
        );

        var prefs = new UserPreferences();
        SavedLayout layout = prefs.GetLayout("WPT-GeometryOnly")!;

        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView"), 10, 20, 900, 600);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView#4"), 30, 40, 700, 500);
        Assert.Null(layout.ExtraScopeViews);
        Assert.Null(layout.IsScopePoppedOut);
        AssertNoRadarRows(layout);
        AssertGeometry(prefs.GetWindowGeometry("ScopeView"), 50, 60, 1000, 800);
        Assert.False(WrittenFile().ContainsKey("radarViewWindowGeometry"));
    }

    [Fact]
    public void LayoutAlreadyHoldingScopes_KeepsItsOwn()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-Collide",
                ["isScopePoppedOut"] = false,
                ["extraScopeViews"] = Extras((2, "")),
                ["extraRadarViews"] = Extras((2, "KOAK"), (3, "KSFO")),
                ["windowGeometries"] = new JsonObject
                {
                    ["ScopeView"] = Geometry(1, 1, 111, 111),
                    ["ScopeView#2"] = Geometry(2, 2, 222, 222),
                    ["RadarView"] = Geometry(9, 9, 999, 999),
                    ["RadarView#2"] = Geometry(3, 3, 333, 333),
                    ["RadarView#3"] = Geometry(4, 4, 444, 444),
                },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-Collide")!;

        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView"), 1, 1, 111, 111);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView#2"), 2, 2, 222, 222);
        Assert.Equal([new SavedExtraView(2, ""), new SavedExtraView(3, ""), new SavedExtraView(4, "")], layout.ExtraScopeViews);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView#3"), 3, 3, 333, 333);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView#4"), 4, 4, 444, 444);
        Assert.False(layout.IsScopePoppedOut);
        AssertNoRadarRows(layout);
        Assert.Equal(["ScopeView", "ScopeView#2", "ScopeView#3", "ScopeView#4"], layout.WindowGeometries.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RadarNotPoppedOut_LeavesScopePopOutNull()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-Docked",
                ["isRadarViewPoppedOut"] = false,
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(10, 20, 900, 600) },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-Docked")!;

        Assert.Null(layout.IsScopePoppedOut);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView"), 10, 20, 900, 600);
        AssertNoRadarRows(layout);
    }

    [Fact]
    public void SecondLoad_IsANoOp()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-Twice",
                ["isRadarViewPoppedOut"] = true,
                ["extraRadarViews"] = Extras((2, "KOAK")),
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(10, 20, 900, 600), ["RadarView#2"] = Geometry(1, 2, 300, 200) },
            }
        );

        var first = new UserPreferences();
        SavedLayout migrated = first.GetLayout("WPT-Twice")!;
        AssertNoRadarRows(migrated);
        string firstLayouts = JsonSerializer.Serialize(first.Layouts);

        JsonNode saved = JsonNode.Parse(File.ReadAllText(PreferencesPath))!;
        Assert.Equal(UserPreferences.CurrentPreferencesVersion, saved["preferencesVersion"]!.GetValue<int>());
        Assert.Null(saved["radarViewWindowGeometry"]);
        Assert.DoesNotContain(GeometryKeys(saved["windowGeometries"]), k => k.StartsWith("RadarView", StringComparison.Ordinal));
        foreach (JsonNode? layout in saved["layouts"]!.AsArray())
        {
            Assert.DoesNotContain(GeometryKeys(layout!["windowGeometries"]), k => k.StartsWith("RadarView", StringComparison.Ordinal));
        }

        string secondLayouts = JsonSerializer.Serialize(new UserPreferences().Layouts);

        Assert.Equal(firstLayouts, secondLayouts);
        string before = JsonSerializer.Serialize(migrated);
        Assert.False(UserPreferences.MigrateRadarWindows(migrated));
        Assert.Equal(before, JsonSerializer.Serialize(migrated));
    }

    private static IEnumerable<string> GeometryKeys(JsonNode? geometries) => (geometries as JsonObject)?.Select(p => p.Key) ?? [];

    [Fact]
    public void TopLevelStore_RadarRowsMoveToScopeRows_AndAnExistingScopeRowWins()
    {
        WriteV3File(
            new JsonObject { ["name"] = "WPT-Plain" },
            file =>
            {
                file["radarViewWindowGeometry"] = Geometry(9, 9, 999, 999);
                file["windowGeometries"] = new JsonObject { ["ScopeView"] = Geometry(1, 1, 111, 111), ["RadarView#3"] = Geometry(3, 3, 333, 333) };
                file["isRadarViewPoppedOut"] = true;
                file["isScopePoppedOut"] = false;
                file["extraRadarViews"] = Extras((2, "KOAK"));
            }
        );

        var prefs = new UserPreferences();

        AssertGeometry(prefs.GetWindowGeometry("ScopeView"), 1, 1, 111, 111);
        AssertGeometry(prefs.GetWindowGeometry("ScopeView#3"), 3, 3, 333, 333);
        Assert.Null(prefs.GetWindowGeometry("RadarView#3"));
        Assert.True(prefs.IsScopePoppedOut);
        JsonObject written = WrittenFile();
        Assert.False(written.ContainsKey("radarViewWindowGeometry"));
        Assert.False(written.ContainsKey("isRadarViewPoppedOut"));
        Assert.False(written.ContainsKey("extraRadarViews"));
    }

    [Fact]
    public void PoppedOutRadar_PopsOutAScopeTheLayoutHadDocked()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-DockedScope",
                ["isRadarViewPoppedOut"] = true,
                ["isScopePoppedOut"] = false,
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-DockedScope")!;

        Assert.True(layout.IsScopePoppedOut);
        AssertNoRadarRows(layout);
    }

    [Fact]
    public void ListedRadarWindow_TakesItsScopeKeyBeforeAStrayRadarRow()
    {
        // The stray "RadarView#3" (3 is not listed) comes first in the file; the listed Radar #2 is given ordinal 3,
        // since a Scope window already holds 2, and its own geometry must land on "ScopeView#3".
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-Stray",
                ["extraScopeViews"] = Extras((2, "")),
                ["extraRadarViews"] = Extras((2, "KOAK")),
                ["windowGeometries"] = new JsonObject
                {
                    ["RadarView#3"] = Geometry(7, 7, 777, 777),
                    ["ScopeView#2"] = Geometry(2, 2, 222, 222),
                    ["RadarView#2"] = Geometry(3, 3, 333, 333),
                },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-Stray")!;

        Assert.Equal([new SavedExtraView(2, ""), new SavedExtraView(3, "")], layout.ExtraScopeViews);
        AssertGeometry(layout.WindowGeometries["ScopeView#2"], 2, 2, 222, 222);
        AssertGeometry(layout.WindowGeometries["ScopeView#3"], 3, 3, 333, 333);
        AssertNoRadarRows(layout);
    }

    [Fact]
    public void StrayRadarRow_WhoseScopeKeyExists_IsDropped()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-StrayTaken",
                ["windowGeometries"] = new JsonObject { ["ScopeView#5"] = Geometry(5, 5, 555, 555), ["RadarView#5"] = Geometry(6, 6, 666, 666) },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-StrayTaken")!;

        Assert.Equal(["ScopeView#5"], layout.WindowGeometries.Keys);
        AssertGeometry(layout.WindowGeometries["ScopeView#5"], 5, 5, 555, 555);
        Assert.Null(layout.ExtraScopeViews);
    }

    [Fact]
    public void NonNumericRadarKey_MovesVerbatim()
    {
        var layout = new SavedLayout
        {
            Name = "X",
            WindowGeometries = new() { ["RadarView#x"] = new SavedWindowGeometry { Width = 400 } },
        };

        Assert.True(UserPreferences.MigrateRadarWindows(layout));

        Assert.Equal(["ScopeView#x"], layout.WindowGeometries.Keys);
        Assert.Equal(400, layout.WindowGeometries["ScopeView#x"].Width);
    }

    [Fact]
    public void RadarOrdinalBelowTwo_KeepsItsNumber()
    {
        var layout = new SavedLayout { Name = "X", ExtraRadarViews = [new SavedExtraView(1, "KOAK")] };

        Assert.True(UserPreferences.MigrateRadarWindows(layout));

        Assert.Equal([new SavedExtraView(1, "")], layout.ExtraScopeViews);
    }

    [Fact]
    public void DuplicateRadarOrdinal_GivesOneScopeEntry()
    {
        var layout = new SavedLayout { Name = "X", ExtraRadarViews = [new SavedExtraView(2, "KOAK"), new SavedExtraView(2, "KSFO")] };

        Assert.True(UserPreferences.MigrateRadarWindows(layout));

        Assert.Equal([new SavedExtraView(2, "")], layout.ExtraScopeViews);
        Assert.Null(layout.ExtraRadarViews);
    }

    [Fact]
    public void MigrateRadarWindows_ReturnsWhetherTheLayoutHeldARadarRow()
    {
        var withRadar = new SavedLayout
        {
            Name = "R",
            WindowGeometries = new() { ["RadarView"] = new SavedWindowGeometry() },
        };
        var withoutRadar = new SavedLayout
        {
            Name = "S",
            IsScopePoppedOut = true,
            ExtraScopeViews = [new SavedExtraView(2, "")],
            WindowGeometries = new() { ["ScopeView"] = new SavedWindowGeometry() },
        };

        Assert.True(UserPreferences.MigrateRadarWindows(withRadar));
        Assert.False(UserPreferences.MigrateRadarWindows(withoutRadar));
        Assert.Equal([new SavedExtraView(2, "")], withoutRadar.ExtraScopeViews);
        Assert.Equal(["ScopeView"], withoutRadar.WindowGeometries.Keys);
        Assert.True(withoutRadar.IsScopePoppedOut);
    }

    [Fact]
    public void LayoutWithNullExtraViewEntries_LoadsWithoutThrowing()
    {
        var onlyNull = new JsonArray { null };
        var mixed = new JsonArray
        {
            null,
            new JsonObject { ["ordinal"] = 2, ["airportId"] = "KOAK" },
        };
        WriteV3File(
            new JsonObject { ["name"] = "WPT-NullOnly", ["extraRadarViews"] = onlyNull },
            file =>
                file["layouts"]!
                    .AsArray()
                    .Add(
                        new JsonObject
                        {
                            ["name"] = "WPT-NullMixed",
                            ["extraRadarViews"] = mixed,
                            ["extraScopeViews"] = new JsonArray { null },
                        }
                    )
        );

        var prefs = new UserPreferences();

        SavedLayout nullOnly = prefs.GetLayout("WPT-NullOnly")!;
        Assert.Null(nullOnly.ExtraScopeViews);
        Assert.Null(nullOnly.ExtraRadarViews);
        SavedLayout nullMixed = prefs.GetLayout("WPT-NullMixed")!;
        Assert.Equal(new SavedExtraView(2, ""), nullMixed.ExtraScopeViews![^1]);
        Assert.Null(nullMixed.ExtraRadarViews);
    }

    [Fact]
    public void LayoutWithNullWindowGeometries_LoadsWithoutThrowing()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-NullGeometries",
                ["isRadarViewPoppedOut"] = true,
                ["windowGeometries"] = null,
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-NullGeometries")!;

        Assert.True(layout.IsScopePoppedOut);
        Assert.Null(layout.IsRadarViewPoppedOut);
    }

    [Fact]
    public void ImportedBundleLayout_AlreadyMigrated_IsUnchanged()
    {
        var layouts = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "New",
                ["isScopePoppedOut"] = true,
                ["extraScopeViews"] = Extras((2, "")),
                ["windowGeometries"] = new JsonObject { ["ScopeView"] = Geometry(1, 1, 111, 111), ["ScopeView#2"] = Geometry(2, 2, 222, 222) },
            },
        };
        var entry = new SettingsBundleEntry(
            SettingsItemType.Layouts,
            SettingsBundleFormats.LayoutsJson,
            "layouts.json",
            Encoding.UTF8.GetBytes(layouts.ToJsonString())
        );

        SavedLayout read = Assert.Single(SettingsBundleItems.ReadLayouts(entry));

        Assert.True(read.IsScopePoppedOut);
        Assert.Equal([new SavedExtraView(2, "")], read.ExtraScopeViews);
        Assert.Equal(["ScopeView", "ScopeView#2"], read.WindowGeometries.Keys.Order(StringComparer.Ordinal));
        AssertGeometry(read.WindowGeometries["ScopeView"], 1, 1, 111, 111);
        AssertGeometry(read.WindowGeometries["ScopeView#2"], 2, 2, 222, 222);
        AssertNoRadarRows(read);
    }

    [Fact]
    public void FileWithNoRadarFields_IsUnchanged_AndVersionIsCurrent()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-ScopeOnly",
                ["isScopePoppedOut"] = true,
                ["extraScopeViews"] = Extras((2, "")),
                ["windowGeometries"] = new JsonObject { ["ScopeView"] = Geometry(1, 1, 111, 111), ["ScopeView#2"] = Geometry(2, 2, 222, 222) },
            }
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-ScopeOnly")!;

        Assert.True(layout.IsScopePoppedOut);
        Assert.Equal([new SavedExtraView(2, "")], layout.ExtraScopeViews);
        Assert.Equal(["ScopeView", "ScopeView#2"], layout.WindowGeometries.Keys.Order(StringComparer.Ordinal));
        AssertGeometry(layout.WindowGeometries["ScopeView"], 1, 1, 111, 111);
        AssertGeometry(layout.WindowGeometries["ScopeView#2"], 2, 2, 222, 222);
        Assert.Equal(
            UserPreferences.CurrentPreferencesVersion,
            JsonNode.Parse(File.ReadAllText(PreferencesPath))!["preferencesVersion"]!.GetValue<int>()
        );
    }

    [Fact]
    public void SlowPathFile_StillMigrates()
    {
        WriteV3File(
            new JsonObject
            {
                ["name"] = "WPT-SlowPath",
                ["isRadarViewPoppedOut"] = true,
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(10, 20, 900, 600) },
            },
            file => file["autoAcceptDelaySeconds"] = "not a number"
        );

        SavedLayout layout = new UserPreferences().GetLayout("WPT-SlowPath")!;

        // The field-by-field recovery backs the file up before reading it, so the backup proves the slow path ran.
        Assert.True(File.Exists(PreferencesPath + ".bak"));

        Assert.True(layout.IsScopePoppedOut);
        AssertGeometry(layout.WindowGeometries.GetValueOrDefault("ScopeView"), 10, 20, 900, 600);
        AssertNoRadarRows(layout);
    }

    [Fact]
    public void ImportedBundleLayout_IsMigrated()
    {
        var layouts = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Old",
                ["isRadarViewPoppedOut"] = true,
                ["extraRadarViews"] = Extras((2, "KOAK")),
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(10, 20, 900, 600), ["RadarView#2"] = Geometry(1, 2, 300, 200) },
            },
            new JsonObject
            {
                ["name"] = "NullExtras",
                ["extraRadarViews"] = null,
                ["windowGeometries"] = new JsonObject { ["RadarView"] = Geometry(5, 6, 700, 500) },
            },
        };
        var entry = new SettingsBundleEntry(
            SettingsItemType.Layouts,
            SettingsBundleFormats.LayoutsJson,
            "layouts.json",
            Encoding.UTF8.GetBytes(layouts.ToJsonString())
        );

        List<SavedLayout> read = SettingsBundleItems.ReadLayouts(entry);

        SavedLayout old = read.Single(l => l.Name == "Old");
        Assert.True(old.IsScopePoppedOut);
        Assert.Equal([new SavedExtraView(2, "")], old.ExtraScopeViews);
        AssertGeometry(old.WindowGeometries.GetValueOrDefault("ScopeView"), 10, 20, 900, 600);
        AssertGeometry(old.WindowGeometries.GetValueOrDefault("ScopeView#2"), 1, 2, 300, 200);
        AssertNoRadarRows(old);

        SavedLayout nullExtras = read.Single(l => l.Name == "NullExtras");
        Assert.Null(nullExtras.ExtraScopeViews);
        AssertGeometry(nullExtras.WindowGeometries.GetValueOrDefault("ScopeView"), 5, 6, 700, 500);
        AssertNoRadarRows(nullExtras);
    }

    private static void WriteFile(int version, Action<JsonObject> fill)
    {
        var file = new JsonObject { ["preferencesVersion"] = version };
        fill(file);
        File.WriteAllText(PreferencesPath, file.ToJsonString());
    }

    // An ordinary setter saves the file, as any later change in the session would.
    private static JsonObject SaveAndReadBack(UserPreferences prefs)
    {
        prefs.SetWindowGeometry("Main", new SavedWindowGeometry { Width = 800 });
        return WrittenFile();
    }

    [Fact]
    public void V4FileWithLeftoverRadarDefaults_MigratesNothing_AndDropsThemAtTheNextSave()
    {
        WriteFile(
            4,
            file =>
            {
                file["isRadarViewPoppedOut"] = false;
                file["extraRadarViews"] = new JsonArray();
                file["isScopePoppedOut"] = false;
                file["windowGeometries"] = new JsonObject { ["ScopeView"] = Geometry(1, 1, 111, 111) };
                file["layouts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "WPT-V4Leftover",
                        ["isRadarViewPoppedOut"] = false,
                        ["extraRadarViews"] = new JsonArray(),
                    },
                };
            }
        );

        var prefs = new UserPreferences();

        Assert.False(prefs.IsScopePoppedOut);
        AssertGeometry(prefs.GetWindowGeometry("ScopeView"), 1, 1, 111, 111);
        Assert.Null(prefs.GetWindowGeometry("ScopeView#2"));
        JsonObject saved = SaveAndReadBack(prefs);
        Assert.False(saved.ContainsKey("isRadarViewPoppedOut"));
        Assert.False(saved.ContainsKey("extraRadarViews"));
        Assert.False(saved["isScopePoppedOut"]!.GetValue<bool>());
        JsonObject savedLayout = saved["layouts"]![0]!.AsObject();
        Assert.False(savedLayout.ContainsKey("isRadarViewPoppedOut"));
        Assert.False(savedLayout.ContainsKey("extraRadarViews"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void OldFileWithRemovedRadarKeys_Loads_KeepsRadarSettings_AndDropsTheRemovedKeys(int version)
    {
        WriteFile(
            version,
            file =>
            {
                file["radarSettings"] = new JsonObject
                {
                    ["S1"] = new JsonObject
                    {
                        ["rangeNm"] = 25,
                        ["pinnedMarkers"] = new JsonArray { "SFO270010" },
                    },
                };
                file["radarDcbVisible"] = false;
                file["radarFlyoutFontSize"] = 16;
                file["collapseStudentDatablocks"] = true;
                file["radarViewWindowGeometry"] = Geometry(9, 9, 999, 999);
                file["isRadarViewPoppedOut"] = true;
                file["extraRadarViews"] = Extras((2, "KOAK"));
            }
        );

        JsonObject saved = SaveAndReadBack(new UserPreferences());

        JsonNode s1 = saved["radarSettings"]!["S1"]!;
        Assert.Equal(25, s1["rangeNm"]!.GetValue<double>());
        Assert.Equal("SFO270010", s1["pinnedMarkers"]![0]!.GetValue<string>());
        string[] removed = ["radarDcbVisible", "collapseStudentDatablocks", "radarViewWindowGeometry", "isRadarViewPoppedOut", "extraRadarViews"];
        Assert.All(removed, key => Assert.False(saved.ContainsKey(key), key));
        Assert.Equal(16, saved["radarFlyoutFontSize"]!.GetValue<int>());
    }
}
