using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

public class ViewSettingsCopyCatalogTests
{
    [Fact]
    public void GroundFiltersGroup_CopiesFilters_LeavesLabels()
    {
        var source = new SavedGroundSettings
        {
            ShowHoldShort = GroundFilterMode.Off,
            ShowParking = GroundFilterMode.IconsOnly,
            ShowSpot = GroundFilterMode.Off,
            ShowRunwayLabels = false,
        };
        var target = new SavedGroundSettings { ShowRunwayLabels = true };
        GroundCopyGroup filters = GroundGroup("ground.filters");

        filters.Copy(source, target);

        Assert.Equal(GroundFilterMode.Off, target.ShowHoldShort);
        Assert.Equal(GroundFilterMode.IconsOnly, target.ShowParking);
        Assert.Equal(GroundFilterMode.Off, target.ShowSpot);
        Assert.True(target.ShowRunwayLabels);
    }

    [Fact]
    public void AllGroundGroupsCopied_MakesTargetEqualSourcePerGroup()
    {
        var source = new SavedGroundSettings
        {
            CenterLat = 37.7,
            CenterLon = -122.2,
            Zoom = 2.4,
            Rotation = 120,
            IsPanZoomLocked = true,
            ShowRunwayLabels = false,
            ShowTaxiwayLabels = false,
            ShowHoldShort = GroundFilterMode.IconsOnly,
            ShowParking = GroundFilterMode.Off,
            ShowSpot = GroundFilterMode.IconsOnly,
            ShowAdwMarkings = false,
        };
        var target = new SavedGroundSettings();

        foreach (GroundCopyGroup group in ViewSettingsCopyCatalog.GroundGroups)
        {
            group.Copy(source, target);
        }

        foreach (GroundCopyGroup group in ViewSettingsCopyCatalog.GroundGroups)
        {
            Assert.True(group.AreEqual(source, target), $"ground group '{group.Key}' not equal after full copy");
        }
    }

    [Fact]
    public void LayoutGroups_ListEveryPopOutWindowAndLayoutPart()
    {
        // The apply dialog renders one row per group, so every pop-out window, the extra views, favorites and the open
        // tabs must each be a group, not only the four pop-outs the dialog once summarized.
        Assert.Equal(
            [
                ViewSettingsCopyCatalog.LayoutTerminalKey,
                ViewSettingsCopyCatalog.LayoutAircraftListKey,
                ViewSettingsCopyCatalog.LayoutGroundKey,
                ViewSettingsCopyCatalog.LayoutScopeKey,
                ViewSettingsCopyCatalog.LayoutControllersKey,
                ViewSettingsCopyCatalog.LayoutMetarKey,
                ViewSettingsCopyCatalog.LayoutExtraScopeKey,
                ViewSettingsCopyCatalog.LayoutExtraGroundKey,
                ViewSettingsCopyCatalog.LayoutFavoriteSetsKey,
                ViewSettingsCopyCatalog.LayoutFavoritesBarKey,
                ViewSettingsCopyCatalog.LayoutFavoritesPanelKey,
                ViewSettingsCopyCatalog.LayoutRundownsWindowKey,
                ViewSettingsCopyCatalog.LayoutOpenTabsKey,
                ViewSettingsCopyCatalog.LayoutColumnsKey,
            ],
            ViewSettingsCopyCatalog.LayoutGroups.Select(g => g.Key)
        );
    }

    [Fact]
    public void ControllersAndMetarRows_DescribeAndDiffTheirOwnFlag()
    {
        var current = new SavedLayout { IsControllersPoppedOut = false, IsMetarPoppedOut = true };
        var source = new SavedLayout { IsControllersPoppedOut = true, IsMetarPoppedOut = true };
        LayoutCopyGroup controllers = LayoutGroup(ViewSettingsCopyCatalog.LayoutControllersKey);
        LayoutCopyGroup metar = LayoutGroup(ViewSettingsCopyCatalog.LayoutMetarKey);

        Assert.Equal("docked", controllers.Describe(current));
        Assert.Equal("popped out", controllers.Describe(source));
        Assert.False(controllers.AreEqual(current, source));
        Assert.True(metar.AreEqual(current, source));
        Assert.True(controllers.IsSaved(source));
    }

