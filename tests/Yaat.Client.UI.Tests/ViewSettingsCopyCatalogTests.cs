using System.Linq;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

public class ViewSettingsCopyCatalogTests
{
    [Fact]
    public void RadarMapsGroup_CopiesOnlyMaps_LeavesOtherFields()
    {
        var source = new SavedRadarSettings
        {
            EnabledStarsIds = [7, 8],
            RangeNm = 80,
            PtlOwn = true,
        };
        var target = new SavedRadarSettings
        {
            EnabledStarsIds = [1],
            RangeNm = 40,
            PtlOwn = false,
        };
        RadarCopyGroup maps = RadarGroup(ViewSettingsCopyCatalog.RadarMapsKey);

        maps.Copy(source, target);

        Assert.Equal(new[] { 7, 8 }, target.EnabledStarsIds);
        Assert.Equal(40, target.RangeNm);
        Assert.False(target.PtlOwn);
    }

    [Fact]
    public void RadarMapsGroup_Copy_ProducesIndependentList()
    {
        var source = new SavedRadarSettings { EnabledStarsIds = [7, 8] };
        var target = new SavedRadarSettings();
        RadarCopyGroup maps = RadarGroup(ViewSettingsCopyCatalog.RadarMapsKey);

        maps.Copy(source, target);
        target.EnabledStarsIds.Add(9);

        Assert.Equal(new[] { 7, 8 }, source.EnabledStarsIds);
    }

    [Fact]
    public void RadarCenterGroup_AreEqual_DetectsRangeDifference()
    {
        RadarCopyGroup center = RadarGroup(ViewSettingsCopyCatalog.RadarCenterKey);
        var a = new SavedRadarSettings
        {
            RangeNm = 40,
            CenterLat = 1,
            CenterLon = 2,
        };
        var b = new SavedRadarSettings
        {
            RangeNm = 40,
            CenterLat = 1,
            CenterLon = 2,
        };

        Assert.True(center.AreEqual(a, b));

        b.RangeNm = 60;
        Assert.False(center.AreEqual(a, b));
    }

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
    public void AllRadarGroupsCopied_MakesTargetEqualSourcePerGroup()
    {
        var source = new SavedRadarSettings
        {
            EnabledStarsIds = [3, 9],
            CenterLat = 37.7,
            CenterLon = -122.2,
            RangeNm = 99,
            ShowRangeRings = true,
            RangeRingSizeNm = 10,
            PtlLengthMinutes = 2,
            PtlOwn = true,
            HistoryCount = 5,
            ShowFixes = true,
            ShowTopDown = true,
            IsPanZoomLocked = true,
        };
        var target = new SavedRadarSettings();

        foreach (RadarCopyGroup group in ViewSettingsCopyCatalog.RadarGroups)
        {
            group.Copy(source, target);
        }

        foreach (RadarCopyGroup group in ViewSettingsCopyCatalog.RadarGroups)
        {
            Assert.True(group.AreEqual(source, target), $"radar group '{group.Key}' not equal after full copy");
        }
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
                ViewSettingsCopyCatalog.LayoutRadarKey,
                ViewSettingsCopyCatalog.LayoutControllersKey,
                ViewSettingsCopyCatalog.LayoutMetarKey,
                ViewSettingsCopyCatalog.LayoutExtraRadarKey,
                ViewSettingsCopyCatalog.LayoutExtraGroundKey,
                ViewSettingsCopyCatalog.LayoutFavoriteSetsKey,
                ViewSettingsCopyCatalog.LayoutFavoritesBarKey,
                ViewSettingsCopyCatalog.LayoutFavoritesPanelKey,
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
    public void ExtraViewRows_DescribeOrdinalAndAirport_AndDiffEachKind()
    {
        var current = new SavedLayout { ExtraRadarViews = [new SavedExtraView(2, "KOAK")] };
        var source = new SavedLayout
        {
            ExtraRadarViews = [new SavedExtraView(2, "KOAK")],
            ExtraGroundViews = [new SavedExtraView(2, "KSFO"), new SavedExtraView(3, "KOAK")],
        };
        LayoutCopyGroup radar = LayoutGroup(ViewSettingsCopyCatalog.LayoutExtraRadarKey);
        LayoutCopyGroup ground = LayoutGroup(ViewSettingsCopyCatalog.LayoutExtraGroundKey);

        Assert.Equal("#2 KOAK", radar.Describe(source));
        Assert.Equal("#2 KSFO, #3 KOAK", ground.Describe(source));
        Assert.Equal("none", ground.Describe(current));
        Assert.True(radar.AreEqual(current, source));
        Assert.False(ground.AreEqual(current, source));
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

    private static RadarCopyGroup RadarGroup(string key) => ViewSettingsCopyCatalog.RadarGroups.Single(g => g.Key == key);

    private static GroundCopyGroup GroundGroup(string key) => ViewSettingsCopyCatalog.GroundGroups.Single(g => g.Key == key);
}
