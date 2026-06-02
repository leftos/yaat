using System.Collections.Generic;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

public class SavedViewSettingsCloneTests
{
    [Fact]
    public void RadarClone_IsIndependentDeepCopy()
    {
        var original = new SavedRadarSettings
        {
            EnabledStarsIds = [1, 2, 3],
            RangeNm = 55,
            BrightnessValues = new Dictionary<string, int> { ["MapA"] = 80 },
            HistoryCount = 4,
            PtlOwn = true,
        };

        var clone = original.Clone();
        clone.EnabledStarsIds.Add(99);
        clone.BrightnessValues!["MapA"] = 10;
        clone.RangeNm = 12;
        clone.PtlOwn = false;

        Assert.Equal(new[] { 1, 2, 3 }, original.EnabledStarsIds);
        Assert.Equal(80, original.BrightnessValues!["MapA"]);
        Assert.Equal(55, original.RangeNm);
        Assert.True(original.PtlOwn);
    }

    [Fact]
    public void RadarClone_NullBrightness_StaysNull()
    {
        var original = new SavedRadarSettings { BrightnessValues = null };

        var clone = original.Clone();

        Assert.Null(clone.BrightnessValues);
    }

    [Fact]
    public void GroundClone_IsIndependentCopy()
    {
        var original = new SavedGroundSettings
        {
            Zoom = 3,
            Rotation = 120,
            ShowParking = GroundFilterMode.IconsOnly,
            ShowRunwayLabels = true,
        };

        var clone = original.Clone();
        clone.Zoom = 9;
        clone.ShowParking = GroundFilterMode.Off;
        clone.ShowRunwayLabels = false;

        Assert.Equal(3, original.Zoom);
        Assert.Equal(120, original.Rotation);
        Assert.Equal(GroundFilterMode.IconsOnly, original.ShowParking);
        Assert.True(original.ShowRunwayLabels);
    }
}
