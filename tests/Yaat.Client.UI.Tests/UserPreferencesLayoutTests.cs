using System.ComponentModel;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// Tests share preferences.json (per-process temp dir set by the test fixture's
// ModuleInitializer). Each test uses unique layout names so concurrent runs do
// not collide on key. Disk-round-trip assertions read from a fresh UserPreferences
// to verify persistence; other assertions use the same instance to avoid the
// inter-instance Save() race noted in Save_ConcurrentWritersDoNotRaceOnTmpFile.
// Tests delete their own layouts at the end to limit accumulation.
public class UserPreferencesLayoutTests
{
    [Fact]
    public void SaveLayout_RoundTripsThroughDisk()
    {
        var layout = new SavedLayout
        {
            Name = "WPT-Roundtrip",
            IsTerminalPoppedOut = true,
            IsDataGridPoppedOut = true,
            IsGroundViewPoppedOut = true,
            IsRadarViewPoppedOut = false,
            WindowGeometries = new()
            {
                ["Main"] = new SavedWindowGeometry
                {
                    X = 100,
                    Y = 200,
                    Width = 1280,
                    Height = 720,
                    IsMaximized = false,
                    IsTopmost = false,
                    ScreenIndex = 0,
                },
                ["GroundView"] = new SavedWindowGeometry
                {
                    X = 1400,
                    Y = 100,
                    Width = 800,
                    Height = 600,
                    IsMaximized = true,
                    IsTopmost = true,
                    ScreenIndex = 1,
                },
            },
            DataGridLayout = new SavedGridLayout
            {
                ColumnOrder = ["callsign", "type", "altitude"],
                SortColumn = "callsign",
                SortDirection = ListSortDirection.Ascending,
                ColumnWidths = new() { ["callsign"] = 120.5, ["altitude"] = 70 },
                HiddenColumns = ["squawk"],
            },
            LoadedFavoriteSetIds = ["bbbb2222", "aaaa1111"],
            ShowFavoritesBar = false,
            IsFavoritesPanelOpen = true,
            OpenTabs = new SavedOpenTabs { Strips = ["NCT", "OAK", "OAK"], Tdls = ["SFO"] },
        };

        var writer = new UserPreferences();
        writer.SaveLayout(layout);

        var reader = new UserPreferences();
        SavedLayout? reloaded = reader.GetLayout("WPT-Roundtrip");

        Assert.NotNull(reloaded);
        Assert.Equal("WPT-Roundtrip", reloaded.Name);
        Assert.True(reloaded.IsTerminalPoppedOut);
        Assert.True(reloaded.IsDataGridPoppedOut);
        Assert.True(reloaded.IsGroundViewPoppedOut);
        Assert.False(reloaded.IsRadarViewPoppedOut);

        Assert.Equal(2, reloaded.WindowGeometries.Count);
        SavedWindowGeometry main = reloaded.WindowGeometries["Main"];
        Assert.Equal(100, main.X);
        Assert.Equal(200, main.Y);
        Assert.Equal(1280, main.Width);
        Assert.Equal(720, main.Height);
        Assert.False(main.IsMaximized);

        SavedWindowGeometry ground = reloaded.WindowGeometries["GroundView"];
        Assert.True(ground.IsMaximized);
        Assert.True(ground.IsTopmost);
        Assert.Equal(1, ground.ScreenIndex);

        Assert.NotNull(reloaded.DataGridLayout);
        Assert.Equal(["callsign", "type", "altitude"], reloaded.DataGridLayout.ColumnOrder);
        Assert.Equal("callsign", reloaded.DataGridLayout.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, reloaded.DataGridLayout.SortDirection);
        Assert.Equal(120.5, reloaded.DataGridLayout.ColumnWidths!["callsign"]);
        Assert.Equal(["squawk"], reloaded.DataGridLayout.HiddenColumns);

        // Loaded favorite set ids round-trip in load order (not sorted).
        Assert.Equal(["bbbb2222", "aaaa1111"], reloaded.LoadedFavoriteSetIds);

        Assert.False(reloaded.ShowFavoritesBar);
        Assert.True(reloaded.IsFavoritesPanelOpen);

        // Open tabs round-trip in tab order, a facility opened twice kept twice.
        Assert.NotNull(reloaded.OpenTabs);
        Assert.Equal(["NCT", "OAK", "OAK"], reloaded.OpenTabs.Strips);
        Assert.Equal(["SFO"], reloaded.OpenTabs.Tdls);

        new UserPreferences().DeleteLayout("WPT-Roundtrip");
    }