    [Fact]
    public void TerminalRow_DescribesAndDiffsTheDockedSize()
    {
        LayoutCopyGroup terminal = LayoutGroup(ViewSettingsCopyCatalog.LayoutTerminalKey);
        var quarter = new SavedLayout { TerminalSplitRatio = 0.25 };
        var half = new SavedLayout { TerminalSplitRatio = 0.5 };
        var poppedOutHalf = new SavedLayout { IsTerminalPoppedOut = true, TerminalSplitRatio = 0.5 };
        var legacy = new SavedLayout();

        Assert.Equal("docked · size 25%", terminal.Describe(quarter));
        // A popped-out terminal is sized by its own window geometry, so its saved share never shows or compares.
        Assert.Equal("popped out", terminal.Describe(poppedOutHalf));
        // A layout saved before the size was captured describes only its dock state.
        Assert.Equal("docked", terminal.Describe(legacy));
        Assert.False(terminal.AreEqual(quarter, half));
        Assert.False(terminal.AreEqual(half, poppedOutHalf));
        Assert.True(terminal.AreEqual(poppedOutHalf, new SavedLayout { IsTerminalPoppedOut = true }));
        // A layout with no recorded size leaves the split as it is when applied, so it has nothing to compare.
        Assert.True(terminal.AreEqual(legacy, quarter));
        Assert.True(terminal.AreEqual(half, new SavedLayout { TerminalSplitRatio = 0.5 }));
        Assert.True(terminal.IsSaved(legacy));
    }

    [Fact]
    public void ExtraViewRows_DescribeOrdinalAndAirport_AndDiffEachKind()
    {
        var current = new SavedLayout { ExtraScopeViews = [new SavedExtraView(2, "")] };
        var source = new SavedLayout
        {
            ExtraScopeViews = [new SavedExtraView(2, "")],
            ExtraGroundViews = [new SavedExtraView(2, "KSFO"), new SavedExtraView(3, "KOAK")],
        };
        LayoutCopyGroup scope = LayoutGroup(ViewSettingsCopyCatalog.LayoutExtraScopeKey);
        LayoutCopyGroup ground = LayoutGroup(ViewSettingsCopyCatalog.LayoutExtraGroundKey);

        Assert.Equal("#2", scope.Describe(source));
        Assert.Equal("#2 KSFO, #3 KOAK", ground.Describe(source));
        Assert.Equal("none", ground.Describe(current));
        Assert.True(scope.AreEqual(current, source));
        Assert.True(scope.IsSaved(source));
        Assert.False(ground.AreEqual(current, source));
    }

    [Fact]
    public void ScopeRows_DescribeDiffAndIsSaved()
    {
        LayoutCopyGroup popOut = LayoutGroup(ViewSettingsCopyCatalog.LayoutScopeKey);
        LayoutCopyGroup extras = LayoutGroup(ViewSettingsCopyCatalog.LayoutExtraScopeKey);
        var legacy = new SavedLayout();
        var poppedOut = new SavedLayout { IsScopePoppedOut = true, ExtraScopeViews = [new SavedExtraView(3, ""), new SavedExtraView(2, "")] };
        var docked = new SavedLayout { IsScopePoppedOut = false, ExtraScopeViews = [] };

        Assert.Equal(("Pop-out windows", "Radar view"), (popOut.Section, popOut.Label));
        Assert.Equal(("Extra windows", "Extra Radar windows"), (extras.Section, extras.Label));

        Assert.Equal("popped out", popOut.Describe(poppedOut));
        Assert.Equal("docked", popOut.Describe(docked));
        Assert.Equal("not saved", popOut.Describe(legacy));
        Assert.False(popOut.AreEqual(poppedOut, docked));
        Assert.True(popOut.AreEqual(docked, new SavedLayout { IsScopePoppedOut = false }));
        // A layout that saved no Scope state leaves the Scope as it is when applied, so it has nothing to compare.
        Assert.True(popOut.AreEqual(legacy, poppedOut));
        Assert.False(popOut.IsSaved(legacy));
        Assert.True(popOut.IsSaved(docked));

        Assert.Equal("#2, #3", extras.Describe(poppedOut));
        Assert.Equal("none", extras.Describe(docked));
        Assert.Equal("not saved", extras.Describe(legacy));
        Assert.False(extras.AreEqual(poppedOut, docked));
        Assert.True(extras.AreEqual(poppedOut, new SavedLayout { ExtraScopeViews = [new SavedExtraView(2, ""), new SavedExtraView(3, "")] }));
        Assert.True(extras.AreEqual(legacy, poppedOut));
        // A number listed twice is one window, as applying the layout opens it once.
        var twice = new SavedLayout { ExtraScopeViews = [new SavedExtraView(2, ""), new SavedExtraView(2, "")] };
        Assert.Equal("#2", extras.Describe(twice));
        Assert.True(extras.AreEqual(twice, new SavedLayout { ExtraScopeViews = [new SavedExtraView(2, "")] }));
        Assert.False(extras.IsSaved(legacy));
        Assert.True(extras.IsSaved(docked));
    }

