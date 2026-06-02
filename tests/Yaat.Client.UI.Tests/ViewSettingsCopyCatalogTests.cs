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
        var maps = RadarGroup(ViewSettingsCopyCatalog.RadarMapsKey);

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
        var maps = RadarGroup(ViewSettingsCopyCatalog.RadarMapsKey);

        maps.Copy(source, target);
        target.EnabledStarsIds.Add(9);

        Assert.Equal(new[] { 7, 8 }, source.EnabledStarsIds);
    }

    [Fact]
    public void RadarCenterGroup_AreEqual_DetectsRangeDifference()
    {
        var center = RadarGroup(ViewSettingsCopyCatalog.RadarCenterKey);
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
        var filters = GroundGroup("ground.filters");

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

        foreach (var group in ViewSettingsCopyCatalog.RadarGroups)
        {
            group.Copy(source, target);
        }

        foreach (var group in ViewSettingsCopyCatalog.RadarGroups)
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
        };
        var target = new SavedGroundSettings();

        foreach (var group in ViewSettingsCopyCatalog.GroundGroups)
        {
            group.Copy(source, target);
        }

        foreach (var group in ViewSettingsCopyCatalog.GroundGroups)
        {
            Assert.True(group.AreEqual(source, target), $"ground group '{group.Key}' not equal after full copy");
        }
    }

    private static RadarCopyGroup RadarGroup(string key) => ViewSettingsCopyCatalog.RadarGroups.Single(g => g.Key == key);

    private static GroundCopyGroup GroundGroup(string key) => ViewSettingsCopyCatalog.GroundGroups.Single(g => g.Key == key);
}