    [Fact]
    public void SaveLayout_RoundTripsExtraViews()
    {
        var layout = new SavedLayout
        {
            Name = "WPT-ExtraOrdinals",
            ExtraRadarViews = [new SavedExtraView(2, "KOAK"), new SavedExtraView(3, "KSFO")],
            ExtraGroundViews = [new SavedExtraView(2, "KOAK")],
            WindowGeometries = new()
            {
                ["RadarView#2"] = new SavedWindowGeometry
                {
                    X = 300,
                    Y = 400,
                    Width = 900,
                    Height = 500,
                    ScreenIndex = 1,
                },
            },
        };

        try
        {
            new UserPreferences().SaveLayout(layout);

            SavedLayout? reloaded = new UserPreferences().GetLayout("WPT-ExtraOrdinals");

            Assert.NotNull(reloaded);
            // Ordinal and airport both ride the layout, so applying it reopens the same windows.
            Assert.Equal([new SavedExtraView(2, "KOAK"), new SavedExtraView(3, "KSFO")], reloaded.ExtraRadarViews);
            Assert.Equal([new SavedExtraView(2, "KOAK")], reloaded.ExtraGroundViews);
            // The extra window's geometry rides in the same dictionary as any other non-fixed window name.
            Assert.Equal(900, reloaded.WindowGeometries["RadarView#2"].Width);
            Assert.Equal(1, reloaded.WindowGeometries["RadarView#2"].ScreenIndex);
        }
        finally
        {
            new UserPreferences().DeleteLayout("WPT-ExtraOrdinals");
        }
    }

    [Fact]
    public void SaveLayout_CapturedBeforeExtraViews_ReadsBackAsNoExtras()
    {
        try
        {
            new UserPreferences().SaveLayout(new SavedLayout { Name = "WPT-NoExtras" });

            SavedLayout? reloaded = new UserPreferences().GetLayout("WPT-NoExtras");

            // Empty (not null): applying such a layout closes any extra windows, since a layout is the
            // whole arrangement rather than a partial overlay.
            Assert.NotNull(reloaded);
            Assert.Empty(reloaded.ExtraRadarViews);
            Assert.Empty(reloaded.ExtraGroundViews);
        }
        finally
        {
            new UserPreferences().DeleteLayout("WPT-NoExtras");
        }
    }

    [Fact]
    public void SaveLayout_NullFavoritesFlags_RoundTripAsNull()
    {
        var writer = new UserPreferences();
        writer.SaveLayout(new SavedLayout { Name = "WPT-NullFavFlags", IsTerminalPoppedOut = true });

        SavedLayout? reloaded = new UserPreferences().GetLayout("WPT-NullFavFlags");

        Assert.NotNull(reloaded);
        // Null = captured before the feature; applying leaves the current bar / panel state untouched.
        Assert.Null(reloaded.ShowFavoritesBar);
        Assert.Null(reloaded.IsFavoritesPanelOpen);

        new UserPreferences().DeleteLayout("WPT-NullFavFlags");
    }

    [Fact]
    public void SaveLayout_DuplicateName_OverwritesAndPreservesCreatedUtc()
    {
        var prefs = new UserPreferences();

        var original = new SavedLayout { Name = "WPT-Overwrite", CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        prefs.SaveLayout(original);
        DateTime originalCreated = prefs.GetLayout("WPT-Overwrite")!.CreatedUtc;

        var replacement = new SavedLayout { Name = "WPT-Overwrite", IsDataGridPoppedOut = true };
        prefs.SaveLayout(replacement);

        SavedLayout? reloaded = prefs.GetLayout("WPT-Overwrite");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsDataGridPoppedOut);
        Assert.Equal(originalCreated, reloaded.CreatedUtc);
        Assert.True(reloaded.ModifiedUtc >= originalCreated);

        prefs.DeleteLayout("WPT-Overwrite");
    }

