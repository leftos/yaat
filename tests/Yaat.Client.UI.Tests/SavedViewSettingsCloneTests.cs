using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

public class SavedViewSettingsCloneTests
{
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

        SavedGroundSettings clone = original.Clone();
        clone.Zoom = 9;
        clone.ShowParking = GroundFilterMode.Off;
        clone.ShowRunwayLabels = false;

        Assert.Equal(3, original.Zoom);
        Assert.Equal(120, original.Rotation);
        Assert.Equal(GroundFilterMode.IconsOnly, original.ShowParking);
        Assert.True(original.ShowRunwayLabels);
    }
}
