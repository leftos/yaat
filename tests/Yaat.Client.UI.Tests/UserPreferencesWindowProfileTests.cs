using System.ComponentModel;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Tests share preferences.json (per-process temp dir set by the test fixture's
// ModuleInitializer). Each test uses unique profile names so concurrent runs do
// not collide on key. Disk-round-trip assertions read from a fresh UserPreferences
// to verify persistence; other assertions use the same instance to avoid the
// inter-instance Save() race noted in Save_ConcurrentWritersDoNotRaceOnTmpFile.
// Tests delete their own profiles at the end to limit accumulation.
public class UserPreferencesWindowProfileTests
{
    [Fact]
    public void SaveWindowProfile_RoundTripsThroughDisk()
    {
        var profile = new SavedWindowProfile
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
        };

        var writer = new UserPreferences();
        writer.SaveWindowProfile(profile);

        var reader = new UserPreferences();
        SavedWindowProfile? reloaded = reader.GetWindowProfile("WPT-Roundtrip");

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

        new UserPreferences().DeleteWindowProfile("WPT-Roundtrip");
    }

    [Fact]
    public void SaveWindowProfile_RoundTripsExtraViews()
    {
        var profile = new SavedWindowProfile
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
            new UserPreferences().SaveWindowProfile(profile);

            SavedWindowProfile? reloaded = new UserPreferences().GetWindowProfile("WPT-ExtraOrdinals");

            Assert.NotNull(reloaded);
            // Ordinal and airport both ride the profile, so applying it reopens the same windows.
            Assert.Equal([new SavedExtraView(2, "KOAK"), new SavedExtraView(3, "KSFO")], reloaded.ExtraRadarViews);
            Assert.Equal([new SavedExtraView(2, "KOAK")], reloaded.ExtraGroundViews);
            // The extra window's geometry rides in the same dictionary as any other non-fixed window name.
            Assert.Equal(900, reloaded.WindowGeometries["RadarView#2"].Width);
            Assert.Equal(1, reloaded.WindowGeometries["RadarView#2"].ScreenIndex);
        }
        finally
        {
            new UserPreferences().DeleteWindowProfile("WPT-ExtraOrdinals");
        }
    }

    [Fact]
    public void SaveWindowProfile_CapturedBeforeExtraViews_ReadsBackAsNoExtras()
    {
        try
        {
            new UserPreferences().SaveWindowProfile(new SavedWindowProfile { Name = "WPT-NoExtras" });

            SavedWindowProfile? reloaded = new UserPreferences().GetWindowProfile("WPT-NoExtras");

            // Empty (not null): applying such a profile closes any extra windows, since a profile is the
            // whole arrangement rather than a partial overlay.
            Assert.NotNull(reloaded);
            Assert.Empty(reloaded.ExtraRadarViews);
            Assert.Empty(reloaded.ExtraGroundViews);
        }
        finally
        {
            new UserPreferences().DeleteWindowProfile("WPT-NoExtras");
        }
    }

    [Fact]
    public void SaveWindowProfile_NullFavoritesFlags_RoundTripAsNull()
    {
        var writer = new UserPreferences();
        writer.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-NullFavFlags", IsTerminalPoppedOut = true });

        SavedWindowProfile? reloaded = new UserPreferences().GetWindowProfile("WPT-NullFavFlags");

        Assert.NotNull(reloaded);
        // Null = captured before the feature; applying leaves the current bar / panel state untouched.
        Assert.Null(reloaded.ShowFavoritesBar);
        Assert.Null(reloaded.IsFavoritesPanelOpen);

        new UserPreferences().DeleteWindowProfile("WPT-NullFavFlags");
    }

    [Fact]
    public void SaveWindowProfile_DuplicateName_OverwritesAndPreservesCreatedUtc()
    {
        var prefs = new UserPreferences();

        var original = new SavedWindowProfile { Name = "WPT-Overwrite", CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        prefs.SaveWindowProfile(original);
        DateTime originalCreated = prefs.GetWindowProfile("WPT-Overwrite")!.CreatedUtc;

        var replacement = new SavedWindowProfile { Name = "WPT-Overwrite", IsDataGridPoppedOut = true };
        prefs.SaveWindowProfile(replacement);

        SavedWindowProfile? reloaded = prefs.GetWindowProfile("WPT-Overwrite");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsDataGridPoppedOut);
        Assert.Equal(originalCreated, reloaded.CreatedUtc);
        Assert.True(reloaded.ModifiedUtc >= originalCreated);

        prefs.DeleteWindowProfile("WPT-Overwrite");
    }

    [Fact]
    public void DeleteWindowProfile_RemovesEntry()
    {
        var prefs = new UserPreferences();
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-ToDelete" });
        Assert.NotNull(prefs.GetWindowProfile("WPT-ToDelete"));

        prefs.DeleteWindowProfile("WPT-ToDelete");

        Assert.Null(prefs.GetWindowProfile("WPT-ToDelete"));
    }

    [Fact]
    public void RenameWindowProfile_ChangesName_KeepsContents()
    {
        var prefs = new UserPreferences();
        prefs.SaveWindowProfile(
            new SavedWindowProfile
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

        bool renamed = prefs.RenameWindowProfile("WPT-OldName", "WPT-NewName");

        Assert.True(renamed);
        // Read back through the same instance to avoid the inter-instance Save()
        // race that can let another test class's concurrent write resurrect the
        // pre-rename state on disk.
        SavedWindowProfile? reloaded = prefs.GetWindowProfile("WPT-NewName");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsRadarViewPoppedOut);
        Assert.Equal(300, reloaded.WindowGeometries["Main"].Width);
        Assert.Null(prefs.GetWindowProfile("WPT-OldName"));

        prefs.DeleteWindowProfile("WPT-NewName");
    }

    [Fact]
    public void RenameWindowProfile_Collision_ReturnsFalse()
    {
        var prefs = new UserPreferences();
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-CollideA" });
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-CollideB" });

        bool renamed = prefs.RenameWindowProfile("WPT-CollideA", "WPT-CollideB");

        Assert.False(renamed);
        Assert.NotNull(prefs.GetWindowProfile("WPT-CollideA"));
        Assert.NotNull(prefs.GetWindowProfile("WPT-CollideB"));

        prefs.DeleteWindowProfile("WPT-CollideA");
        prefs.DeleteWindowProfile("WPT-CollideB");
    }

    [Fact]
    public void WindowProfiles_AreSortedByName()
    {
        var prefs = new UserPreferences();
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-Sort-Zulu" });
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-Sort-Alpha" });
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "WPT-Sort-Mike" });

        string[] names = [.. prefs.WindowProfiles.Where(p => p.Name.StartsWith("WPT-Sort-", StringComparison.Ordinal)).Select(p => p.Name)];

        Assert.Equal(["WPT-Sort-Alpha", "WPT-Sort-Mike", "WPT-Sort-Zulu"], names);

        prefs.DeleteWindowProfile("WPT-Sort-Zulu");
        prefs.DeleteWindowProfile("WPT-Sort-Alpha");
        prefs.DeleteWindowProfile("WPT-Sort-Mike");
    }
}