    [Fact]
    public void DeleteLayout_RemovesEntry()
    {
        var prefs = new UserPreferences();
        prefs.SaveLayout(new SavedLayout { Name = "WPT-ToDelete" });
        Assert.NotNull(prefs.GetLayout("WPT-ToDelete"));

        prefs.DeleteLayout("WPT-ToDelete");

        Assert.Null(prefs.GetLayout("WPT-ToDelete"));
    }

    [Fact]
    public void RenameLayout_ChangesName_KeepsContents()
    {
        var prefs = new UserPreferences();
        prefs.SaveLayout(
            new SavedLayout
            {
                Name = "WPT-OldName",
                IsRadarViewPoppedOut = true,
                WindowGeometries = new()
                {
                    ["Main"] = new SavedWindowGeometry
                    {
                        X = 1,
                        Y = 2,
                        Width = 300,
                        Height = 400,
                    },
                },
            }
        );

        bool renamed = prefs.RenameLayout("WPT-OldName", "WPT-NewName");

        Assert.True(renamed);
        // Read back through the same instance to avoid the inter-instance Save()
        // race that can let another test class's concurrent write resurrect the
        // pre-rename state on disk.
        SavedLayout? reloaded = prefs.GetLayout("WPT-NewName");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsRadarViewPoppedOut);
        Assert.Equal(300, reloaded.WindowGeometries["Main"].Width);
        Assert.Null(prefs.GetLayout("WPT-OldName"));