    [Fact]
    public void FavoriteSetsRow_TwoLayoutsDifferingOnlyInSetOrder_AreNotEqual()
    {
        var a = new SavedLayout { LoadedFavoriteSetIds = ["aaaa1111"], FavoriteSetOrder = ["global", "aaaa1111", "airport", "scenario"] };
        var b = new SavedLayout { LoadedFavoriteSetIds = ["aaaa1111"], FavoriteSetOrder = ["aaaa1111", "global", "airport", "scenario"] };
        var orderOnly = new SavedLayout { FavoriteSetOrder = ["scenario"] };
        LayoutCopyGroup group = LayoutGroup(ViewSettingsCopyCatalog.LayoutFavoriteSetsKey);

        Assert.False(group.AreEqual(a, b));
        Assert.True(group.AreEqual(a, a));
        Assert.True(group.IsSaved(orderOnly));
        Assert.Equal("not saved", group.Describe(orderOnly));
        Assert.Equal("1 set", group.Describe(a));
    }

    [Fact]
    public void FavoritesRows_OnALayoutSavedBeforeTheyExisted_AreNotSaved()
    {
        var legacy = new SavedLayout();
        var saved = new SavedLayout
        {
            LoadedFavoriteSetIds = ["aaaa1111", "bbbb2222"],
            ShowFavoritesBar = false,
            IsFavoritesPanelOpen = true,
        };

        foreach (
            string key in new[]
            {
                ViewSettingsCopyCatalog.LayoutFavoriteSetsKey,
                ViewSettingsCopyCatalog.LayoutFavoritesBarKey,
                ViewSettingsCopyCatalog.LayoutFavoritesPanelKey,
            }
        )
        {
            LayoutCopyGroup group = LayoutGroup(key);
            Assert.False(group.IsSaved(legacy), $"'{key}' should have nothing to apply on a legacy layout");
            Assert.Equal("not saved", group.Describe(legacy));
            Assert.True(group.IsSaved(saved));
        }

        Assert.Equal("2 sets", LayoutGroup(ViewSettingsCopyCatalog.LayoutFavoriteSetsKey).Describe(saved));
        Assert.Equal("hidden", LayoutGroup(ViewSettingsCopyCatalog.LayoutFavoritesBarKey).Describe(saved));
        Assert.Equal("open", LayoutGroup(ViewSettingsCopyCatalog.LayoutFavoritesPanelKey).Describe(saved));
    }

    [Fact]
    public void OpenTabsRow_DescribesBothKinds_AndIsNotSavedWhenNull()
    {
        LayoutCopyGroup tabs = LayoutGroup(ViewSettingsCopyCatalog.LayoutOpenTabsKey);
        var source = new SavedLayout
        {
            OpenTabs = new SavedOpenTabs { Strips = ["NCT", "OAK"], Tdls = ["SFO"] },
        };
        var empty = new SavedLayout { OpenTabs = new SavedOpenTabs() };

        Assert.Equal("Strips: NCT, OAK · vTDLS: SFO", tabs.Describe(source));
        Assert.Equal("none", tabs.Describe(empty));
        Assert.False(tabs.AreEqual(empty, source));
        Assert.False(tabs.IsSaved(new SavedLayout()));
        Assert.True(tabs.IsSaved(empty));
    }

    [Fact]
    public void AllLayoutKeys_SelectsEveryRowAndEverySavedGeometry()
    {
        var layout = new SavedLayout
        {
            WindowGeometries = new() { ["Main"] = new SavedWindowGeometry(), ["RadarView#2"] = new SavedWindowGeometry() },
        };

        HashSet<string> keys = ViewSettingsCopyCatalog.AllLayoutKeys(layout);

        Assert.All(ViewSettingsCopyCatalog.LayoutGroups, g => Assert.Contains(g.Key, keys));
        Assert.Contains(ViewSettingsCopyCatalog.LayoutGeometryKeyPrefix + "Main", keys);
        Assert.Contains(ViewSettingsCopyCatalog.LayoutGeometryKeyPrefix + "RadarView#2", keys);
        Assert.Equal(ViewSettingsCopyCatalog.LayoutGroups.Count + 2, keys.Count);
    }

    private static LayoutCopyGroup LayoutGroup(string key) => ViewSettingsCopyCatalog.LayoutGroups.Single(g => g.Key == key);

    private static GroundCopyGroup GroundGroup(string key) => ViewSettingsCopyCatalog.GroundGroups.Single(g => g.Key == key);
}
