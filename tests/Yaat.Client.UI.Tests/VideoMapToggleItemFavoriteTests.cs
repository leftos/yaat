using System.ComponentModel;
using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests;

public class VideoMapToggleItemFavoriteTests
{
    private static VideoMapToggleItem MakeItem() =>
        new()
        {
            MapId = "map-1",
            ShortName = "SFO FINALS",
            Name = "SFO Final Approach",
            BrightnessCategory = "A",
            StarsId = 12,
        };

    [Fact]
    public void IsFavorite_FalseWhenNoScopeFavorited()
    {
        var item = MakeItem();

        Assert.False(item.IsFavorite);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void IsFavorite_TrueWhenAnyScopeFavorited(bool artcc, bool airport, bool scenario)
    {
        var item = MakeItem();
        item.IsFavoriteArtcc = artcc;
        item.IsFavoriteAirport = airport;
        item.IsFavoriteScenario = scenario;

        Assert.True(item.IsFavorite);
    }

    [Fact]
    public void IsFavorite_RaisesChangeNotification_WhenScopeFlagChanges()
    {
        var item = MakeItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VideoMapToggleItem.IsFavorite))
            {
                raised = true;
            }
        };

        item.IsFavoriteAirport = true;

        Assert.True(raised);
    }
}