        prefs.DeleteLayout("WPT-NewName");
    }

    [Fact]
    public void RenameLayout_Collision_ReturnsFalse()
    {
        var prefs = new UserPreferences();
        prefs.SaveLayout(new SavedLayout { Name = "WPT-CollideA" });
        prefs.SaveLayout(new SavedLayout { Name = "WPT-CollideB" });

        bool renamed = prefs.RenameLayout("WPT-CollideA", "WPT-CollideB");

        Assert.False(renamed);
        Assert.NotNull(prefs.GetLayout("WPT-CollideA"));
        Assert.NotNull(prefs.GetLayout("WPT-CollideB"));

        prefs.DeleteLayout("WPT-CollideA");
        prefs.DeleteLayout("WPT-CollideB");
    }

    [Fact]
    public void Layouts_AreSortedByName()
    {
        var prefs = new UserPreferences();
        prefs.SaveLayout(new SavedLayout { Name = "WPT-Sort-Zulu" });
        prefs.SaveLayout(new SavedLayout { Name = "WPT-Sort-Alpha" });
        prefs.SaveLayout(new SavedLayout { Name = "WPT-Sort-Mike" });

        string[] names = [.. prefs.Layouts.Where(p => p.Name.StartsWith("WPT-Sort-", StringComparison.Ordinal)).Select(p => p.Name)];

        Assert.Equal(["WPT-Sort-Alpha", "WPT-Sort-Mike", "WPT-Sort-Zulu"], names);

        prefs.DeleteLayout("WPT-Sort-Zulu");
        prefs.DeleteLayout("WPT-Sort-Alpha");
        prefs.DeleteLayout("WPT-Sort-Mike");
    }

    [Fact]
    public void SaveLayout_WithoutOpenTabs_RoundTripsAsNull()
    {
        try
        {
            new UserPreferences().SaveLayout(new SavedLayout { Name = "WPT-NoTabs", IsMetarPoppedOut = true });

            SavedLayout? reloaded = new UserPreferences().GetLayout("WPT-NoTabs");

            // Null = saved before open tabs were captured; applying it leaves the open tabs as they are.
            Assert.NotNull(reloaded);
            Assert.Null(reloaded.OpenTabs);
        }
        finally
        {
            new UserPreferences().DeleteLayout("WPT-NoTabs");
        }
    }

    /// <summary>
    /// Saved window profiles carry over as layouts: a preferences file holding only the old <c>windowProfiles</c> key
    /// loads them with their names, timestamps and fields, and the next save writes only the <c>layouts</c> key.
    /// </summary>
    [Fact]
    public void LegacyWindowProfilesKey_LoadsAsLayouts_AndNextSaveDropsIt()
    {
        var createdA = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var modifiedA = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var createdB = new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var legacy = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "WPT-Legacy-A",
                ["createdUtc"] = createdA,
                ["modifiedUtc"] = modifiedA,
                ["isMetarPoppedOut"] = true,
                ["loadedFavoriteSetIds"] = new JsonArray("aaaa1111"),
                ["windowGeometries"] = new JsonObject
                {
                    ["Main"] = new JsonObject { ["width"] = 1280, ["height"] = 720 },
                },
            },
            new JsonObject
            {
                ["name"] = "WPT-Legacy-B",
                ["createdUtc"] = createdB,
                ["modifiedUtc"] = createdB,
            },
        };
        // Version 1 so loading runs no version migration, whose save would hide whether the old key survives a load.
        var file = new JsonObject { ["preferencesVersion"] = 1, ["windowProfiles"] = legacy };

        using var scope = new PreferencesFileScope();
        File.WriteAllText(PreferencesPath, file.ToJsonString());

        var prefs = new UserPreferences();

        Assert.Equal(["WPT-Legacy-A", "WPT-Legacy-B"], prefs.Layouts.Select(l => l.Name));
        SavedLayout a = prefs.GetLayout("WPT-Legacy-A")!;
        Assert.Equal(createdA, a.CreatedUtc);
        Assert.Equal(modifiedA, a.ModifiedUtc);
        Assert.True(a.IsMetarPoppedOut);
        Assert.Equal(["aaaa1111"], a.LoadedFavoriteSetIds);
        Assert.Equal(1280, a.WindowGeometries["Main"].Width);
        Assert.Null(a.OpenTabs);
        Assert.Equal(createdB, prefs.GetLayout("WPT-Legacy-B")!.CreatedUtc);

        prefs.SaveLayout(new SavedLayout { Name = "WPT-Legacy-C" });

        JsonObject written = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
        Assert.False(written.ContainsKey("windowProfiles"));
        Assert.Equal(["WPT-Legacy-A", "WPT-Legacy-B", "WPT-Legacy-C"], written["layouts"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()));

        SavedLayout reloadedA = new UserPreferences().GetLayout("WPT-Legacy-A")!;
        Assert.Equal(createdA, reloadedA.CreatedUtc);
        Assert.Equal(modifiedA, reloadedA.ModifiedUtc);
    }

    /// <summary>
    /// A file holding both keys keeps every layout: a legacy entry whose name a layout already uses is dropped (the
    /// layout is the newer save), the others join, the list is sorted by name, and the next save drops the old key.
    /// </summary>
    [Fact]
    public void BothKeys_CollidingLegacyEntryDropped_OthersKept_SortedByName()
    {
        var layoutCreated = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var legacyCreated = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new JsonObject
        {
            ["preferencesVersion"] = 1,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["name"] = "WPT-Both-B", ["createdUtc"] = layoutCreated },
            },
            ["windowProfiles"] = new JsonArray
            {
                new JsonObject { ["name"] = "wpt-both-b", ["createdUtc"] = legacyCreated },
                new JsonObject { ["name"] = "WPT-Both-A", ["createdUtc"] = legacyCreated },
            },
        };

        using var scope = new PreferencesFileScope();
        File.WriteAllText(PreferencesPath, file.ToJsonString());

        var prefs = new UserPreferences();

        Assert.Equal(["WPT-Both-A", "WPT-Both-B"], prefs.Layouts.Select(l => l.Name));
        Assert.Equal(layoutCreated, prefs.GetLayout("WPT-Both-B")!.CreatedUtc);

        prefs.SaveLayout(new SavedLayout { Name = "WPT-Both-C" });

        JsonObject written = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
        Assert.False(written.ContainsKey("windowProfiles"));
        Assert.Equal(["WPT-Both-A", "WPT-Both-B", "WPT-Both-C"], written["layouts"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()));
    }

    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");
}
